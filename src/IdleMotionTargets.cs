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
    public readonly record struct SavedTarget(Vector3 Position, float Attraction);

    public static byte* Apply(byte* chain, IdlePart part, IdleMotionFrame frame, Span<SavedTarget> saved,
        out byte* drivenNodes, out int count)
    {
        drivenNodes = null;
        count = part == IdlePart.Tail ? 5 : part is IdlePart.LeftEar or IdlePart.RightEar ? 2 : 0;
        if (count == 0 || saved.Length < count || *(ushort*)(chain + 0xE8) != count) return null;
        var angles = part == IdlePart.LeftEar ? frame.LeftEar : frame.RightEar;
        if (part == IdlePart.Tail ? frame.Middle == default && frame.Rear == default && frame.Tip == default : angles == default) return null;
        var particles = *(byte**)(chain + 0x20);
        if (particles == null) return null;
        // Only the tail needs an active drive: its heavy presets deliberately have
        // very weak attraction. Smoothly raise it for this constraint call only.
        var drive = part == IdlePart.Tail ? frame.TailDrive : 0;
        var driveLimit = part == IdlePart.Tail ? frame.TailDriveLimit : .04f;
        var jointLimit = part == IdlePart.Tail ? frame.TailJointLimit : .5f;
        if (!float.IsFinite(drive) || drive is < 0 or > 1
            || !float.IsFinite(driveLimit) || driveLimit is < 0 or > .12f
            || !float.IsFinite(jointLimit) || jointLimit is <= 0 or > .85f) return null;
        var nodes = drive > 0 ? *(byte**)(chain + 0x28) : null;
        if (drive > 0 && nodes == null) return null;
        Span<Vector3> modified = stackalloc Vector3[5];
        for (var i = 0; i < count; i++)
        {
            modified[i] = *(Vector3*)(particles + i * 96 + 0x40);
            var attraction = nodes != null && i >= 2 ? *(float*)(nodes + i * 160 + 0x88) : 0;
            saved[i] = new(modified[i], attraction);
            if (!ContactMath.Finite(modified[i]) || !float.IsFinite(attraction) || attraction is < 0 or > 1) return null;
        }
        var rotation = Quaternion.Identity;
        var first = part == IdlePart.Tail ? 2 : 1;
        for (var i = first; i < count; i++)
        {
            var delta = saved[i].Position - saved[i - 1].Position;
            if (delta.LengthSquared() is < 1e-8f or > 9) return null;
            var joint = part == IdlePart.Tail ? i == 2 ? frame.Middle : i == 3 ? frame.Rear : frame.Tip : angles;
            if (!float.IsFinite(joint.X) || !float.IsFinite(joint.Y) || joint.LengthSquared() > jointLimit * jointLimit) return null;
            // Tail uses yaw/pitch. Ear uses roll/pitch, mirrored by the event sampler.
            var q = Quaternion.CreateFromAxisAngle(part == IdlePart.Tail ? Vector3.UnitY : Vector3.UnitZ, joint.X)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitX, joint.Y);
            rotation = Quaternion.Normalize(rotation * q);
            modified[i] = modified[i - 1] + Vector3.Transform(delta, rotation);
            if (!ContactMath.Finite(modified[i])) return null;
        }
        // Validation completes before the first write, so an early return never leaves half a pose.
        for (var i = first; i < count; i++)
        {
            *(Vector3*)(particles + i * 96 + 0x40) = modified[i];
            if (nodes != null && modified[i] != saved[i].Position)
                *(float*)(nodes + i * 160 + 0x88) = TailAttraction(saved[i].Attraction, drive, driveLimit);
        }
        drivenNodes = nodes;
        return particles;
    }

    public static float TailAttraction(float original, float drive, float limit = .04f)
        => original + MathF.Max(0, limit - original) * drive;

    public static void Restore(byte* particles, byte* drivenNodes, ReadOnlySpan<SavedTarget> saved, int count)
    {
        if (particles == null) return;
        for (var i = count == 5 ? 2 : 1; i < count; i++)
        {
            *(Vector3*)(particles + i * 96 + 0x40) = saved[i].Position;
            if (drivenNodes != null) *(float*)(drivenNodes + i * 160 + 0x88) = saved[i].Attraction;
        }
    }
}
