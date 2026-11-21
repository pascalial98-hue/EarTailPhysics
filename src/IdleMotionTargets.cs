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
    public readonly record struct SavedTarget(Vector3 Position, float Attraction, bool Modified = false);

    public static byte* Apply(byte* chain, IdlePart part, IdleMotionFrame frame, Span<SavedTarget> saved,
        out byte* drivenNodes, out int count)
    {
        drivenNodes = null;
        count = part == IdlePart.Tail ? 5 : part is IdlePart.LeftEar or IdlePart.RightEar ? 2 : 0;
        if (count == 0 || saved.Length < count || *(ushort*)(chain + 0xE8) != count) return null;
        var angles = part == IdlePart.LeftEar ? frame.LeftEar : frame.RightEar;
        var isTail = part == IdlePart.Tail;
        var swingsFromRoot = isTail && frame.Proximal != default;
        if (isTail ? !swingsFromRoot && frame.Middle == default && frame.Rear == default && frame.Tip == default : angles == default) return null;
        var particles = *(byte**)(chain + 0x20);
        if (particles == null) return null;
        var first = isTail && !swingsFromRoot ? 2 : 1;
        var drive = isTail ? frame.TailDrive : frame.EarDrive;
        var driveLimit = isTail ? frame.TailDriveLimit : .16f;
        var jointLimit = part == IdlePart.Tail ? frame.TailJointLimit : .5f;
        if (!float.IsFinite(drive) || drive is < 0 or > 1
            || !float.IsFinite(driveLimit) || driveLimit is < 0 or > .18f
            || !float.IsFinite(jointLimit) || jointLimit is <= 0 or > .85f) return null;
        var nodes = drive > 0 ? *(byte**)(chain + 0x28) : null;
        if (drive > 0 && nodes == null) return null;
        Span<Vector3> modified = stackalloc Vector3[5];
        for (var i = 0; i < count; i++)
        {
            modified[i] = *(Vector3*)(particles + i * 96 + 0x40);
            var attraction = nodes != null && i >= first ? *(float*)(nodes + i * 160 + 0x88) : 0;
            saved[i] = new(modified[i], attraction);
            if (!ContactMath.Finite(modified[i]) || !float.IsFinite(attraction) || attraction is < 0 or > 1) return null;
        }
        var rotation = Quaternion.Identity;
        // A vertical base twists under model-Y yaw. Bank sideways in proportion
        // to the inclination of its animation target. No direction thresholds.
        var baseSwingAxis = Vector3.UnitY;
        if (swingsFromRoot)
        {
            var baseLink = saved[1].Position - saved[0].Position;
            var length = baseLink.Length();
            if (!float.IsFinite(length) || length > 3) return null;
            if (length < .0001f)
            {
                // Some custom shapes collapse b/c onto one point. Preserve that
                // attachment and let the remaining segments perform the action.
                first = 2;
            }
            else
            {
                var inclination = Math.Clamp(baseLink.Y / length, -1, 1);
                baseSwingAxis = Vector3.Normalize(new Vector3(0, 1 - MathF.Abs(inclination), inclination));
            }
        }
        for (var i = first; i < count; i++)
        {
            var delta = saved[i].Position - saved[i - 1].Position;
            if (delta.LengthSquared() is < 1e-8f or > 9) return null;
            var joint = !isTail ? angles : i switch
            {
                1 => frame.Proximal, 2 => frame.Middle, 3 => frame.Rear, _ => frame.Tip,
            };
            if (!float.IsFinite(joint.X) || !float.IsFinite(joint.Y) || joint.LengthSquared() > jointLimit * jointLimit) return null;
            // Tail uses yaw/pitch. Ear uses roll/pitch, mirrored by the event sampler.
            var swingAxis = !isTail ? Vector3.UnitZ : i == 1 ? baseSwingAxis : Vector3.UnitY;
            var q = Quaternion.CreateFromAxisAngle(swingAxis, joint.X)
                * Quaternion.CreateFromAxisAngle(Vector3.UnitX, joint.Y);
            rotation = Quaternion.Normalize(rotation * q);
            modified[i] = modified[i - 1] + Vector3.Transform(delta, rotation);
            if (!ContactMath.Finite(modified[i])) return null;
        }
        // Validation completes before the first write, so an early return never leaves half a pose.
        for (var i = first; i < count; i++)
        {
            if (modified[i] == saved[i].Position) continue;
            saved[i] = saved[i] with { Modified = true };
            *(Vector3*)(particles + i * 96 + 0x40) = modified[i];
            if (nodes != null)
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
        for (var i = 1; i < count; i++)
        {
            if (!saved[i].Modified) continue;
            *(Vector3*)(particles + i * 96 + 0x40) = saved[i].Position;
            if (drivenNodes != null) *(float*)(drivenNodes + i * 160 + 0x88) = saved[i].Attraction;
        }
    }
}
