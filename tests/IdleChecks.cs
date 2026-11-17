using System.Numerics;
using EarTailPhysics;

unsafe partial class Program
{
    static void TestIdleMotions()
    {
        var cfg = new Settings().Snapshot();
        Assert(!cfg.RandomIdleMotions, "Idle motion defaults off for new and existing configurations");
        var enabled = cfg.Snapshot(); enabled.RandomIdleMotions = true;
        Assert(enabled.Snapshot().RandomIdleMotions && cfg.SameNativeConfiguration(enabled), "Idle toggle persists without rebuilding physics");
        foreach (var edge in new[] { 0f, 1f })
        {
            Near(IdleMotion.Pulse(edge), 0, "Idle pulse returns to animation at event boundary");
            Near(IdleMotion.Sway(edge), 0, "Idle sway returns to animation at event boundary");
        }
        Assert(IdleMotion.Pulse(.00001f) < .000001f && IdleMotion.Pulse(.99999f) < .000001f, "No abrupt velocity at event boundaries");

        var seen = new HashSet<string>();
        var schedule = new IdleMotion(142);
        IdleMotionFrame frame = IdleMotionFrame.Empty;
        for (int i = 0; i < 60 * 180; i++)
        {
            frame = schedule.Update(100 + i / 60.0, Vector3.Zero, 0, true, true, true, 1);
            if (frame.HasMotion) foreach (var name in frame.Status.Split(" · ")) seen.Add(name);
            Assert(Bounded(frame.Middle) && Bounded(frame.Rear) && Bounded(frame.Tip)
                && Bounded(frame.LeftEar) && Bounded(frame.RightEar), "All random target angles remain small and finite");
        }
        foreach (var name in new[] { "尾巴慢摆", "尾尖轻挑", "尾巴轻甩", "左耳轻抖", "右耳轻抖", "双耳轻竖" })
            Assert(seen.Contains(name), "Random schedule reaches " + name);
        Assert(schedule.Events is > 20 and < 100, "Random events have quiet gaps instead of continuous looping");

        // Sample at different render rates: event counts and total activity should be similar.
        var samples = new List<(long Events, double Activity)>();
        foreach (var hz in new[] { 30, 60, 144 })
        {
            var s = new IdleMotion(41); double activity = 0;
            for (var i = 0; i < hz * 120; i++)
            {
                var f = s.Update(100 + i / (double)hz, Vector3.Zero, 0, true, true, true, 1);
                activity += (f.Middle.Length() + f.Rear.Length() + f.Tip.Length() + f.LeftEar.Length() + f.RightEar.Length()) / hz;
            }
            samples.Add((s.Events, activity));
        }
        Assert(samples.Max(s => s.Events) - samples.Min(s => s.Events) <= 2, "No per-frame probability bias in random timing");
        Assert(samples.Max(s => s.Activity) / samples.Min(s => s.Activity) < 1.15, "Idle action strength remains comparable at 30/60/144 FPS");

        var movement = new IdleMotion(23); double time = 100;
        for (var i = 0; i < 60 * 30; i++)
        {
            frame = movement.Update(time, new(i * .03f, 0, 0), 0, true, true, true, 1); time += 1d / 60;
            Assert(!frame.HasMotion, "Running never starts idle events");
        }
        Assert(movement.Events == 0, "No events queued during movement");
        var stillPosition = new Vector3((60 * 30 - 1) * .03f, 0, 0);
        for (var i = 0; i < 60 * 12 && !frame.HasMotion; i++)
        { frame = movement.Update(time, stillPosition, 0, true, true, true, 1); time += 1d / 60; }
        Assert(frame.HasMotion, "Resting after movement starts an event");
        var eventsBeforeTurn = movement.Events;
        for (var i = 0; i < 40; i++)
        { frame = movement.Update(time, stillPosition, i * .02f, true, true, true, 1); time += 1d / 60; }
        Assert(!frame.HasMotion && movement.Events == eventsBeforeTurn, "Turning fades an active event without starting another");
        for (var i = 0; i < 900; i++)
        {
            frame = movement.Update(time, stillPosition, 0, false, true, true, 1); time += 1d / 60;
            Assert(!frame.HasMotion, "Pause/combat/cutscene/disabled gate cannot drive targets");
        }
        Assert(movement.Events == eventsBeforeTurn, "Paused time does not accumulate catch-up events");
        frame = movement.Update(time + 5, stillPosition, 0, true, true, true, 1);
        Assert(!frame.HasMotion, "Long frame gap resets instead of bursting queued events");
        frame = movement.Update(double.NaN, stillPosition, 0, true, true, true, 1);
        Assert(!frame.HasMotion, "Invalid clock safely resets");

        var earsOnly = new IdleMotion(1); var tailOnly = new IdleMotion(2);
        bool sawEar = false, sawTail = false;
        for (var i = 0; i < 60 * 35; i++)
        {
            var e = earsOnly.Update(100 + i / 60.0, Vector3.Zero, 0, true, false, true, 1);
            var t = tailOnly.Update(100 + i / 60.0, Vector3.Zero, 0, true, true, false, 2);
            Assert(e.Middle == default && e.Rear == default && e.Tip == default, "Original-tail comparison never receives idle tail motion");
            Assert(t.LeftEar == default && t.RightEar == default, "Ear-off state never receives idle ear motion");
            sawEar |= e.HasMotion; sawTail |= t.HasMotion;
        }
        Assert(sawEar && sawTail, "Ear and tail participation remain independent");
        TestIdleTargetScope();
    }

    static bool Bounded(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && v.Length() <= .3f;

    static void TestIdleTargetScope()
    {
        using var mem = new NativeMemoryView();
        var chain = mem.Allocate(0x180); var particles = mem.Allocate(5 * 96);
        *(byte**)(chain + 0x20) = particles;
        var frame = new IdleMotionFrame(100, new(.1f, .01f), new(.12f, .03f), new(.04f, .15f), new(.17f, .06f), new(-.13f, .07f), "test");
        var saved = new Vector3[5];
        foreach (var part in new[] { IdlePart.Tail, IdlePart.LeftEar, IdlePart.RightEar })
        {
            var count = part == IdlePart.Tail ? 5 : 2;
            *(ushort*)(chain + 0xE8) = (ushort)count;
            for (var i = 0; i < count; i++)
            {
                new Span<byte>(particles + i * 96, 96).Fill(0x51);
                *(Vector3*)(particles + i * 96 + 0x40) = part == IdlePart.Tail ? new(.02f, 1, i * .2f) : new(i * .02f, 1 + i * .1f, .03f);
            }
            var before = new ReadOnlySpan<byte>(particles, count * 96).ToArray();
            for (var repeat = 0; repeat < 12; repeat++)
            {
                var changed = IdleMotionTargets.Apply(chain, part, frame, saved, out var actualCount);
                Assert(changed == particles && actualCount == count, "Supported local chain accepts bounded idle targets");
                for (var i = 0; i < count; i++)
                {
                    var node = new ReadOnlySpan<byte>(particles + i * 96, 96);
                    Assert(node[..0x40].SequenceEqual(before.AsSpan(i * 96, 0x40)), "Idle target leaves particle position, velocity and force untouched");
                    Assert(node[0x4C..].SequenceEqual(before.AsSpan(i * 96 + 0x4C, 20)), "Idle target leaves native coefficients and flags untouched");
                    var p = *(Vector3*)(particles + i * 96 + 0x40);
                    if (i == 0 || part == IdlePart.Tail && i == 1) Assert(p == saved[i], "Root/proximal animation anchors stay exact");
                    if (i > 0) Near(Vector3.Distance(p, *(Vector3*)(particles + (i - 1) * 96 + 0x40)), Vector3.Distance(saved[i], saved[i - 1]), "Idle targets preserve segment lengths");
                }
                Assert(*(Vector3*)(particles + (count - 1) * 96 + 0x40) != saved[count - 1], "Idle input actually changes the intended endpoint");
                try { throw new InvalidOperationException("simulated native delegate failure"); }
                catch (InvalidOperationException) { }
                finally { IdleMotionTargets.Restore(changed, saved, count); }
                Assert(new ReadOnlySpan<byte>(particles, count * 96).SequenceEqual(before), "Restore exact animation targets after every native iteration, including exceptions");
            }
            Assert(IdleMotionTargets.Apply(chain, part, IdleMotionFrame.Empty, saved, out _) == null, "Quiet frame performs no writes");
            Assert(new ReadOnlySpan<byte>(particles, count * 96).SequenceEqual(before), "No idle target drift across repeated native substeps");
            *(Vector3*)(particles + (count - 1) * 96 + 0x40) = new(float.NaN, 0, 0);
            var invalidBefore = new ReadOnlySpan<byte>(particles, count * 96).ToArray();
            Assert(IdleMotionTargets.Apply(chain, part, frame, saved, out _) == null, "Invalid target prevents the entire scoped override");
            Assert(new ReadOnlySpan<byte>(particles, count * 96).SequenceEqual(invalidBefore), "No partial target writes on invalid input");
        }
        Assert(IdleMotionTargets.Apply((byte*)1, IdlePart.None, frame, saved, out _) == null, "Unowned chain is rejected before dereference");

        var skeleton = mem.Allocate(0x120); var draw = mem.Allocate(0x200); var module = mem.Allocate(0x590);
        *(byte**)(chain + 0x10) = skeleton; *(byte**)(skeleton + 0xB8) = draw;
        *(byte**)(draw + 0x158) = module; *(byte**)(module + 0x98) = skeleton;
        var binding = new IdleMotionBindings((nint)module, (nint)chain, 0, 0);
        Assert(RuntimeBinding.IdleChainPart((nint)chain, (nint)draw, binding) == IdlePart.Tail, "Idle hook recognizes live local tail");
        Assert(RuntimeBinding.IdleChainPart(1, (nint)draw, binding) == IdlePart.None, "Idle hook never dereferences another player's chain");
        *(nint*)(skeleton + 0xB8) = 1;
        Assert(RuntimeBinding.IdleChainPart((nint)chain, (nint)draw, binding) == IdlePart.None, "Idle hook rejects replaced skeleton ownership");
        *(byte**)(skeleton + 0xB8) = draw; *(nint*)(draw + 0x158) = 1;
        Assert(RuntimeBinding.IdleChainPart((nint)chain, (nint)draw, binding) == IdlePart.None, "Idle hook rejects stale module before dereferencing it");
    }
}
