using System.Numerics;
using System.Runtime.InteropServices;
using EarTailPhysics;

unsafe partial class Program
{
    static void TestProfiles()
    {
        var legacy = new Settings { Version = 1, ContactRadius = .08f }.Snapshot();
        Assert(legacy.Version == 2 && legacy.ContactNodeRadii?.Length == 5, "Migrate five radii");
        Near(legacy.GetContactRadius(0), 0, "Root protected by default");
        Near(legacy.GetContactRadius(1), 0, "Proximal protected by default");
        Near(legacy.GetContactRadius(2), .08f, "Legacy middle unchanged");
        Near(legacy.GetContactRadius(3), .056f, "Legacy rear unchanged");
        Near(legacy.GetContactRadius(4), .036f, "Legacy tip unchanged");
        var edited = legacy.Snapshot();
        edited.ContactNodeRadii![2] = .22f;
        edited.ProtectRoot = false;
        edited.ProtectProximal = false;
        edited.ShowCollisionPreview = true;
        Near(legacy.GetContactRadius(2), .08f, "Settings snapshots do not share profile arrays");
        Near(edited.GetContactRadius(0), .044f, "Unprotected root retains configured radius");
        Near(edited.GetContactRadius(1), .068f, "Unprotected proximal retains configured radius");
        Assert(legacy.SameNativeConfiguration(edited), "Radius, protection and preview avoid native rebuilds");
        edited.EarGrade++;
        Assert(!legacy.SameNativeConfiguration(edited), "Ear preset change rebuilds");
        var bounds = new Settings { ContactNodeRadii = [float.NaN, float.PositiveInfinity, -1, .5f, 0] }.Snapshot();
        Near(bounds.GetConfiguredRadius(0), .045f * .55f, "Nonfinite root falls back");
        Near(bounds.GetConfiguredRadius(1), .045f * .85f, "Nonfinite proximal falls back");
        Near(bounds.GetConfiguredRadius(2), 0, "Negative radius clamps to zero");
        Near(bounds.GetConfiguredRadius(3), .30f, "Large radius clamps");
        Near(bounds.GetConfiguredRadius(4), 0, "Zero stays disabled");
    }

    static void TestNodeContacts()
    {
        using var mem = new NativeMemoryView();
        var module = mem.Allocate(0x590);
        *(Matrix4x4*)(module + 0x10) = Matrix4x4.Identity;
        var chain = mem.Allocate(0x180);
        var particles = mem.Allocate(5 * 96);
        *(ushort*)(chain + 0xE8) = 5;
        *(byte**)(chain + 0x20) = particles;
        var radii = new float[] { .03f, .04f, .05f, .06f, .07f };
        var observed = new List<SweepShape>();
        SceneContacts.RayDelegate ray = (nint _, SceneContacts.Hit* hit, Vector3* origin, Vector3* direction, float distance, int layers, int* flags) =>
        {
            hit->Point = new(origin->X, 0, origin->Z);
            hit->Normal = Vector3.UnitY;
            hit->Distance = origin->Y;
            return hit->Distance >= 0 && hit->Distance <= distance ? (byte)1 : (byte)0;
        };
        SceneContacts.SweepDelegate sweep = (nint _, SceneContacts.Hit* hit, Vector4* sphere, Vector3* direction, float distance, int layers, int* flags) =>
        {
            var start = new Vector3(sphere->X, sphere->Y, sphere->Z);
            observed.Add(new(start, start + *direction * distance, sphere->W, -1, true));
            return 0;
        };
        var contacts = new SceneContacts(Marshal.GetFunctionPointerForDelegate(ray), Marshal.GetFunctionPointerForDelegate(sweep));
        for (var protectedMask = 0; protectedMask < 4; protectedMask++)
        {
            for (var i = 0; i < 5; i++)
            {
                var p = new Vector3(i * .25f, -.02f, 0);
                *(Vector3*)(particles + i * 96) = p;
                *(Vector3*)(particles + i * 96 + 0x40) = p;
                *(Vector3*)(particles + i * 96 + 0x20) = new(.02f, -.03f, .01f);
            }
            var cfg = new Settings { GroundCollision = true, ContactNodeRadii = radii,
                ProtectRoot = (protectedMask & 1) != 0, ProtectProximal = (protectedMask & 2) != 0 }.Snapshot();
            var state = new SceneContacts.State((nint)module);
            Assert(contacts.Resolve(chain, state, cfg, 1), "Each root protection combination resolves");
            var preview = SceneContacts.CapturePreview(chain, state, cfg, true)!;
            Assert(preview.Active && preview.Nodes.Length == 5, "Capture all five contact nodes");
            for (var i = 0; i < 5; i++)
            {
                var p = *(Vector3*)(particles + i * 96);
                if (cfg.IsProtected(i)) Near(p.Y, -.02f, "Protected node is never written");
                else
                {
                    Near(p.Y, radii[i], "Per-node ground clearance uses its own radius");
                    Near((*(Vector3*)(particles + i * 96 + 0x20)).Y, 0, "No rebound at unprotected node");
                }
                Near(preview.Nodes[i].Radius, cfg.GetContactRadius(i), "Preview uses actual effective radius");
                Assert(preview.Nodes[i].Protected == cfg.IsProtected(i), "Preview protection matches solver");
            }
        }

        // Static straight chain: each incoming-link sweep carries the receiving node's radius.
        for (var i = 0; i < 5; i++)
        {
            var p = new Vector3(i * .25f, 1, 0);
            *(Vector3*)(particles + i * 96) = p;
            *(Vector3*)(particles + i * 96 + 0x40) = p;
        }
        var scene = new Settings { SceneCollision = true, ContactNodeRadii = radii, ProtectRoot = false, ProtectProximal = false }.Snapshot();
        var sceneState = new SceneContacts.State((nint)module);
        observed.Clear();
        Assert(!contacts.Resolve(chain, sceneState, scene, 1), "Empty scene has no contact");
        Assert(observed.Count == 12, "Three spatial passes cover four unprotected links");
        for (var i = 0; i < observed.Count; i++) Near(observed[i].Radius, radii[i % 4 + 1], "Actual sweep radius follows node");
        var actualPreview = SceneContacts.CapturePreview(chain, sceneState, scene, true)!;
        Assert(actualPreview.Sweeps.Length == 4, "Preview includes four observed link sweeps");
        foreach (var shape in actualPreview.Sweeps)
            Assert(observed.Any(q => q.Start == shape.Start && q.End == shape.End && q.Radius == shape.Radius), "Wire shape matches an actual query");
        for (var i = 0; i < 5; i++) *(Vector3*)(particles + i * 96) += new Vector3(0, .01f, 0);
        observed.Clear();
        contacts.Resolve(chain, sceneState, scene, 1);
        Assert(observed.Any(q => MathF.Abs(q.Radius - radii[0]) < .000001f), "Unprotected root performs temporal sphere sweep");

        scene.ContactNodeRadii = [0, 0, 0, 0, 0];
        var before = new ReadOnlySpan<byte>(particles, 5 * 96).ToArray();
        var queryCount = contacts.Queries;
        Assert(!contacts.Resolve(chain, sceneState, scene, 1) && contacts.Queries == queryCount, "All zero radii skip all scene queries");
        Assert(!sceneState.Initialized, "Re-enabling all-zero profile does not sweep from stale history");
        Assert(new ReadOnlySpan<byte>(particles, 5 * 96).SequenceEqual(before), "All zero radii preserve particles");
        var inactive = SceneContacts.CapturePreview(chain, sceneState, new Settings().Snapshot(), true)!;
        Assert(!inactive.Active && inactive.Sweeps.Length == 3 && inactive.Sweeps.ToArray().All(s => !s.Observed), "Disabled collisions show a clearly inactive profile");
        var captured = inactive.Nodes[2].Position;
        *(Vector3*)(particles + 2 * 96) = new Vector3(9, 9, 9);
        Assert(inactive.Nodes[2].Position == captured, "Preview never reads live memory after capture");
        GC.KeepAlive(ray); GC.KeepAlive(sweep);
    }

    static void TestPreviewGeometry()
    {
        var center = new Vector3(2, 3, 4);
        var sphere = ColliderWireGeometry.Sphere(center, .12f).ToArray();
        Assert(sphere.Length == 72, "Three complete sphere rings");
        foreach (var line in sphere)
        { Near(Vector3.Distance(line.Start, center), .12f, "Sphere wire lies on radius"); Near(Vector3.Distance(line.End, center), .12f, "Sphere endpoints lie on radius"); }
        foreach (var delta in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(new(1, 2, 3)) })
        {
            var capsule = new SweepShape(center, center + delta, .12f, 2, true);
            foreach (var line in ColliderWireGeometry.Capsule(capsule))
                foreach (var p in new[] { line.Start, line.End })
                {
                    var t = Math.Clamp(Vector3.Dot(p - center, delta), 0, 1);
                    Near(Vector3.Distance(p, center + t * delta), .12f, "Capsule wire equals swept sphere boundary");
                }
        }
        Assert(!ColliderWireGeometry.Sphere(center, 0).Any(), "Zero radius draws no collider");
        Assert(!ColliderWireGeometry.Capsule(new(center, center, float.NaN, 2, false)).Any(), "Invalid radius not drawn");
        var nodes = new NodeCollider[] { new(center, .1f, false, false) };
        var shapes = new SweepShape[] { new(center, center + Vector3.UnitY, .1f, 0, true) };
        var snapshot = new CollisionPreview(nodes, shapes, true);
        nodes[0] = default; shapes[0] = default;
        Assert(snapshot.Nodes[0].Position == center && snapshot.Sweeps[0].Radius == .1f, "Preview owns its arrays");
    }
}
