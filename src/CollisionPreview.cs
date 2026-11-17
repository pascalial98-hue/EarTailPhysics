using System.Diagnostics;
using System.Numerics;

namespace EarTailPhysics;

public readonly record struct NodeCollider(Vector3 Position, float Radius, bool Touching, bool Protected);
public readonly record struct SweepShape(Vector3 Start, Vector3 End, float Radius, int NodeIndex, bool Observed);
public readonly record struct WireLine(Vector3 Start, Vector3 End);

// Owned immutable snapshot; the drawing thread never reads live game pointers or mutable solver arrays.
public sealed class CollisionPreview
{
    private readonly NodeCollider[] nodes;
    private readonly SweepShape[] sweeps;
    public ReadOnlySpan<NodeCollider> Nodes => nodes;
    public ReadOnlySpan<SweepShape> Sweeps => sweeps;
    public bool Active { get; }
    public long CapturedAt { get; } = Stopwatch.GetTimestamp();
    public bool Fresh => Stopwatch.GetElapsedTime(CapturedAt).TotalSeconds < .5;
    public CollisionPreview(NodeCollider[] nodes, SweepShape[] sweeps, bool active)
    {
        this.nodes = (NodeCollider[])nodes.Clone();
        this.sweeps = (SweepShape[])sweeps.Clone();
        Active = active;
    }
}

public static class ColliderWireGeometry
{
    public const int Segments = 24;

    public static IEnumerable<WireLine> Sphere(Vector3 center, float radius)
    {
        if (!ContactMath.Finite(center) || !float.IsFinite(radius) || radius <= 0) yield break;
        foreach (var line in Ring(center, Vector3.UnitX, Vector3.UnitY, radius)) yield return line;
        foreach (var line in Ring(center, Vector3.UnitX, Vector3.UnitZ, radius)) yield return line;
        foreach (var line in Ring(center, Vector3.UnitY, Vector3.UnitZ, radius)) yield return line;
    }

    public static IEnumerable<WireLine> Capsule(SweepShape shape)
    {
        if (!ContactMath.Finite(shape.Start) || !ContactMath.Finite(shape.End) || !float.IsFinite(shape.Radius) || shape.Radius <= 0) yield break;
        var delta = shape.End - shape.Start;
        if (delta.LengthSquared() < 1e-8f)
        {
            foreach (var line in Sphere(shape.End, shape.Radius)) yield return line;
            yield break;
        }
        var axis = Vector3.Normalize(delta);
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) < .9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(axis, u);
        foreach (var line in Ring(shape.Start, u, v, shape.Radius)) yield return line;
        foreach (var line in Ring(shape.End, u, v, shape.Radius)) yield return line;
        foreach (var side in new[] { u, -u, v, -v })
            yield return new(shape.Start + side * shape.Radius, shape.End + side * shape.Radius);
        foreach (var radial in new[] { u, v })
        {
            for (var i = 0; i < Segments / 2; i++)
            {
                var a = MathF.PI * i / (Segments / 2);
                var b = MathF.PI * (i + 1) / (Segments / 2);
                yield return new(shape.Start + (radial * MathF.Cos(a) - axis * MathF.Sin(a)) * shape.Radius,
                    shape.Start + (radial * MathF.Cos(b) - axis * MathF.Sin(b)) * shape.Radius);
                yield return new(shape.End + (radial * MathF.Cos(a) + axis * MathF.Sin(a)) * shape.Radius,
                    shape.End + (radial * MathF.Cos(b) + axis * MathF.Sin(b)) * shape.Radius);
            }
        }
    }

    private static IEnumerable<WireLine> Ring(Vector3 center, Vector3 u, Vector3 v, float radius)
    {
        for (var i = 0; i < Segments; i++)
        {
            var a = MathF.Tau * i / Segments;
            var b = MathF.Tau * (i + 1) / Segments;
            yield return new(center + radius * (u * MathF.Cos(a) + v * MathF.Sin(a)),
                center + radius * (u * MathF.Cos(b) + v * MathF.Sin(b)));
        }
    }
}
