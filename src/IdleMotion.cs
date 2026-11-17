using System.Numerics;

namespace EarTailPhysics;

public enum IdlePart { None, Tail, LeftEar, RightEar }

// One immutable framework-time sample is shared by every native substep/iteration.
public sealed record IdleMotionFrame(double Time, Vector2 Middle, Vector2 Rear, Vector2 Tip,
    Vector2 LeftEar, Vector2 RightEar, string Status)
{
    public static readonly IdleMotionFrame Empty = new(0, default, default, default, default, default, "随机小动作已关闭");
    public float TailDrive { get; init; }
    public bool HasMotion => Middle != default || Rear != default || Tip != default || LeftEar != default || RightEar != default;
}

public sealed class IdleMotion(int? seed = null)
{
    private readonly Random random = seed is { } value ? new(value) : new();
    private readonly Channel tail = new(), ears = new();
    private double lastTime;
    private Vector3 lastPosition;
    private float lastRotation, stillTime, weight;
    private bool initialized;
    public long Events { get; private set; }

    private sealed class Channel
    {
        public int Kind = -1, Previous = -1;
        public double Start, End, Due = double.NaN;
        public float Duration, Sign, Amplitude;
        public void Clear() { Kind = -1; Due = double.NaN; }
    }

    public void Reset()
    {
        initialized = false; weight = stillTime = 0;
        tail.Clear(); ears.Clear();
    }

    // Called only on the framework thread. A stop/turn fades existing motion and
    // requires a fresh quiet period before another event can start.
    public IdleMotionFrame Update(double now, Vector3 position, float rotation, bool allowed,
        bool tailEnabled, bool earsEnabled, int tailType)
    {
        if (!double.IsFinite(now) || !ContactMath.Finite(position) || !float.IsFinite(rotation))
        { Reset(); return IdleMotionFrame.Empty; }
        var dt = now - lastTime;
        if (!initialized || dt <= 0 || dt > .5)
        {
            Reset(); initialized = true;
            lastTime = now; lastPosition = position; lastRotation = rotation;
            return new(now, default, default, default, default, default, allowed ? "等待静止" : "当前状态暂停随机动作");
        }
        var speed = Vector3.Distance(position, lastPosition) / (float)dt;
        var turnSpeed = MathF.Abs(MathF.IEEERemainder(rotation - lastRotation, 2 * MathF.PI)) / (float)dt;
        lastTime = now; lastPosition = position; lastRotation = rotation;
        if (!allowed)
        {
            weight = stillTime = 0; tail.Clear(); ears.Clear();
            return new(now, default, default, default, default, default, "当前状态暂停随机动作");
        }
        var moving = speed > .08f || turnSpeed > .18f;
        stillTime = moving ? 0 : stillTime + (float)dt;
        var eligible = stillTime >= 1.8f;
        weight = Math.Clamp(weight + (eligible ? 1 : -1) * (float)dt / .35f, 0, 1);
        if (weight == 0 && !eligible) { tail.Clear(); ears.Clear(); }
        Advance(tail, now, tailEnabled, eligible, tailType == 1 ? 1.25f : 1, true);
        Advance(ears, now, earsEnabled, eligible, 1, false);

        var middle = Vector2.Zero; var rear = Vector2.Zero; var tip = Vector2.Zero;
        var tailDrive = 0f;
        var left = Vector2.Zero; var right = Vector2.Zero;
        var tailScale = tailType == 1 ? .85f : tailType == 2 ? .7f : 1;
        if (tail.Kind >= 0 && weight > 0)
        {
            var t = (float)((now - tail.Start) / tail.Duration);
            var a = tail.Amplitude * weight * tailScale;
            tailDrive = Pulse(t) * weight;
            // Values are incremental joint angles: root and proximal target stay fixed.
            switch (tail.Kind)
            {
                case 0: // a slow side glance, then a softer return through centre
                    middle = new(tail.Sign * .36f * Sway(t) * a, .09f * Pulse(t) * a);
                    rear = new(tail.Sign * .30f * Sway(t) * a, .08f * Pulse(t) * a);
                    tip = new(tail.Sign * .20f * Sway(t) * a, .10f * Pulse(t) * a);
                    break;
                case 1: // lift/relax the tip, with a little movement in the rear
                    rear = new(tail.Sign * .10f * Pulse(t) * a, .22f * Pulse(t) * a);
                    tip = new(tail.Sign * .10f * Pulse(t) * a, .38f * Pulse(t) * a);
                    break;
                case 2: // one brief flick; no repeated bounce train
                    middle = new(tail.Sign * .24f * Sway(t) * a, .05f * Pulse(t) * a);
                    rear = new(tail.Sign * .40f * Sway(t) * a, .07f * Pulse(t) * a);
                    tip = new(tail.Sign * .30f * Sway(t) * a, .10f * Pulse(t) * a);
                    break;
            }
        }
        if (ears.Kind >= 0 && weight > 0)
        {
            var t = (float)((now - ears.Start) / ears.Duration);
            var a = ears.Amplitude * weight;
            var twitch = new Vector2(.17f * Pulse(t) * a, .08f * Sway(t) * a);
            switch (ears.Kind)
            {
                case 0: left = twitch; break;
                case 1: right = new(-twitch.X, twitch.Y); break;
                case 2:
                    left = new(.10f * Pulse(t) * a, -.13f * Pulse(t) * a);
                    right = new(-left.X, left.Y);
                    break;
            }
        }
        var status = !eligible ? weight > 0 ? "移动／转身，正在淡出" : "等待静止"
            : tail.Kind >= 0 || ears.Kind >= 0 ? Describe(tail.Kind, ears.Kind) : "静止中，等待下一次随机动作";
        return new(now, middle, rear, tip, left, right, status) { TailDrive = tailDrive };
    }

    private void Advance(Channel channel, double now, bool enabled, bool canStart, float durationScale, bool isTail)
    {
        if (!enabled) { channel.Clear(); return; }
        if (channel.Kind >= 0 && now >= channel.End)
        {
            channel.Kind = -1;
            channel.Due = now + (isTail ? 2.5 : 2) + random.NextDouble() * (isTail ? 4 : 5);
        }
        if (!canStart || channel.Kind >= 0) return;
        if (double.IsNaN(channel.Due)) channel.Due = now + .8 + random.NextDouble() * 1.7;
        if (now < channel.Due) return;
        // No immediate repeat on a channel; ears and tail have independent schedules.
        var kind = channel.Previous < 0 ? random.Next(3) : (channel.Previous + 1 + random.Next(2)) % 3;
        channel.Previous = channel.Kind = kind;
        channel.Start = now;
        channel.Duration = (isTail ? kind == 0 ? 4.8f : kind == 1 ? 3.4f : 2.1f
            : kind == 2 ? 1.6f : .65f) * durationScale * (.9f + .2f * (float)random.NextDouble());
        channel.End = now + channel.Duration;
        channel.Sign = random.Next(2) == 0 ? -1 : 1;
        channel.Amplitude = .8f + .4f * (float)random.NextDouble();
        Events++;
    }

    // Smooth zero position and zero first derivative at both event boundaries.
    public static float Pulse(float t) => t is <= 0 or >= 1 ? 0 : MathF.Pow(MathF.Sin(MathF.PI * t), 2);
    public static float Sway(float t) => Pulse(t) * MathF.Sin(2 * MathF.PI * t);
    private static string Describe(int tailKind, int earKind)
    {
        var a = tailKind switch { 0 => "尾巴慢摆", 1 => "尾尖轻挑", 2 => "尾巴轻甩", _ => "" };
        var b = earKind switch { 0 => "左耳轻抖", 1 => "右耳轻抖", 2 => "双耳轻竖", _ => "" };
        return a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;
    }
}
