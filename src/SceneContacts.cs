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
        public readonly SweepShape?[] SpatialSweeps = new SweepShape?[5];
        public bool ContactAdjusted;
        public uint ContactMask;
        public Settings? FrameSettings;
        public Vector3 Root;
        public bool Initialized;
        public string LastResult = "等待尾链物理回调";
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
        Array.Clear(state.SpatialSweeps);
        Array.Clear(state.Normals);
        if (!cfg.GroundCollision && !cfg.SceneCollision) { state.LastResult = "地面／场景碰撞未开启"; return false; }
        if (bgModule == 0) { state.LastResult = "等待场景碰撞模块"; return false; }
        if (cfg.GetContactRadius(0) == 0 && cfg.GetContactRadius(1) == 0 && cfg.GetContactRadius(2) == 0
            && cfg.GetContactRadius(3) == 0 && cfg.GetContactRadius(4) == 0)
        { state.Initialized = false; state.LastResult = "所有接触节点均为保护或半径 0"; return false; }
        if (*(ushort*)(chain + 0xE8) != 5) { state.LastResult = "尾链节点数不支持"; return false; }
        var particles = *(byte**)(chain + 0x20);
        if (particles == null) { state.LastResult = "等待尾链粒子初始化"; return false; }
        // Native particle targets are world-oriented, relative to the current skeleton translation.
        var root = *(Vector3*)((byte*)state.Module + 0x40);
        if (!ContactMath.Finite(root)) { state.LastResult = "角色位置无效，跳过接触"; return false; }
        var reset = !state.Initialized || Vector3.DistanceSquared(root, state.Root) > 4;
        for (var i = 0; i < 5; i++)
        {
            var p = *(Vector3*)(particles + i * 96) + root;
            if (!ContactMath.Finite(p) || Vector3.DistanceSquared(p, root) > 100)
            { state.LastResult = "尾节点位置超出有效范围"; return false; }
            state.Points[i] = p;
            state.Normals[i] = Vector3.Zero;
            if (reset) state.Previous[i] = p;
            if (i > 0)
            {
                var t = *(Vector3*)(particles + i * 96 + 0x40);
                var parent = *(Vector3*)(particles + (i - 1) * 96 + 0x40);
                state.Lengths[i - 1] = Vector3.Distance(t, parent);
                if (!float.IsFinite(state.Lengths[i - 1]) || state.Lengths[i - 1] is < .0001f or > 3)
                { state.LastResult = "尾骨段长度尚未有效初始化"; return false; }
            }
        }
        int* flags = stackalloc int[] { 0x4000, 0, 0x4000, 0 };
        var any = false;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            ContactMath.KeepLengths(state.Points, state.Lengths, cfg);
            for (var i = 0; i < 5; i++)
            {
                var radius = cfg.GetContactRadius(i);
                if (radius == 0) continue;
                ref var next = ref state.Points[i];
                if (cfg.SceneCollision)
                {
                    // Temporal sweep prevents tunnelling; link sweep catches static wall intersections.
                    var start = iteration == 0 || i == 0 ? state.Previous[i] : state.Points[i - 1];
                    var direction = next - start;
                    var distance = direction.Length();
                    if (distance > .0001f && distance < 3)
                    {
                        direction /= distance;
                        var sphere = new Vector4(start, radius);
                        if (iteration > 0 && i > 0) state.SpatialSweeps[i] = new(start, next, radius, i, true);
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
            // Explicitly unprotected roots may move. Protected nodes are never written here.
            for (var i = 0; i < 5; i++)
            {
                if (cfg.IsProtected(i)) continue;
                var relative = state.Points[i] - root;
                if (!ContactMath.Finite(relative)) return false;
                *(Vector3*)(particles + i * 96) = relative;
                var normal = state.Normals[i];
                if (normal.LengthSquared() > .1f)
                {
                    state.ContactMask |= 1u << i;
                    var velocity = (Vector3*)(particles + i * 96 + 0x20);
                    *velocity = ContactMath.RemoveInwardVelocity(*velocity, normal) * .9f;
                }
            }
            Interlocked.Increment(ref Contacts);
            state.ContactAdjusted = true;
        }
        // Length projections with no contact are not written to native particles. The next
        // temporal sweep must start at the actual particle position, not that unused candidate.
        for (var i = 0; i < 5; i++) state.Previous[i] = *(Vector3*)(particles + i * 96) + root;
        state.Root = root; state.Initialized = true;
        state.LastResult = any ? "已发生接触修正" : "正在检测，当前未发生接触修正";
        return any;
    }

    public static CollisionPreview? CapturePreview(byte* chain, State state, Settings cfg, bool sceneAvailable)
    {
        if (*(ushort*)(chain + 0xE8) != 5) return null;
        var particles = *(byte**)(chain + 0x20);
        if (particles == null) return null;
        var root = *(Vector3*)((byte*)state.Module + 0x40);
        if (!ContactMath.Finite(root)) return null;
        var nodes = new NodeCollider[5];
        var sweeps = new List<SweepShape>(4);
        var active = sceneAvailable && (cfg.GroundCollision || cfg.SceneCollision);
        for (var i = 0; i < 5; i++)
        {
            var position = *(Vector3*)(particles + i * 96) + root;
            if (!ContactMath.Finite(position) || Vector3.DistanceSquared(position, root) > 100) return null;
            nodes[i] = new(position, cfg.GetContactRadius(i), active && (state.ContactMask & (1u << i)) != 0, cfg.IsProtected(i));
            if (i < 1 || nodes[i].Radius == 0) continue;
            if (cfg.SceneCollision && sceneAvailable && state.SpatialSweeps[i] is { } observed) sweeps.Add(observed);
            else if (!active) sweeps.Add(new(nodes[i - 1].Position, position, nodes[i].Radius, i, false));
        }
        return new CollisionPreview(nodes, sweeps.ToArray(), active);
    }
}
