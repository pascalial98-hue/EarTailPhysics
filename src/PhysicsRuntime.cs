using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace EarTailPhysics;

public unsafe sealed class PhysicsRuntime : IDisposable
{
    public const string SupportedHash = "52042b60631cf2c48851b3aaee36e9ec4fedd6d7f2c68f0e9a8c3befb0af9f98";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void JobDelegate(nint module, nint updater);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WindDelegate(nint controller, nint actor, nint draw, float delta, byte flags);
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
    private Hook<WindDelegate>? windHook;
    private Hook<PoseDelegate>? poseHook;
    private Hook<FinalizeDelegate>? finalizeHook;
    private LoadDelegate? load;
    private RefDelegate? addRef, release;
    private BoneDelegate? findBone;
    private Settings settings;
    private int generation = 1;
    private int appliedGeneration;
    private nint currentModule, currentSkeleton, currentHandle, currentVector, currentFirstSimulator;
    private nint localDraw, bgModule, contactChain;
    private bool inPose;
    private DescriptorBuilder? view;
    private SceneContacts.State? contactState;
    private SceneContacts? contacts;
    private CollisionPreview? preview;
    private bool previewVisible;
    private bool failed, disposed;
    private bool contactsFailed;
    public string Status { get; private set; } = "等待角色物理更新";
    public bool Supported { get; }
    public long Queries => contacts == null ? 0 : Interlocked.Read(ref contacts.Queries);
    public long Contacts => contacts == null ? 0 : Interlocked.Read(ref contacts.Contacts);
    public long Rebuilds { get; private set; }
    private long windCallbacks, jobCallbacks, localWindCallbacks, localJobCallbacks, tailPoseCallbacks;
    public long WindCallbacks => Interlocked.Read(ref windCallbacks);
    public long JobCallbacks => Interlocked.Read(ref jobCallbacks);
    public long LocalWindCallbacks => Interlocked.Read(ref localWindCallbacks);
    public long LocalJobCallbacks => Interlocked.Read(ref localJobCallbacks);
    public long TailPoseCallbacks => Interlocked.Read(ref tailPoseCallbacks);
    public bool LocalCharacterFound => Volatile.Read(ref localDraw) != 0;
    public bool SceneAvailable => Volatile.Read(ref bgModule) != 0;
    public string TailStatus { get; private set; } = "等待尾链绑定";
    public float TailWind { get; private set; }
    public bool TailSimulating { get; private set; }
    public string ContactStatus => contactState?.LastResult ?? "等待尾链绑定";
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
            // This per-character callback feeds native environment wind before physics jobs.
            // It provides a second, earlier entry instead of relying solely on CreateJobData.
            windHook = interop.HookFromAddress<WindDelegate>(b + 0x5FC130, OnWind);
            jobHook = interop.HookFromAddress<JobDelegate>(b + 0x18B96D0, OnCreateJobs);
            poseHook = interop.HookFromAddress<PoseDelegate>(b + 0x1BE66E0, OnPose);
            finalizeHook = interop.HookFromAddress<FinalizeDelegate>(b + 0x1BE7560, OnFinalize);
            finalizeHook.Enable(); poseHook.Enable(); jobHook.Enable(); windHook.Enable();
            framework.Update += OnFramework;
            Supported = true;
        }
        catch { windHook?.Dispose(); jobHook?.Dispose(); poseHook?.Dispose(); finalizeHook?.Dispose(); throw; }
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
        if (draw != Volatile.Read(ref localDraw)) Interlocked.Increment(ref generation);
        Volatile.Write(ref localDraw, draw);
        if (!failed && Rebuilds == 0)
            Status = draw == 0 ? "等待可用的自身角色" : "已找到自身，等待物理入口回调";
        var pose = client.IsGPosing;
        if (pose != inPose) { inPose = pose; Interlocked.Increment(ref generation); }
        var game = GameFramework.Instance();
        Volatile.Write(ref bgModule, game == null ? 0 : (nint)game->BGCollisionModule);
    }

    private void OnCreateJobs(nint module, nint updater)
    {
        Interlocked.Increment(ref jobCallbacks);
        ApplyAtBoundary(module, false);
        jobHook!.Original(module, updater);
    }

    private void OnWind(nint controller, nint actor, nint draw, float delta, byte flags)
    {
        Interlocked.Increment(ref windCallbacks);
        var module = RuntimeBinding.ModuleForDraw(draw, Volatile.Read(ref localDraw));
        if (module != 0) ApplyAtBoundary(module, true);
        // Always let the game calculate its current direction, gusts and per-group variation.
        windHook!.Original(controller, actor, draw, delta, flags);
        try
        {
            if (!disposed && !failed && module != 0 && module == currentModule) BindTail(module);
        }
        catch (Exception ex) { StopContacts(ex); }
    }

    private void ApplyAtBoundary(nint module, bool windBoundary)
    {
        if (!disposed && !failed)
        {
            try
            {
                var draw = Volatile.Read(ref localDraw);
                // The callback's module is live; do not dereference a cached draw pointer after a redraw.
                if (RuntimeBinding.OwnsModule(module, draw, out var skeleton))
                {
                    if (windBoundary) Interlocked.Increment(ref localWindCallbacks);
                    else Interlocked.Increment(ref localJobCallbacks);
                    var handle = *(nint*)(module + 0x190);
                    var vector = *(nint*)(module + 0xA0);
                    var first = vector == 0 || *(nint*)(module + 0xA8) <= vector ? 0 : *(nint*)vector;
                    var revision = Volatile.Read(ref generation);
                    if (handle != 0 && (module != currentModule || skeleton != currentSkeleton || handle != currentHandle
                        || vector != currentVector || first != currentFirstSimulator || revision != appliedGeneration))
                    {
                        var cfg = Volatile.Read(ref settings).Snapshot();
                        if (cfg.PauseInGPose && inPose) cfg.Enabled = false;
                        Replace(module, skeleton, handle, cfg);
                        appliedGeneration = revision;
                    }
                    if (handle == 0) Status = "已找到自身物理模块，等待资源加载";
                    else BindTail(module);
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
        var windState = NativeWindState.Capture(module);
        var next = new DescriptorBuilder((byte*)handle, cfg, BoneExists(skeleton, "j_mimi_l") && BoneExists(skeleton, "j_mimi_r"));
        var adopted = false;
        var held = false;
        var windTailIndex = next.HasTail ? next.TailSimulatorIndex : view?.TailSimulatorIndex ?? -1;
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
            windState.Restore(module, windTailIndex);
            currentModule = module; currentSkeleton = skeleton; currentHandle = handle;
            currentVector = *(nint*)(module + 0xA0);
            currentFirstSimulator = currentVector == 0 || *(nint*)(module + 0xA8) <= currentVector ? 0 : *(nint*)currentVector;
            Rebuilds++;
            contactsFailed = false;
            contactState = null;
            BindTail(module);
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

    private void BindTail(nint module)
    {
        if (contactsFailed) return;
        var tail = RuntimeBinding.TailChain(module, view?.TailSimulatorIndex ?? -1, view?.TailChainIndex ?? -1, out var sim);
        if (tail == 0)
        {
            Volatile.Write(ref contactChain, 0);
            Volatile.Write(ref preview, null);
            contactState = null; TailWind = 0; TailSimulating = false;
            TailStatus = view?.HasTail == true ? "尾链尚未就绪" : "尾巴为原版／未启用或骨链不支持";
            return;
        }
        if (tail != Volatile.Read(ref contactChain) || contactState == null)
        {
            contactState = new SceneContacts.State(module);
            Volatile.Write(ref contactChain, tail);
        }
        var wind = *(Vector3*)(sim + 0x40);
        TailWind = ContactMath.Finite(wind) ? wind.Length() : 0;
        TailSimulating = *(byte*)(sim + 0x444) != 0;
        TailStatus = TailSimulating ? "尾链已绑定，原生模拟已启用" : "尾链已绑定，当前动作暂停原生模拟";
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
        contactsFailed = true;
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
                Interlocked.Increment(ref tailPoseCallbacks);
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
        windHook?.Disable(); jobHook?.Disable(); poseHook?.Disable(); finalizeHook?.Disable();
        windHook?.Dispose(); jobHook?.Dispose(); poseHook?.Dispose(); finalizeHook?.Dispose();
        view?.Dispose();
    }
}
