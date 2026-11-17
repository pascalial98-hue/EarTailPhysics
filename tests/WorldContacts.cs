using System.Numerics;
using System.Runtime.InteropServices;
using EarTailPhysics;

unsafe partial class Program
{
    static void TestWorldContacts()
    {
        using var mem = new NativeMemoryView();
        var module = mem.Allocate(0x590);
        var chain = mem.Allocate(0x180);
        var particles = mem.Allocate(5 * 96);
        *(ushort*)(chain + 0xE8) = 5;
        *(byte**)(chain + 0x20) = particles;

        // A model-space horizontal chain can be vertical after the actor transform.
        // Explicit expected coordinates catch a missing rotation, scale or transpose.
        var hanging = Matrix4x4.CreateScale(.7f) * Matrix4x4.CreateRotationZ(-MathF.PI / 2)
            * Matrix4x4.CreateTranslation(10, 2, -3);
        hanging.M14 = float.NaN; hanging.M24 = 123; hanging.M34 = float.PositiveInfinity; hanging.M44 = -27;
        *(Matrix4x4*)(module + 0x10) = hanging;
        Assert(PhysicsSpace.TryRead((nint)module, out var hangingSpace), "Native padding lanes are not affine matrix data");
        for (var i = 0; i < 5; i++) *(Vector3*)(particles + i * 96) = new(i * .25f, 0, 0);
        var preview = SceneContacts.CapturePreview(chain, new((nint)module), new Settings().Snapshot(), true)!;
        for (var i = 0; i < 5; i++)
        {
            NearVector(preview.Nodes[i].Position, new(10, 2 - i * .175f, -3), "Preview follows full actor transform");
            NearVector(hangingSpace.ToLocal(preview.Nodes[i].Position), new(i * .25f, 0, 0), "World correction round trip");
        }
        var nativeNodes = mem.Allocate(4 * 160);
        *(byte**)(chain + 0x28) = nativeNodes;
        for (var i = 0; i < 4; i++) *(Vector3*)(nativeNodes + i * 160 + 0x70) = new(i * .25f, .1f, 0);
        var offsetState = new SceneContacts.State((nint)module);
        var offsetPreview = SceneContacts.CapturePreview(chain, offsetState, new Settings().Snapshot(), true)!;
        Near(offsetState.PoseOffset, .07f, "Measure native animation blend discrepancy in world metres");
        NearVector(offsetPreview.Nodes[2].Position, preview.Nodes[2].Position, "Animation diagnostic never disguises actual query positions");
        *(byte**)(chain + 0x28) = null;
        *(Matrix4x4*)(module + 0x10) = default;
        Assert(!PhysicsSpace.TryRead((nint)module, out _), "Reject singular model transform");
        Assert(SceneContacts.CapturePreview(chain, new((nint)module), new Settings(), true) == null, "Invalid transform hides preview");
        hanging.M11 = float.NaN;
        *(Matrix4x4*)(module + 0x10) = hanging;
        Assert(!PhysicsSpace.TryRead((nint)module, out _), "Reject nonfinite affine component");

        const float wall = 10.6f;
        var originWorld = new Vector3(10, 1, -2);
        var observed = new List<SweepShape>();
        int normalMode = 0; // 0: native normal, 1: triangle only, 2: invalid geometry, 3: invalid distance
        SceneContacts.RayDelegate ray = (nint _, SceneContacts.Hit* hit, Vector3* origin, Vector3* direction, float distance, int layers, int* flags) =>
        {
            Assert(layers == 1 && flags[0] == 0x4000 && flags[2] == 0x4000, "Preserve native material filters");
            hit->Point = new(origin->X, 1, origin->Z);
            hit->Distance = origin->Y - 1;
            hit->Normal = normalMode == 0 ? Vector3.UnitY : Vector3.Zero;
            hit->V1 = new(9, 1, -3); hit->V2 = new(11, 1, -3); hit->V3 = new(10, 1, -1);
            return hit->Distance >= 0 && hit->Distance <= distance ? (byte)1 : (byte)0;
        };
        SceneContacts.SweepDelegate sweep = (nint _, SceneContacts.Hit* hit, Vector4* sphere, Vector3* direction, float distance, int layers, int* flags) =>
        {
            var start = new Vector3(sphere->X, sphere->Y, sphere->Z);
            observed.Add(new(start, start + *direction * distance, sphere->W, -1, true));
            Assert(start.X > 9 && start.X < 12 && start.Z < -1 && start.Z > -3, "Wall queries use known world location");
            if (direction->X <= .00001f) return 0;
            var travel = (wall - sphere->W - start.X) / direction->X;
            if (travel < 0 || travel > distance) return 0;
            hit->Distance = normalMode == 3 ? float.NaN : travel;
            hit->Point = start + *direction * travel + Vector3.UnitX * sphere->W;
            hit->Normal = normalMode == 0 ? -Vector3.UnitX : Vector3.Zero;
            if (normalMode != 2)
            {
                // Deliberately +X winding: fallback must oppose the incoming sweep.
                hit->V1 = new(wall, 1, -2); hit->V2 = new(wall, 1.01f, -2); hit->V3 = new(wall, 1, -1.99f);
            }
            return 1;
        };
        var contacts = new SceneContacts(Marshal.GetFunctionPointerForDelegate(ray), Marshal.GetFunctionPointerForDelegate(sweep));
        var transforms = new[]
        {
            Matrix4x4.CreateTranslation(originWorld),
            Matrix4x4.CreateScale(.68455f) * Matrix4x4.CreateRotationY(2.73f) * Matrix4x4.CreateTranslation(originWorld),
            Matrix4x4.CreateScale(.6f, 1.3f, .8f) * Matrix4x4.CreateRotationZ(-1.1f)
                * Matrix4x4.CreateRotationY(.7f) * Matrix4x4.CreateTranslation(originWorld)
        };
        var cfg = new Settings { SceneCollision = true, ContactNodeRadii = [.05f, .05f, .05f, .05f, .05f] }.Snapshot();
        foreach (var world in transforms)
        {
            *(Matrix4x4*)(module + 0x10) = world;
            Matrix4x4.Invert(world, out var inverse);
            for (normalMode = 0; normalMode < 4; normalMode++)
            {
                for (var i = 0; i < 5; i++)
                {
                    var p = Vector3.Transform(originWorld + new Vector3(i * .2f, 0, 0), inverse);
                    *(Vector3*)(particles + i * 96) = p;
                    *(Vector3*)(particles + i * 96 + 0x40) = p;
                    *(Vector3*)(particles + i * 96 + 0x20) = Vector3.TransformNormal(new(.03f, .02f, 0), inverse);
                }
                var state = new SceneContacts.State((nint)module);
                var before = new ReadOnlySpan<byte>(particles, 5 * 96).ToArray();
                var invalidBefore = contacts.InvalidHits;
                var fallbackBefore = contacts.TriangleNormals;
                var hitsBefore = contacts.SceneHits;
                var adjusted = contacts.Resolve(chain, state, cfg, 1);
                Assert(contacts.SceneHits > hitsBefore, "Real world-plane sweep returns positive native hit");
                if (normalMode >= 2)
                {
                    Assert(!adjusted && contacts.InvalidHits > invalidBefore, "Reject malformed collision result");
                    Assert(new ReadOnlySpan<byte>(particles, 5 * 96).SequenceEqual(before), "Invalid hit never changes particles");
                    continue;
                }
                Assert(adjusted && state.ContactMask != 0, "Static wall intersection projects chain");
                Assert(normalMode == 0 || contacts.TriangleNormals > fallbackBefore, "Triangle supplies missing native normal");
                var actual = SceneContacts.CapturePreview(chain, state, cfg, true)!;
                for (var i = 0; i < 5; i++)
                {
                    var p = Vector3.Transform(*(Vector3*)(particles + i * 96), world);
                    NearVector(actual.Nodes[i].Position, p, "Preview matches corrected particles in world space");
                    NearVector(state.Previous[i], p, "Temporal history retains world position");
                    if (cfg.IsProtected(i)) NearVector(p, originWorld + new Vector3(i * .2f, 0, 0), "World conversion preserves protected nodes");
                    else Assert(p.X <= wall - .05f + .0001f, "World wall clearance is configured radius in metres");
                    if ((state.ContactMask & (1u << i)) == 0) continue;
                    var velocity = Vector3.TransformNormal(*(Vector3*)(particles + i * 96 + 0x20), world);
                    Near(velocity.X, 0, "Wall removes world-normal speed under actor rotation and scale");
                    Near(velocity.Y, .018f, "Wall preserves tangential velocity with existing damping");
                }
            }
            for (normalMode = 0; normalMode < 2; normalMode++)
            {
                for (var i = 0; i < 5; i++)
                {
                    var p = Vector3.Transform(originWorld + new Vector3(i * .2f, -.03f, 0), inverse);
                    *(Vector3*)(particles + i * 96) = p;
                    *(Vector3*)(particles + i * 96 + 0x40) = p;
                    *(Vector3*)(particles + i * 96 + 0x20) = Vector3.TransformNormal(new(.02f, -.03f, 0), inverse);
                }
                var floor = new Settings { GroundCollision = true, ContactNodeRadii = [.03f, .04f, .05f, .06f, .07f] }.Snapshot();
                var state = new SceneContacts.State((nint)module);
                Assert(contacts.Resolve(chain, state, floor, 1), "World floor works with transformed actor and either normal source");
                for (var i = 2; i < 5; i++)
                {
                    var p = Vector3.Transform(*(Vector3*)(particles + i * 96), world);
                    Near(p.Y, 1 + floor.GetContactRadius(i), "Floor uses world-space per-node radius");
                    var v = Vector3.TransformNormal(*(Vector3*)(particles + i * 96 + 0x20), world);
                    Near(v.Y, 0, "Floor removes downward speed without rebound after local writeback");
                }
            }
        }
        Assert(contacts.GroundHits > 0 && contacts.Contacts > 0, "Expose native hits separately from resolved contacts");
        *(Matrix4x4*)(module + 0x10) = default;
        var count = contacts.Queries;
        Assert(!contacts.Resolve(chain, new((nint)module), cfg, 1) && contacts.Queries == count, "Invalid transform cannot issue scene queries");
        GC.KeepAlive(ray); GC.KeepAlive(sweep);
    }

    static void NearVector(Vector3 actual, Vector3 expected, string description)
        => Assert(Vector3.Distance(actual, expected) < .0001f, $"{description}: {actual} != {expected}");
}
