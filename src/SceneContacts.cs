using System.Numerics;
using System.Runtime.InteropServices;

namespace EarTailPhysics;

public unsafe sealed class SceneContacts
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte RayDelegate(nint module, Hit* hit, Vector3* origin, Vector3* direction, float distance, int layers, int* flags);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte SweepDelegate(nint module, Hit* hit, Vector4* sphere, Vector3* direction, float distance, int layers, int* flags);
    [StructLayout(LayoutKind.Explicit, Size = 0x58)]
    public struct Hit
    {
        [FieldOffset(0)] public Vector3 Point;
        [FieldOffset(0x30)] public Vector3 Normal;
        [FieldOffset(0x48)] public float Distance;
    }
    public sealed class State(nint module)
    {
        public readonly nint Module = module;
        public readonly Vector3[] Previous = new Vector3[5];
        public readonly Vector3[] Points = new Vector3[5];
        public readonly Vector3[] Normals = new Vector3[5];
        public readonly float[] Lengths = new float[4];
        public Vector3 Root;
        public bool Initialized;
    }
    private readonly RayDelegate ray;
    private readonly SweepDelegate sweep;
    public long Queries, Contacts;
    public SceneContacts(nint rayAddress, nint sweepAddress)
    {
        ray = Marshal.GetDelegateForFunctionPointer<RayDelegate>(rayAddress);
        sweep = Marshal.GetDelegateForFunctionPointer<SweepDelegate>(sweepAddress);
    }

    public bool Resolve(byte* chain, State state, Settings cfg, nint bgModule)
    {
        if (bgModule == 0 || (!cfg.GroundCollision && !cfg.SceneCollision)) return false;
        if (*(ushort*)(chain + 0xE8) != 5) return false;
        var particles = *(byte**)(chain + 0x20);
        if (particles == null) return false;
        // Native particle targets are world-oriented, relative to the current skeleton translation.
        var root = *(Vector3*)((byte*)state.Module + 0x40);
        if (!ContactMath.Finite(root)) return false;
        var reset = !state.Initialized || Vector3.DistanceSquared(root, state.Root) > 4;
        for (var i = 0; i < 5; i++)
        {
            var p = *(Vector3*)(particles + i * 96) + root;
            if (!ContactMath.Finite(p) || Vector3.DistanceSquared(p, root) > 100) return false;
            state.Points[i] = p;
            state.Normals[i] = Vector3.Zero;
            if (reset) state.Previous[i] = p;
            if (i > 0)
            {
                var t = *(Vector3*)(particles + i * 96 + 0x40);
                var parent = *(Vector3*)(particles + (i - 1) * 96 + 0x40);
                state.Lengths[i - 1] = Vector3.Distance(t, parent);
                if (state.Lengths[i - 1] is < .0001f or > 3) return false;
            }
        }
        int* flags = stackalloc int[] { 0x4000, 0, 0x4000, 0 };
        var any = false;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            ContactMath.KeepLengths(state.Points, state.Lengths, 2);
            for (var i = 2; i < 5; i++)
            {
                var radius = cfg.ContactRadius * (i == 2 ? 1 : i == 3 ? .7f : .45f);
                ref var next = ref state.Points[i];
                if (cfg.SceneCollision)
                {
                    // Temporal sweep prevents tunnelling; link sweep catches static wall intersections.
                    var start = iteration == 0 ? state.Previous[i] : state.Points[i - 1];
                    var direction = next - start;
                    var distance = direction.Length();
                    if (distance > .0001f && distance < 3)
                    {
                        direction /= distance;
                        var sphere = new Vector4(start, radius);
                        Hit hit = default;
                        Interlocked.Increment(ref Queries);
                        if (sweep(bgModule, &hit, &sphere, &direction, distance, 1, flags) != 0
                            && ContactMath.Finite(hit.Normal) && hit.Normal.LengthSquared() > .1f
                            && float.IsFinite(hit.Distance) && hit.Distance >= 0 && hit.Distance <= distance
                            && (cfg.GroundCollision || hit.Normal.Y < .25f))
                        {
                            next = start + direction * MathF.Max(0, hit.Distance - .002f);
                            state.Normals[i] = Vector3.Normalize(hit.Normal);
                            any = true;
                        }
                    }
                }
                if (cfg.GroundCollision)
                {
                    var origin = next + Vector3.UnitY * .6f;
                    var down = -Vector3.UnitY;
                    Hit hit = default;
                    Interlocked.Increment(ref Queries);
                    if (ray(bgModule, &hit, &origin, &down, 2.5f, 1, flags) != 0
                        && hit.Normal.Y > .25f && hit.Point.Y <= next.Y + .35f
                        && ContactMath.ProjectPlane(ref next, hit.Point, hit.Normal, radius))
                    {
                        state.Normals[i] = Vector3.Normalize(hit.Normal);
                        any = true;
                    }
                }
            }
        }
        if (any)
        {
            // Contact wins over tiny length residuals; native bone writeback preserves bone translations.
            for (var i = 2; i < 5; i++)
            {
                var relative = state.Points[i] - root;
                if (!ContactMath.Finite(relative)) return false;
                *(Vector3*)(particles + i * 96) = relative;
                var normal = state.Normals[i];
                if (normal.LengthSquared() > .1f)
                {
                    var velocity = (Vector3*)(particles + i * 96 + 0x20);
                    *velocity = ContactMath.RemoveInwardVelocity(*velocity, normal) * .9f;
                }
            }
            Interlocked.Increment(ref Contacts);
        }
        Array.Copy(state.Points, state.Previous, 5);
        state.Root = root; state.Initialized = true;
        return any;
    }
}
