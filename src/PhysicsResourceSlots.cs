using System.Collections.Concurrent;

namespace EarTailPhysics;

// Slot 0 owns a native reference to the installed descriptor. A second, private
// reference keeps the view out of the game's resource cache/destructor. The real
// resource stays pinned while the view borrows its skeleton and collider data.
internal unsafe sealed class PhysicsResourceSlots(
    Func<nint, nint> acquire, Action<nint> release, Action<nint, nint, uint> load,
    Action<nint, uint> unload, Action<nint> detached)
{
    private sealed record Attachment(nint Source, DescriptorBuilder View)
    {
        public nint Handle => (nint)View.Handle;
        public int References => Volatile.Read(ref *(int*)(View.Handle + 0xAC));
    }

    internal object Gate { get; } = new();
    private readonly ConcurrentDictionary<nint, Attachment> attached = new();
    private readonly List<Attachment> retired = [];
    private nint changingModule;
    private int awaitingRelease;

    internal bool HasRetired => Volatile.Read(ref awaitingRelease) != 0;
    internal bool IsChanging => Volatile.Read(ref changingModule) != 0;
    private bool Tracked(nint module) => module == Volatile.Read(ref changingModule) || attached.ContainsKey(module);
    private static nint BaseHandle(nint module) => *(nint*)(module + 0x190);

    internal nint SourceFor(nint module, nint handle) =>
        attached.TryGetValue(module, out var current) && current.Handle == handle ? current.Source : handle;

    private void Hold(nint source)
    {
        var acquired = acquire(source);
        if (acquired == source && acquired != 0) return;
        if (acquired != 0) release(acquired);
        throw new InvalidDataException("Could not retain original physics resource");
    }

    // Takes ownership of 'view' including when native Load rejects or partially adopts it.
    internal void Install(nint module, nint source, DescriptorBuilder view)
    {
        lock (Gate)
        {
            // A redraw may reuse the address of a module that was torn down
            // directly. Only the descriptor actually held by this slot is live here.
            if (attached.TryGetValue(module, out var stale) && BaseHandle(module) != stale.Handle)
            {
                attached.TryRemove(module, out _);
                detached(module);
                Retire(stale);
            }
            var next = new Attachment(source, view);
            try { Hold(source); }
            catch { view.Dispose(); throw; }
            *(int*)(view.Handle + 0xAC) = 1;
            var priorModule = Interlocked.Exchange(ref changingModule, module);
            try
            {
                load(module, next.Handle, 0);
                if (BaseHandle(module) != next.Handle)
                    throw new InvalidDataException("Native loader did not adopt physics descriptor");
                if (NativeWindState.Count(module, out _) != view.Handle[0xD0])
                    throw new InvalidDataException("Installed physics group count differs from the slot descriptor");
            }
            finally
            {
                // Even a failed load can have installed the pointer. Keep it valid
                // so that recovery can rebuild from the pinned real resource.
                if (BaseHandle(module) == next.Handle) attached[module] = next;
                else Retire(next);
                Volatile.Write(ref changingModule, priorModule);
            }
        }
    }

    // Preserve native same-resource no-op semantics. An explicit unload followed by
    // load (redraw) reaches the original resource normally and will be rebound.
    internal void GameLoad(nint module, nint handle, uint slot)
    {
        if (!Tracked(module)) { load(module, handle, slot); return; }
        lock (Gate)
        {
            var priorModule = Interlocked.Exchange(ref changingModule, module);
            try
            {
                if (slot == 0 && attached.TryGetValue(module, out var current)
                    && current.Source == handle && BaseHandle(module) == current.Handle)
                    handle = current.Handle;
                load(module, handle, slot);
            }
            finally { Volatile.Write(ref changingModule, priorModule); }
        }
    }

    internal void GameUnload(nint module, uint slot)
    {
        if (!Tracked(module)) { unload(module, slot); return; }
        lock (Gate)
        {
            var priorModule = Interlocked.Exchange(ref changingModule, module);
            attached.TryGetValue(module, out var previous);
            if (slot == 0 && previous != null) detached(module);
            try { unload(module, slot); }
            finally
            {
                if (slot == 0 && previous != null && BaseHandle(module) != previous.Handle
                    && attached.TryRemove(module, out _))
                {
                    Retire(previous);
                }
                Volatile.Write(ref changingModule, priorModule);
            }
        }
    }

    private bool TryFree(Attachment entry)
    {
        if (entry.References != 1) return false;
        // Release the source pin first. This crosses the game's resource lock,
        // shared by source and view, before freeing the private reference field
        // that a concurrent native release may still be finishing with.
        release(entry.Source);
        entry.View.Dispose();
        return true;
    }

    private void Retire(Attachment entry)
    {
        if (TryFree(entry)) return;
        retired.Add(entry);
        Volatile.Write(ref awaitingRelease, retired.Count);
    }

    internal void CollectRetired()
    {
        lock (Gate)
        {
            for (var i = retired.Count - 1; i >= 0; i--)
                if (TryFree(retired[i])) retired.RemoveAt(i);
            Volatile.Write(ref awaitingRelease, retired.Count);
        }
    }

    internal void Restore(nint module)
    {
        lock (Gate)
        {
            if (!attached.TryGetValue(module, out var current)) return;
            if (current.References == 1)
            {
                // A direct native teardown may already have dropped the slot ref.
                // The old module address may no longer identify a live allocation.
                attached.TryRemove(module, out _);
                detached(module);
                Retire(current);
                return;
            }
            if (BaseHandle(module) != current.Handle)
                throw new InvalidDataException("Base physics slot changed outside its unload boundary");
            // Native Load unloads the old slot before acquiring the new one. The
            // extra hold bridges that gap even when this view is the sole source owner.
            Hold(current.Source);
            var priorModule = Interlocked.Exchange(ref changingModule, module);
            try
            {
                load(module, current.Source, 0);
                if (BaseHandle(module) != current.Source)
                    throw new InvalidDataException("Native loader did not restore original physics");
            }
            finally
            {
                Volatile.Write(ref changingModule, priorModule);
                release(current.Source);
            }
        }
    }

    internal void RestoreAll()
    {
        lock (Gate)
        {
            List<Exception>? errors = null;
            foreach (var module in attached.Keys)
            {
                try { Restore(module); }
                catch (Exception ex) { (errors ??= []).Add(ex); }
            }
            CollectRetired();
            if (errors != null) throw new AggregateException("Failed to restore base physics", errors);
        }
    }
}
