using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace EarTailPhysics;

public unsafe sealed class PhysicsRuntime : IDisposable
{
    public const string SupportedHash = "52042b60631cf2c48851b3aaee36e9ec4fedd6d7f2c68f0e9a8c3befb0af9f98";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void JobDelegate(nint module, nint updater);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LoadDelegate(nint module, nint resource, uint slot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint RefDelegate(nint resource, nint unused, int unused2);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ushort BoneDelegate(nint skeleton, byte* name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PoseDelegate(nint chain, nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FinalizeDelegate(nint chain, byte fixedLength, float blend);

    private readonly IObjectTable objects;
    private readonly IClientState client;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private Hook<JobDelegate>? jobHook;
    private Hook<PoseDelegate>? poseHook;
    private Hook<FinalizeDelegate>? finalizeHook;
    private LoadDelegate? load;
    private RefDelegate? addRef, release;
    private BoneDelegate? findBone;
    private Settings settings;
    private int generation = 1;
    private int appliedGeneration;
    private nint currentModule, currentSkeleton, currentHandle, currentVector;
    private nint localDraw, bgModule, contactChain;
    private bool inPose;
    private DescriptorBuilder? view;
    private SceneContacts.State? contactState;
    private SceneContacts? contacts;
    private CollisionPreview? preview;
    private bool previewVisible;
    private bool failed, disposed;
    public string Status { get; private set; } = "等待角色物理更新";
    public bool Supported { get; }
    public long Queries => contacts == null ? 0 : Interlocked.Read(ref contacts.Queries);
    public long Contacts => contacts == null ? 0 : Interlocked.Read(ref contacts.Contacts);
    public long Rebuilds { get; private set; }
    public CollisionPreview? Preview => Volatile.Read(ref preview);
    public void SetPreviewVisible(bool visible) => Volatile.Write(ref previewVisible, visible);

    public PhysicsRuntime(Settings settings, IObjectTable objects, IClientState client, IFramework framework,
        IGameInteropProvider interop, IPluginLog log)
    {
        this.settings = settings.Snapshot(); this.objects = objects; this.client = client;
        this.framework = framework; this.log = log;
        var main = Process.GetCurrentProcess().MainModule ?? throw new InvalidOperationException("Process image unavailable");
        using var file = File.OpenRead(main.FileName);
        var hash = Convert.ToHexStringLower(SHA256.HashData(file));
        if (hash != SupportedHash)
        {
            Status = "当前客户端尚未适配，物理接管已停用（首版支持国服 2026.11.03）";
            log.Warning("EarTailPhysics unsupported executable SHA256: {Hash}", hash);
            return;
        }
        var b = main.BaseAddress;
        load = Marshal.GetDelegateForFunctionPointer<LoadDelegate>(b + 0x18B9800);
        addRef = Marshal.GetDelegateForFunctionPointer<RefDelegate>(b + 0x2FEA70);
        release = Marshal.GetDelegateForFunctionPointer<RefDelegate>(b + 0x2FEA40);
        findBone = Marshal.GetDelegateForFunctionPointer<BoneDelegate>(b + 0x25A5B0);
        contacts = new SceneContacts(b + 0x5F1670, b + 0x5F1790);
        try
        {
            jobHook = interop.HookFromAddress<JobDelegate>(b + 0x18B96D0, OnCreateJobs);
            poseHook = interop.HookFromAddress<PoseDelegate>(b + 0x1BE66E0, OnPose);
            finalizeHook = interop.HookFromAddress<FinalizeDelegate>(b + 0x1BE7560, OnFinalize);
            finalizeHook.Enable(); poseHook.Enable(); jobHook.Enable();
            framework.Update += OnFramework;
            Supported = true;
        }
        catch { jobHook?.Dispose(); poseHook?.Dispose(); finalizeHook?.Dispose(); throw; }
    }

    public void Configure(Settings value, bool forceRebuild = false)
    {
        var next = value.Snapshot();
        var rebuild = forceRebuild || failed || !Volatile.Read(ref settings).SameNativeConfiguration(next);
        failed = false;
        Volatile.Write(ref settings, next);
        if (rebuild) Interlocked.Increment(ref generation);
    }

    private void OnFramework(IFramework _)
    {
        var actor = objects.LocalPlayer;
        var draw = actor == null ? 0 : *(nint*)(actor.Address + 0x100);
        Volatile.Write(ref localDraw, draw);
        var pose = client.IsGPosing;
        if (pose != inPose) { inPose = pose; Interlocked.Increment(ref generation); }
        var game = GameFramework.Instance();
        Volatile.Write(ref bgModule, game == null ? 0 : (nint)game->BGCollisionModule);
    }

    private void OnCreateJobs(nint module, nint updater)
    {
        if (!disposed && !failed)
        {
            try
            {
                var draw = Volatile.Read(ref localDraw);
                // The callback's module is live; do not dereference a cached draw pointer after a redraw.
                var skeleton = *(nint*)(module + 0x98);
                if (draw != 0 && skeleton != 0 && *(nint*)(skeleton + 0xB8) == draw)
                {
                    var handle = *(nint*)(module + 0x190);
                    var vector = *(nint*)(module + 0xA0);
                    var revision = Volatile.Read(ref generation);
                    if (handle != 0 && (module != currentModule || skeleton != currentSkeleton || handle != currentHandle
                        || vector != currentVector || revision != appliedGeneration))
                    {
                        var cfg = Volatile.Read(ref settings).Snapshot();
                        if (cfg.PauseInGPose && inPose) cfg.Enabled = false;
                        Replace(module, skeleton, handle, cfg);
                        appliedGeneration = revision;
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;
                Status = "接管暂停：" + ex.Message;
                Volatile.Write(ref contactChain, 0);
                log.Error(ex, "EarTailPhysics stopped after a descriptor validation failure");
                try { Restore(); } catch (Exception restoreError) { log.Error(restoreError, "Automatic restore failed; redraw the local character"); }
                Status = "接管暂停：" + ex.Message;
            }
        }
        jobHook!.Original(module, updater);
    }

    private bool BoneExists(nint skeleton, string bone)
    {
        byte* name = stackalloc byte[32];
        NativeMemoryView.SetName(name, bone);
        return findBone!(skeleton, name) != ushort.MaxValue;
    }

    private void Replace(nint module, nint skeleton, nint handle, Settings cfg)
    {
        if (*(uint*)(handle + 0xAC) == 0) throw new InvalidDataException("Unreferenced physics resource");
        var next = new DescriptorBuilder((byte*)handle, cfg, BoneExists(skeleton, "j_mimi_l") && BoneExists(skeleton, "j_mimi_r"));
        var adopted = false;
        var held = false;
        Volatile.Write(ref contactChain, 0);
        Volatile.Write(ref preview, null);
        try
        {
            // Hold the REAL resource before native Load releases the module's old reference.
            // The private descriptor is a non-owning view; its synthetic refcount never reaches zero.
            if (addRef!(handle, 0, 0) != handle) throw new InvalidDataException("Resource identity changed");
            held = true;
            *(uint*)(next.Handle + 0xAC) = 1;
            load!(module, (nint)next.Handle, 0);
            if (*(nint*)(module + 0x190) != (nint)next.Handle)
                throw new InvalidDataException("Native loader did not adopt descriptor");
            // Native Load copies node/collider records. Retain the real resource for all unmodified
            // data and EPHB references; never allow a fabricated handle to reach a native destructor.
            *(nint*)(module + 0x190) = handle;
            adopted = true;
            held = false; // this reference now belongs to the module
            var old = view;
            view = next;
            old?.Dispose();
            currentModule = module; currentSkeleton = skeleton; currentHandle = handle;
            currentVector = *(nint*)(module + 0xA0);
            Rebuilds++;
            contactState = null;
            if (next.HasTail)
            {
                var begin = *(nint*)(module + 0xA0);
                var end = *(nint*)(module + 0xA8);
                if (begin != 0 && (end - begin) / 8 > next.TailSimulatorIndex)
                {
                    var sim = *(nint*)(begin + next.TailSimulatorIndex * 8);
                    var chains = *(nint*)(sim + 0x78);
                    var chainEnd = *(nint*)(sim + 0x80);
                    if (chains != 0 && (chainEnd - chains) / 8 > next.TailChainIndex)
                    {
                        var tail = *(nint*)(chains + next.TailChainIndex * 8);
                        if (*(ushort*)(tail + 0xE8) == 5)
                        {
                            contactState = new SceneContacts.State(module);
                            Volatile.Write(ref contactChain, tail);
                        }
                    }
                }
            }
            Status = !cfg.Enabled ? "已恢复加载来源的原始物理" : $"已接管：耳朵 {(next.HasEars ? "✓" : "未启用／骨链不支持")} · 尾巴 {(next.HasTail ? "✓" : "原版／骨链不支持")}";
            log.Information("EarTailPhysics rebuilt local physics: ears={Ears}, tail={Tail}, ground={Ground}, scene={Scene}",
                next.HasEars, next.HasTail, cfg.GroundCollision, cfg.SceneCollision);
        }
        finally
        {
            if (held) release!(handle, 0, 0);
            if (!adopted) next.Dispose();
        }
    }

    private SceneContacts.State? OwnedContactState(nint chain)
    {
        if (disposed || failed || chain != Volatile.Read(ref contactChain) || contactState is not { } state) return null;
        // Validate ownership from the LIVE callback object, never a stale cached draw pointer.
        var skeleton = *(nint*)(chain + 0x10);
        var owner = skeleton == 0 ? 0 : *(nint*)(skeleton + 0xB8);
        return owner != 0 && owner == Volatile.Read(ref localDraw) && *(nint*)(owner + 0x158) == state.Module ? state : null;
    }

    private void StopContacts(Exception ex)
    {
        Volatile.Write(ref contactChain, 0);
        Volatile.Write(ref preview, null);
        Status = "场景碰撞暂停：" + ex.Message;
        log.Error(ex, "EarTailPhysics scene contact disabled after error");
    }

    private void OnPose(nint chain, nint context)
    {
        try
        {
            if (OwnedContactState(chain) is { } state)
            {
                // Keep one immutable profile across all substeps and the corresponding preview.
                var cfg = state.FrameSettings ??= Volatile.Read(ref settings);
                contacts!.Resolve((byte*)chain, state, cfg, Volatile.Read(ref bgModule));
            }
        }
        catch (Exception ex) { StopContacts(ex); }
        // Native pose construction consumes corrected particles, including an opted-in root.
        // Correcting particles only in OnFinalize is too late for the native node matrices.
        poseHook!.Original(chain, context);
    }

    private void OnFinalize(nint chain, byte fixedLength, float blend)
    {
        SceneContacts.State? state = null;
        try { state = OwnedContactState(chain); }
        catch (Exception ex) { StopContacts(ex); }
        // Contact projection already bounded the lengths. Preserve corrected translations;
        // native pose construction and finalization continue to own bone rotations/blending.
        finalizeHook!.Original(chain, state?.ContactAdjusted == true ? (byte)0 : fixedLength, blend);
        if (state == null) return;
        try
        {
            var cfg = state.FrameSettings ?? Volatile.Read(ref settings);
            Volatile.Write(ref preview, Volatile.Read(ref previewVisible) && cfg.ShowCollisionPreview
                ? SceneContacts.CapturePreview((byte*)chain, state, cfg, Volatile.Read(ref bgModule) != 0) : null);
        }
        catch (Exception ex) { StopContacts(ex); }
        finally { state.ContactAdjusted = false; state.ContactMask = 0; state.FrameSettings = null; }
    }

    private void Restore()
    {
        Volatile.Write(ref contactChain, 0);
        Volatile.Write(ref preview, null);
        var actor = objects.LocalPlayer;
        if (actor == null || currentModule == 0) return;
        var draw = *(nint*)(actor.Address + 0x100);
        if (draw == 0 || *(nint*)(draw + 0x158) != currentModule) return;
        if (*(nint*)(currentModule + 0x98) != currentSkeleton || *(nint*)(currentModule + 0x190) != currentHandle) return;
        Replace(currentModule, currentSkeleton, currentHandle, new Settings { Enabled = false });
    }

    public void Dispose()
    {
        if (disposed) return;
        if (Supported)
        {
            framework.Update -= OnFramework;
            try
            {
                if (framework.IsInFrameworkUpdateThread) Restore();
                else framework.RunOnFrameworkThread(Restore).GetAwaiter().GetResult();
            }
            catch (Exception ex) { log.Error(ex, "Restore failed; redraw the local character to restore original physics"); }
        }
        disposed = true;
        jobHook?.Disable(); poseHook?.Disable(); finalizeHook?.Disable();
        jobHook?.Dispose(); poseHook?.Dispose(); finalizeHook?.Dispose();
        view?.Dispose();
    }
}
