using System.Numerics;

namespace EarTailPhysics;

public static class ContactMath
{
    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static Vector3 RemoveInwardVelocity(Vector3 velocity, Vector3 normal)
        => velocity - normal * MathF.Min(0, Vector3.Dot(velocity, normal));

    public static bool ProjectPlane(ref Vector3 point, Vector3 surface, Vector3 normal, float radius)
    {
        if (!Finite(point) || !Finite(surface) || !Finite(normal) || normal.LengthSquared() < .1f) return false;
        normal = Vector3.Normalize(normal);
        var depth = radius - Vector3.Dot(point - surface, normal);
        if (depth <= 0 || depth > .5f) return false;
        point += normal * depth;
        return true;
    }

    public static void KeepLengths(Vector3[] points, float[] lengths, int firstDynamic)
    {
        // Forward projection leaves root and proximal nodes exactly unchanged.
        for (var i = firstDynamic; i < points.Length; i++)
        {
            var d = points[i] - points[i - 1];
            var len = d.Length();
            if (len > 1e-6f && float.IsFinite(len)) points[i] = points[i - 1] + d * (lengths[i - 1] / len);
        }
    }

    public static void KeepLengths(Vector3[] points, float[] lengths, Settings cfg)
    {
        for (var i = 1; i < points.Length; i++)
        {
            var delta = points[i] - points[i - 1];
            var length = delta.Length();
            if (length <= 1e-6f || !float.IsFinite(length)) continue;
            if (!cfg.IsProtected(i)) points[i] = points[i - 1] + delta * (lengths[i - 1] / length);
            else if (!cfg.IsProtected(i - 1)) points[i - 1] = points[i] - delta * (lengths[i - 1] / length);
        }
    }
}
