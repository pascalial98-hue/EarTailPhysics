using System.Numerics;

namespace EarTailPhysics;

// Native particles, animation targets and velocities are in skeleton model space.
// BG collision queries and configured radii are in world metres.
public readonly struct PhysicsSpace(Matrix4x4 world, Matrix4x4 inverse)
{
    public Vector3 Origin => world.Translation;
    public Vector3 ToWorld(Vector3 point) => Vector3.Transform(point, world);
    public Vector3 ToLocal(Vector3 point) => Vector3.Transform(point, inverse);
    public Vector3 VelocityToWorld(Vector3 velocity) => Vector3.TransformNormal(velocity, world);
    public Vector3 VelocityToLocal(Vector3 velocity) => Vector3.TransformNormal(velocity, inverse);

    public static unsafe bool TryRead(nint module, out PhysicsSpace space)
    {
        space = default;
        if (module == 0) return false;
        var matrix = *(Matrix4x4*)(module + 0x10);
        // The native affine matrix only defines XYZ in each 16-byte row. Padding
        // lanes can contain unrelated values (including NaN); do not invert them.
        matrix.M14 = matrix.M24 = matrix.M34 = 0;
        matrix.M44 = 1;
        if (!Finite(matrix) || !Matrix4x4.Invert(matrix, out var inverse) || !Finite(inverse)) return false;
        space = new(matrix, inverse);
        return true;
    }

    private static bool Finite(Matrix4x4 matrix)
        => ContactMath.Finite(new(matrix.M11, matrix.M12, matrix.M13))
        && ContactMath.Finite(new(matrix.M21, matrix.M22, matrix.M23))
        && ContactMath.Finite(new(matrix.M31, matrix.M32, matrix.M33))
        && ContactMath.Finite(matrix.Translation);
}
