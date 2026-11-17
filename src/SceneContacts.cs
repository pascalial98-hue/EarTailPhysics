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
        [FieldOffset(0x0C)] public Vector3 V1;
        [FieldOffset(0x18)] public Vector3 V2;
        [FieldOffset(0x24)] public Vector3 V3;
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
        public float PoseOffset = float.NaN;
        public string LastResult = "等待尾链物理回调";
    }
    private readonly RayDelegate ray;
    private readonly SweepDelegate sweep;
    public long Queries, Contacts, SceneHits, GroundHits, InvalidHits, TriangleNormals;
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
        if (!PhysicsSpace.TryRead(state.Module, out var space))
        { state.Initialized = false; state.LastResult = "角色物理坐标变换无效，跳过接触"; return false; }
        var root = space.Origin;
        var reset = !state.Initialized || Vector3.DistanceSquared(root, state.Root) > 4;
        for (var i = 0; i < 5; i++)
        {
            var p = space.ToWorld(*(Vector3*)(particles + i * 96));
            if (!ContactMath.Finite(p) || Vector3.DistanceSquared(p, root) > 100)
            { state.LastResult = "尾节点位置超出有效范围"; return false; }
            state.Points[i] = p;
            state.Normals[i] = Vector3.Zero;
            if (reset) state.Previous[i] = p;
            if (i > 0)
            {
                var t = *(Vector3*)(particles + i * 96 + 0x40);
                var parent = *(Vector3*)(particles + (i - 1) * 96 + 0x40);
                state.Lengths[i - 1] = Vector3.Distance(space.ToWorld(t), space.ToWorld(parent));
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
                        if (sweep(bgModule, &hit, &sphere, &direction, distance, 1, flags) != 0)
                        {
                            Interlocked.Increment(ref SceneHits);
                            if (!ValidateHit(hit, direction, distance, out var normal))
                                Interlocked.Increment(ref InvalidHits);
                            else if (cfg.GroundCollision || normal.Y < .25f)
                            {
                                next = start + direction * MathF.Max(0, hit.Distance - .002f);
                                state.Normals[i] = normal;
                                any = true;
                            }
                        }
                    }
                }
                if (cfg.GroundCollision)
                {
                    var origin = next + Vector3.UnitY * .6f;
                    var down = -Vector3.UnitY;
                    Hit hit = default;
                    Interlocked.Increment(ref Queries);
                    if (ray(bgModule, &hit, &origin, &down, 2.5f, 1, flags) != 0)
                    {
                        Interlocked.Increment(ref GroundHits);
                        if (!ValidateHit(hit, down, 2.5f, out var normal))
                            Interlocked.Increment(ref InvalidHits);
                        else if (normal.Y > .25f && hit.Point.Y <= next.Y + .35f
                            && ContactMath.ProjectPlane(ref next, hit.Point, normal, radius))
                        {
                            state.Normals[i] = normal;
                            any = true;
                        }
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
                var relative = space.ToLocal(state.Points[i]);
                if (!ContactMath.Finite(relative)) return false;
                *(Vector3*)(particles + i * 96) = relative;
                var normal = state.Normals[i];
                if (normal.LengthSquared() > .1f)
                {
                    state.ContactMask |= 1u << i;
                    var velocity = (Vector3*)(particles + i * 96 + 0x20);
                    var worldVelocity = space.VelocityToWorld(*velocity);
                    *velocity = space.VelocityToLocal(ContactMath.RemoveInwardVelocity(worldVelocity, normal) * .9f);
                }
            }
            Interlocked.Increment(ref Contacts);
            state.ContactAdjusted = true;
        }
        // Length projections with no contact are not written to native particles. The next
        // temporal sweep must start at the actual particle position, not that unused candidate.
        for (var i = 0; i < 5; i++) state.Previous[i] = space.ToWorld(*(Vector3*)(particles + i * 96));
        state.Root = root; state.Initialized = true;
        state.LastResult = any ? "已发生接触修正" : "正在检测，当前未发生接触修正";
        return any;
    }

    public static CollisionPreview? CapturePreview(byte* chain, State state, Settings cfg, bool sceneAvailable)
    {
        if (*(ushort*)(chain + 0xE8) != 5) return null;
        var particles = *(byte**)(chain + 0x20);
        if (particles == null) return null;
        if (!PhysicsSpace.TryRead(state.Module, out var space)) return null;
        var root = space.Origin;
        var nodes = new NodeCollider[5];
        var nativeNodes = *(byte**)(chain + 0x28);
        state.PoseOffset = nativeNodes == null ? float.NaN : 0;
        var sweeps = new List<SweepShape>(4);
        var active = sceneAvailable && (cfg.GroundCollision || cfg.SceneCollision);
        for (var i = 0; i < 5; i++)
        {
            var position = space.ToWorld(*(Vector3*)(particles + i * 96));
            if (!ContactMath.Finite(position) || Vector3.DistanceSquared(position, root) > 100) return null;
            nodes[i] = new(position, cfg.GetContactRadius(i), active && (state.ContactMask & (1u << i)) != 0, cfg.IsProtected(i));
            // Four node matrices are finalized/blended; the fifth particle is a virtual tip.
            // Diagnose residual animation blending without moving the displayed query geometry.
            if (nativeNodes != null && i < 4)
            {
                var blended = space.ToWorld(*(Vector3*)(nativeNodes + i * 160 + 0x70));
                var offset = Vector3.Distance(position, blended);
                state.PoseOffset = float.IsFinite(offset) ? MathF.Max(state.PoseOffset, offset) : float.NaN;
            }
            if (i < 1 || nodes[i].Radius == 0) continue;
            if (cfg.SceneCollision && sceneAvailable && state.SpatialSweeps[i] is { } observed) sweeps.Add(observed);
            else if (!active) sweeps.Add(new(nodes[i - 1].Position, position, nodes[i].Radius, i, false));
        }
        return new CollisionPreview(nodes, sweeps.ToArray(), active);
    }

    private bool ValidateHit(Hit hit, Vector3 direction, float distance, out Vector3 normal)
    {
        normal = default;
        if (!float.IsFinite(hit.Distance) || hit.Distance < 0 || hit.Distance > distance) return false;
        normal = hit.Normal;
        // Native mesh queries may supply a triangle without the optional normal.
        // Use actual returned geometry, never invent a normal for an empty hit.
        if (!ContactMath.Finite(normal) || !float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() < .000001f)
        {
            if (!ContactMath.Finite(hit.V1) || !ContactMath.Finite(hit.V2) || !ContactMath.Finite(hit.V3)) return false;
            normal = Vector3.Cross(hit.V2 - hit.V1, hit.V3 - hit.V1);
            if (!ContactMath.Finite(normal) || !float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() < 1e-12f) return false;
            normal = Vector3.Normalize(normal);
            if (Vector3.Dot(normal, direction) > 0) normal = -normal;
            Interlocked.Increment(ref TriangleNormals);
        }
        else normal = Vector3.Normalize(normal);
        return true;
    }
}
