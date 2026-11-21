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
            Assert(Bounded(frame.Proximal, frame.TailJointLimit) && Bounded(frame.Middle, frame.TailJointLimit) && Bounded(frame.Rear, frame.TailJointLimit) && Bounded(frame.Tip, frame.TailJointLimit)
                && Bounded(frame.LeftEar) && Bounded(frame.RightEar),
                "Tail and ear angles stay within their scoped limits");
            Assert(float.IsFinite(frame.TailDrive) && frame.TailDrive is >= 0 and <= 1, "Tail drive has a finite smooth envelope");
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
                activity += (f.Proximal.Length() + f.Middle.Length() + f.Rear.Length() + f.Tip.Length() + f.LeftEar.Length() + f.RightEar.Length()) / hz;
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
            Assert(e.Proximal == default && e.Middle == default && e.Rear == default && e.Tip == default && e.TailDrive == 0, "Original-tail comparison never receives idle tail motion");
            Assert(t.LeftEar == default && t.RightEar == default && t.EarDrive == 0, "Ear-off state never receives idle ear motion");
            sawEar |= e.HasMotion; sawTail |= t.HasMotion;
        }
        Assert(sawEar && sawTail, "Ear and tail participation remain independent");
        TestEarExcursions();
        TestIdleTargetScope();
        TestIdleTailResponse();
        TestFluffyWholeTailMotion();
    }

    static bool Bounded(Vector2 v, float limit = .5f) => float.IsFinite(v.X) && float.IsFinite(v.Y) && v.Length() <= limit;

    static void TestEarExcursions()
    {
        var schedule = new IdleMotion(10);
        var peaks = new Dictionary<string, float>();
        for (var i = 0; i < 60 * 120; i++)
        {
            var frame = schedule.Update(100 + i / 60d, default, 0, true, false, true, 1);
            if (!frame.HasMotion) { Near(frame.EarDrive, 0, "Ear drive ends between actions"); continue; }
            var angle = MathF.Max(frame.LeftEar.Length(), frame.RightEar.Length());
            peaks[frame.Status] = MathF.Max(peaks.GetValueOrDefault(frame.Status), angle);
            Assert(frame.EarDrive is > 0 and <= 1, "Active ear has bounded temporary drive");
            if (frame.Status == "左耳轻抖") Assert(frame.RightEar == default, "Left twitch leaves the right ear alone");
            if (frame.Status == "右耳轻抖") Assert(frame.LeftEar == default, "Right twitch leaves the left ear alone");
        }
        foreach (var action in new[] { "左耳轻抖", "右耳轻抖", "双耳轻竖" })
            Assert(peaks.GetValueOrDefault(action) > .32f, "Distinct ear target excursion: " + action);
    }

    static void TestIdleTargetScope()
    {
        using var mem = new NativeMemoryView();
        var chain = mem.Allocate(0x180); var particles = mem.Allocate(5 * 96); var nodes = mem.Allocate(5 * 160);
        *(byte**)(chain + 0x20) = particles;
        *(byte**)(chain + 0x28) = nodes;
        var frame = new IdleMotionFrame(100, new(.1f, .01f), new(.12f, .03f), new(.04f, .15f), new(.17f, .06f), new(-.13f, .07f), "test")
            { Proximal = new(.21f, .12f), EarDrive = .75f, TailDrive = .8f, TailDriveLimit = .18f, TailJointLimit = .85f };
        var saved = new IdleMotionTargets.SavedTarget[5];
        foreach (var part in new[] { IdlePart.Tail, IdlePart.LeftEar, IdlePart.RightEar })
        {
            var count = part == IdlePart.Tail ? 5 : 2;
            *(ushort*)(chain + 0xE8) = (ushort)count;
            for (var i = 0; i < count; i++)
            {
                new Span<byte>(particles + i * 96, 96).Fill(0x51);
                *(Vector3*)(particles + i * 96 + 0x40) = part == IdlePart.Tail ? new(.02f, 1, i * .2f) : new(i * .02f, 1 + i * .1f, .03f);
                new Span<byte>(nodes + i * 160, 160).Fill(0x29);
                *(float*)(nodes + i * 160 + 0x88) = i == 3 ? .00125f : .0025f;
            }
            var before = new ReadOnlySpan<byte>(particles, count * 96).ToArray();
            var nodesBefore = new ReadOnlySpan<byte>(nodes, count * 160).ToArray();
            for (var repeat = 0; repeat < 12; repeat++)
            {
                var changed = IdleMotionTargets.Apply(chain, part, frame, saved, out var driven, out var actualCount);
                Assert(changed == particles && actualCount == count, "Supported local chain accepts bounded idle targets");
                for (var i = 0; i < count; i++)
                {
                    var node = new ReadOnlySpan<byte>(particles + i * 96, 96);
                    Assert(node[..0x40].SequenceEqual(before.AsSpan(i * 96, 0x40)), "Idle target leaves particle position, velocity and force untouched");
                    Assert(node[0x4C..].SequenceEqual(before.AsSpan(i * 96 + 0x4C, 20)), "Idle target leaves native coefficients and flags untouched");
                    var p = *(Vector3*)(particles + i * 96 + 0x40);
                    if (i == 0) Assert(p == saved[i].Position, "Ear and tail attachment positions stay exact");
                    if (i > 0) Near(Vector3.Distance(p, *(Vector3*)(particles + (i - 1) * 96 + 0x40)), Vector3.Distance(saved[i].Position, saved[i - 1].Position), "Idle targets preserve segment lengths");
                    var nativeNode = new ReadOnlySpan<byte>(nodes + i * 160, 160);
                    if (i > 0)
                    {
                        Assert(p != saved[i].Position, "Driven link moved in this reference pose");
                        var attraction = *(float*)(nodes + i * 160 + 0x88);
                        var expected = part == IdlePart.Tail ? frame.TailDriveLimit : .16f;
                        var envelope = part == IdlePart.Tail ? frame.TailDrive : frame.EarDrive;
                        Near(attraction, IdleMotionTargets.TailAttraction(saved[i].Attraction, envelope, expected), "Event-only drive");
                        Assert(nativeNode[..0x88].SequenceEqual(nodesBefore.AsSpan(i * 160, 0x88))
                            && nativeNode[0x8C..].SequenceEqual(nodesBefore.AsSpan(i * 160 + 0x8C, 20)), "Idle drive changes only attraction");
                    }
                    else Assert(nativeNode.SequenceEqual(nodesBefore.AsSpan(i * 160, 160)), "Attachment coefficients remain exact");
                }
                Assert(*(Vector3*)(particles + (count - 1) * 96 + 0x40) != saved[count - 1].Position, "Idle input actually changes the intended endpoint");
                try { throw new InvalidOperationException("simulated native delegate failure"); }
                catch (InvalidOperationException) { }
                finally { IdleMotionTargets.Restore(changed, driven, saved, count); }
                Assert(new ReadOnlySpan<byte>(particles, count * 96).SequenceEqual(before), "Restore exact animation targets after every native iteration, including exceptions");
                Assert(new ReadOnlySpan<byte>(nodes, count * 160).SequenceEqual(nodesBefore), "Restore every native coefficient after every iteration, including exceptions");
            }
            Assert(IdleMotionTargets.Apply(chain, part, IdleMotionFrame.Empty, saved, out _, out _) == null, "Quiet frame performs no writes");
            Assert(new ReadOnlySpan<byte>(particles, count * 96).SequenceEqual(before), "No idle target drift across repeated native substeps");
            *(Vector3*)(particles + (count - 1) * 96 + 0x40) = new(float.NaN, 0, 0);
            var invalidBefore = new ReadOnlySpan<byte>(particles, count * 96).ToArray();
            Assert(IdleMotionTargets.Apply(chain, part, frame, saved, out _, out _) == null, "Invalid target prevents the entire scoped override");
            Assert(new ReadOnlySpan<byte>(particles, count * 96).SequenceEqual(invalidBefore), "No partial target writes on invalid input");
            Assert(new ReadOnlySpan<byte>(nodes, count * 160).SequenceEqual(nodesBefore), "Invalid input never leaves a drive override");
        }
        Assert(IdleMotionTargets.Apply((byte*)1, IdlePart.None, frame, saved, out _, out _) == null, "Unowned chain is rejected before dereference");

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

    static void TestIdleTailResponse()
    {
        // Simplified native attraction recurrence, not a game-physics/visual acceptance.
        // Short target pulses must survive the very weak fluffy-tail coefficients.
        foreach (var attraction in new[] { .0025f, .00125f, .005f })
        {
            float passive = 0, active = 0, peakPassive = 0, peakActive = 0;
            for (var i = 0; i <= 240; i++)
            {
                var envelope = IdleMotion.Pulse(i / 240f);
                var target = .15f * envelope;
                passive += (target - passive) * attraction;
                var driven = IdleMotionTargets.TailAttraction(attraction, envelope);
                active += (target - active) * driven;
                peakPassive = MathF.Max(peakPassive, passive);
                peakActive = MathF.Max(peakActive, active);
                Assert(driven >= attraction && driven <= .040001f, "Active tail follows a bounded coefficient throughout its curve");
            }
            Assert(peakActive > .10f && peakActive > peakPassive * 1.5f, "Weak tail follows a transient target substantially better");
            Near(IdleMotionTargets.TailAttraction(attraction, 0), attraction, "No residual drive between events");
        }
        Near(IdleMotionTargets.TailAttraction(.1f, 1), .1f, "Already strong custom attraction is never reduced");

        // Actual generated targets on a 0.6 m mid-to-tip chain must move visibly
        // across all three event families, while the ear event limits stay unchanged.
        using var mem = new NativeMemoryView();
        var chain = mem.Allocate(0x180); var particles = mem.Allocate(5 * 96); var nodes = mem.Allocate(5 * 160);
        *(byte**)(chain + 0x20) = particles; *(byte**)(chain + 0x28) = nodes; *(ushort*)(chain + 0xE8) = 5;
        for (var i = 0; i < 5; i++) { *(Vector3*)(particles + i * 96 + 0x40) = new(0, 0, i * .2f); *(float*)(nodes + i * 160 + 0x88) = .0025f; }
        var peaks = new Dictionary<string, float>(); var schedule = new IdleMotion(142);
        var saved = new IdleMotionTargets.SavedTarget[5];
        for (var i = 0; i < 60 * 120; i++)
        {
            var sample = schedule.Update(100 + i / 60.0, default, 0, true, true, false, 1);
            var p = IdleMotionTargets.Apply(chain, IdlePart.Tail, sample, saved, out var driven, out var count);
            if (p == null) continue;
            var distance = Vector3.Distance(*(Vector3*)(p + 4 * 96 + 0x40), saved[4].Position);
            peaks[sample.Status] = MathF.Max(peaks.GetValueOrDefault(sample.Status), distance);
            IdleMotionTargets.Restore(p, driven, saved, count);
        }
        foreach (var name in new[] { "尾巴慢摆", "尾尖轻挑", "尾巴轻甩" })
            Assert(peaks.GetValueOrDefault(name) > .12f, "Each fluffy-tail event has a visible target excursion: " + name);

        var before = new ReadOnlySpan<byte>(particles, 5 * 96).ToArray();
        *(float*)(nodes + 3 * 160 + 0x88) = float.NaN;
        var bad = new IdleMotionFrame(100, new(.2f, 0), default, default, default, default, "invalid") { TailDrive = 1 };
        Assert(IdleMotionTargets.Apply(chain, IdlePart.Tail, bad, saved, out _, out _) == null, "Invalid native attraction rejects all changes");
        Assert(new ReadOnlySpan<byte>(particles, 5 * 96).SequenceEqual(before), "Coefficient validation runs before target writes");
    }

    static void TestFluffyWholeTailMotion()
    {
        using var mem = new NativeMemoryView();
        var chain = mem.Allocate(0x180); var particles = mem.Allocate(5 * 96); var nodes = mem.Allocate(5 * 160);
        *(byte**)(chain + 0x20) = particles; *(byte**)(chain + 0x28) = nodes; *(ushort*)(chain + 0xE8) = 5;
        var saved = new IdleMotionTargets.SavedTarget[5];
        // Cover an upright fluffy tail as well as rearward/sideways poses: yaw
        // alone cannot move a vertical chain. Use the same 0.2 m segments.
        foreach (var axis in new[] { Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitX })
        {
            var proximalPeak = new Dictionary<string, float>();
            var middlePeak = new Dictionary<string, float>();
            var tipPeak = new Dictionary<string, float>();
            var schedule = new IdleMotion(142);
            string previous = ""; float sign = 0;
            for (var n = 0; n < 5; n++)
            {
                *(Vector3*)(particles + n * 96 + 0x40) = axis * (n * .2f);
                *(float*)(nodes + n * 160 + 0x88) = .0025f;
            }
            for (var i = 0; i < 60 * 120; i++)
            {
                var sample = schedule.Update(100 + i / 60.0, default, 0, true, true, false, 1);
                if (!sample.HasMotion) { previous = ""; continue; }
                Assert(sample.TailDriveLimit == .18f && sample.TailJointLimit == .85f, "Fluffy style selects its dedicated active-motion limits");
                var currentSign = MathF.Sign(sample.Proximal.X);
                if (sample.Status == previous && currentSign != 0)
                    Assert(sign == currentSign, "Fluffy motion returns from one arc without repeated direction reversals");
                previous = sample.Status; sign = currentSign;
                var p = IdleMotionTargets.Apply(chain, IdlePart.Tail, sample, saved, out var driven, out var count);
                Assert(p != null, "Every generated fluffy sample fits the native target validator");
                var proximal = Vector3.Distance(*(Vector3*)(p + 96 + 0x40), saved[1].Position);
                proximalPeak[sample.Status] = MathF.Max(proximalPeak.GetValueOrDefault(sample.Status), proximal);
                var mid = Vector3.Distance(*(Vector3*)(p + 2 * 96 + 0x40), saved[2].Position);
                var tip = Vector3.Distance(*(Vector3*)(p + 4 * 96 + 0x40), saved[4].Position);
                middlePeak[sample.Status] = MathF.Max(middlePeak.GetValueOrDefault(sample.Status), mid);
                tipPeak[sample.Status] = MathF.Max(tipPeak.GetValueOrDefault(sample.Status), tip);
                IdleMotionTargets.Restore(p, driven, saved, count);
            }
            foreach (var action in new[] { "尾巴慢摆", "尾尖轻挑", "尾巴轻甩" })
            {
                Assert(proximalPeak.GetValueOrDefault(action) > .045f, "The first skinned link swings in every pose: " + action);
                Assert(middlePeak.GetValueOrDefault(action) > .09f, "Fluffy action moves the middle in every pose: " + action);
                Assert(tipPeak.GetValueOrDefault(action) > .18f, "Fluffy action has broad endpoint travel in every pose: " + action);
            }
        }
        foreach (var type in new[] { 0, 2 })
        {
            var schedule = new IdleMotion(3);
            for (var i = 0; i < 60 * 30; i++)
            {
                var sample = schedule.Update(100 + i / 60.0, default, 0, true, true, false, type);
                Assert(sample.Proximal == default && sample.TailDriveLimit == .04f && sample.TailJointLimit == .5f,
                    "Short and dragon tails retain their earlier active-motion limits");
                Assert(Bounded(sample.Middle) && Bounded(sample.Rear) && Bounded(sample.Tip), "Other styles retain previous angular limits");
            }
        }
        var nearTarget = (Vector3*)(particles + 96 + 0x40);
        *nearTarget = *(Vector3*)(particles + 0x40);
        var collapsed = new IdleMotionFrame(100, new(.3f, 0), default, default, default, default, "collapsed proximal")
            { Proximal = new(.4f, 0), TailDrive = 1, TailDriveLimit = .18f };
        var fallback = IdleMotionTargets.Apply(chain, IdlePart.Tail, collapsed, saved, out var drivenNodes, out var fallbackCount);
        Assert(fallback != null && !saved[1].Modified && saved[2].Modified, "A coincident proximal node keeps the rest of the tail active");
        IdleMotionTargets.Restore(fallback, drivenNodes, saved, fallbackCount);
        var before = new ReadOnlySpan<byte>(particles, 5 * 96).ToArray();
        var invalid = new IdleMotionFrame(100, new(.2f, 0), default, default, default, default, "invalid")
            { TailDrive = 1, TailDriveLimit = .19f };
        Assert(IdleMotionTargets.Apply(chain, IdlePart.Tail, invalid, saved, out _, out _) == null, "Out-of-range drive limit rejected");
        invalid = invalid with { TailDriveLimit = .12f, TailJointLimit = float.NaN };
        Assert(IdleMotionTargets.Apply(chain, IdlePart.Tail, invalid, saved, out _, out _) == null, "Nonfinite joint limit rejected");
        Assert(new ReadOnlySpan<byte>(particles, 5 * 96).SequenceEqual(before), "Bad profile limits leave all targets intact");
    }
}
