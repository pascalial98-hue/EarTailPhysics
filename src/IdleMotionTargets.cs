using System.Numerics;

namespace EarTailPhysics;

public sealed record IdleMotionBindings(nint Module, nint Tail, nint LeftEar, nint RightEar)
{
    public static readonly IdleMotionBindings Empty = new(0, 0, 0, 0);
}

// Scoped input to the native attraction/length solver. No final bone pose, particle
// position, velocity, rest length, wind or shared animation resource is overwritten.
public static unsafe class IdleMotionTargets
{
    public static byte* Apply(byte* chain, IdlePart part, IdleMotionFrame frame, Span<Vector3> saved, out int count)
    {
        count = part == IdlePart.Tail ? 5 : part is IdlePart.LeftEar or IdlePart.RightEar ? 2 : 0;
        if (count == 0 || saved.Length < count || *(ushort*)(chain + 0xE8) != count) return null;
        var angles = part == IdlePart.LeftEar ? frame.LeftEar : frame.RightEar;
        if (part == IdlePart.Tail ? frame.Middle == default && frame.Rear == default && frame.Tip == default : angles == default) return null;
        var particles = *(byte**)(chain + 0x20);
        if (particles == null) return null;
        Span<Vector3> modified = stackalloc Vector3[5];
        for (var i = 0; i < count; i++)
        {
            saved[i] = modified[i] = *(Vector3*)(particles + i * 96 + 0x40);
            if (!ContactMath.Finite(saved[i])) return null;
        }
        var rotation = Quaternion.Identity;
        var first = part == IdlePart.Tail ? 2 : 1;
        for (var i = first; i < count; i++)
        {
            var delta = saved[i] - saved[i - 1];
            if (delta.LengthSquared() is < 1e-8f or > 9) return null;
            var joint = part == IdlePart.Tail ? i == 2 ? frame.Middle : i == 3 ? frame.Rear : frame.Tip : angles;
            if (!float.IsFinite(joint.X) || !float.IsFinite(joint.Y) || joint.LengthSquared() > .25f) return null;
            // Tail uses yaw/pitch. Ear uses roll/pitch, mirrored by the event sampler.
            var q = Quaternion.CreateFromAxisAngle(part == IdlePart.Tail ? Vector3.UnitY : Vector3.UnitZ, joint.X)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitX, joint.Y);
            rotation = Quaternion.Normalize(rotation * q);
            modified[i] = modified[i - 1] + Vector3.Transform(delta, rotation);
            if (!ContactMath.Finite(modified[i])) return null;
        }
        // Validation completes before the first write, so an early return never leaves half a pose.
        for (var i = first; i < count; i++) *(Vector3*)(particles + i * 96 + 0x40) = modified[i];
        return particles;
    }

    public static void Restore(byte* particles, ReadOnlySpan<Vector3> saved, int count)
    {
        if (particles == null) return;
        for (var i = count == 5 ? 2 : 1; i < count; i++) *(Vector3*)(particles + i * 96 + 0x40) = saved[i];
    }
}
