using EarTailPhysics;

unsafe partial class Program
{
    static void TestSlotLifecycle()
    {
        using var mem = new NativeMemoryView();
        var source = mem.Allocate(0x120);
        source[0xD0] = 7;
        *(byte**)(source + 0xD8) = mem.Allocate(7 * 104);
        CheckSlotLifecycle(source);
    }

    // Contract test of ownership across native calls using a reference-counted stub.
    // The optional second fixture runs this same sequence on a local, real PHYB.
    static void CheckSlotLifecycle(byte* source)
    {
        using var mem = new NativeMemoryView();
        nint real = (nint)source;
        nint module = (nint)mem.Allocate(0x590);
        nint other = (nint)mem.Allocate(0x590);
        *(int*)(real + 0xAC) = 2; // one backing reference and one installed slot
        *(nint*)(module + 0x190) = real;
        var counts = new Dictionary<nint, int> { [module] = 7 };
        PhysicsResourceSlots? slots = null;
        var vector = (nint)mem.Allocate(8 * sizeof(nint));
        int gearReads = 0, failStage = 0;

        nint Acquire(nint h)
        {
            Assert(h != 0 && *(int*)(h + 0xAC) > 0, "Only acquire a live resource");
            ++*(int*)(h + 0xAC);
            return h;
        }

        void Release(nint h)
        {
            Assert(*(int*)(h + 0xAC) > 0, "Resource reference cannot underflow");
            --*(int*)(h + 0xAC);
            if (h != real) Assert(*(int*)(h + 0xAC) >= 1, "View never reaches the native destructor");
        }

        void Unload(nint m, uint slot)
        {
            if (slot != 0) return;
            var old = *(nint*)(m + 0x190);
            if (old != 0) Release(old);
            *(nint*)(m + 0x190) = 0;
            *(nint*)(m + 0xA8) = *(nint*)(m + 0xA0);
            counts.Remove(m);
        }

        void Load(nint m, nint h, uint slot)
        {
            if (slot != 0)
            {
                Assert(slots!.IsChanging, "Equipment load cannot re-enter plugin takeover while rebuilding");
                var active = (byte*)*(nint*)(m + 0x190);
                Assert(active[0xD0] == counts[m], "Gear refresh sees descriptors for every running group");
                if (counts[m] == 8)
                {
                    var last = *(byte**)(active + 0xD8) + 7 * 104;
                    Assert(last[2] == 2, "Eighth descriptor is the live ear group");
                    Assert(NativeMemoryView.Name(*(byte**)(Chain(active, 7, 0) + 0x30)) == "j_mimi_l"
                        && NativeMemoryView.Name(*(byte**)(Chain(active, 7, 1) + 0x30)) == "j_mimi_r", "Both ears remain available during gear refresh");
                }
                gearReads++;
                return;
            }
            if (failStage == 1) throw new IOException("Before adoption");
            if (*(nint*)(m + 0x190) == h) return;
            slots!.GameUnload(m, 0);
            *(nint*)(m + 0x190) = Acquire(h);
            counts[m] = *(byte*)(h + 0xD0);
            *(nint*)(m + 0xA0) = vector;
            *(nint*)(m + 0xA8) = vector + (failStage == 3 ? 7 : counts[m]) * sizeof(nint);
            if (failStage == 2) throw new IOException("After adoption");
        }

        slots = new PhysicsResourceSlots(Acquire, Release, Load, Unload, _ => { });
        void Apply(nint m, bool ears)
        {
            var src = slots.SourceFor(m, *(nint*)(m + 0x190));
            Assert(src == real, "Reapply from original source exactly once");
            var view = new DescriptorBuilder((byte*)src, new Settings { Ears = ears }, true);
            slots.Install(m, src, view);
            Assert(counts[m] == (ears ? 8 : 7), "One optional extra group at every rebuild");
        }

        for (var i = 0; i < 12; i++)
        {
            Apply(module, true);
            var handle = *(nint*)(module + 0x190);
            slots.GameLoad(module, real, 0);
            Assert(*(nint*)(module + 0x190) == handle, "Repeated loading of same source retains tuning");
            slots.GameUnload(module, 2);
            slots.GameLoad(module, 0, 2);
            Apply(module, false);
            slots.GameLoad(module, 0, 2);
            slots.Restore(module);
            Assert(*(nint*)(module + 0x190) == real && counts[module] == 7, "Restore installs the exact source");
            Assert(*(int*)(real + 0xAC) == 2, "Reapply and restore balance source references");
        }
        Assert(gearReads == 24, "All equipment refreshes inspected the installed base slot");
        Apply(module, true);
        var borrowed = Acquire(*(nint*)(module + 0x190));
        slots.GameUnload(module, 0); // game redraw
        Assert(slots.HasRetired && *(byte*)(borrowed + 0xD0) == 8, "Other native reference keeps descriptor alive after unload");
        slots.CollectRetired();
        Assert(slots.HasRetired && *(int*)(real + 0xAC) == 2, "Backing source remains pinned");
        slots.GameLoad(module, real, 0);
        Apply(module, true);
        Release(borrowed);
        slots.CollectRetired();
        Assert(!slots.HasRetired, "Last external reference releases deferred backing storage");

        slots.GameLoad(other, real, 0);
        Apply(other, true);
        slots.RestoreAll();
        Assert(*(nint*)(module + 0x190) == real && *(nint*)(other + 0x190) == real, "Dispose restores even a previous actor without a local-player lookup");
        slots.GameUnload(other, 0);
        for (var stage = 1; stage <= 3; stage++)
        {
            failStage = stage;
            try { Apply(module, true); throw new Exception("Expected simulated failure"); }
            catch (IOException) when (stage < 3) { }
            catch (InvalidDataException) when (stage == 3) { }
            failStage = 0;
            slots.RestoreAll();
            Assert(*(nint*)(module + 0x190) == real && *(int*)(real + 0xAC) == 2, "Partial load restores source and balances pins");
        }
        var differentSource = (nint)mem.Copy(source, 0x120);
        *(int*)(differentSource + 0xAC) = 1;
        Apply(module, true);
        slots.GameLoad(module, differentSource, 0);
        Assert(slots.SourceFor(module, differentSource) == differentSource && *(int*)(real + 0xAC) == 1,
            "New body source releases old attachment and retains its own identity");
        slots.Install(module, differentSource, new DescriptorBuilder((byte*)differentSource, new Settings(), true));
        slots.RestoreAll();
        Assert(*(nint*)(module + 0x190) == differentSource && *(int*)(differentSource + 0xAC) == 2,
            "Disable after body change restores that body");
        slots.GameLoad(module, real, 0);
        Apply(module, true);
        Unload(module, 0); // native already tore down the module outside the observed callback
        slots.RestoreAll();
        Assert(*(nint*)(module + 0x190) == 0 && *(int*)(real + 0xAC) == 1,
            "Dropped native slot reference releases the pin without reloading the dead module");

        slots.GameLoad(module, real, 0);
        Release(real); // the base slot is now the only owner
        Apply(module, true);
        Assert(*(int*)(real + 0xAC) == 1, "Live descriptor pins source in absence of an external cache reference");
        slots.Restore(module);
        Assert(*(nint*)(module + 0x190) == real && *(int*)(real + 0xAC) == 1,
            "Bridge hold keeps sole source reference alive across native restore");
        slots.GameUnload(module, 0);
        Assert(*(int*)(real + 0xAC) == 0 && !slots.IsChanging, "Final unload releases the original exactly once");
    }
}
