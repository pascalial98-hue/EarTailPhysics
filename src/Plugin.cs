using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Command;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace EarTailPhysics;

public sealed class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly Settings cfg;
    private readonly PhysicsRuntime runtime;
    private readonly CollisionOverlay overlay;
    private int highlighted = -1;
    private bool open;
    private static readonly string[] EarNames = ["1 · 轻柔", "2 · 自然", "3 · 明显", "4 · 强烈"];
    private static readonly string[] TailNames = ["短毛猫尾", "长毛猫尾 / 狐尾", "龙尾"];
    private static readonly string[] StrengthNames = ["原版对照", "轻惯性", "强惯性"];
    private static readonly string[] BodyNames = ["关闭", "细尾 · 中后段", "中等 · 中后段", "粗尾 · 中后段"];

    public Plugin(IDalamudPluginInterface pi, IObjectTable objects, IClientState client, IFramework framework,
        IGameInteropProvider interop, ICommandManager commands, IPluginLog log, IGameGui gameGui, ICondition conditions)
    {
        this.pi = pi; this.commands = commands;
        cfg = (pi.GetPluginConfig() as Settings ?? new Settings()).Snapshot();
        overlay = new CollisionOverlay(gameGui);
        runtime = new PhysicsRuntime(cfg, objects, client, framework, interop, log, conditions);
        commands.AddHandler("/earphysics", new CommandInfo((_, _) => open = !open) { HelpMessage = "耳尾物理增强：打开独立设置" });
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenConfigUi += Open;
        pi.UiBuilder.OpenMainUi += Open;
    }

    private void Open() => open = true;

    private void Draw()
    {
        highlighted = -1;
        DrawSettings();
        var show = open && cfg.ShowCollisionPreview && cfg.Enabled && cfg.TailStrength > 0;
        runtime.SetPreviewVisible(show);
        if (show) overlay.Draw(runtime.Preview, highlighted);
    }

    private void DrawSettings()
    {
        if (!open) return;
        ImGui.SetNextWindowSize(new Vector2(530, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("耳尾物理增强 · 0.1.6 测试版", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        { ImGui.End(); return; }
        try
        {
            ImGui.TextWrapped("独立运行，无需物理 Mod。保留耳尾外观包，测试前关闭旧耳尾物理包。");
            ImGui.Separator();
            ImGui.BeginDisabled(!runtime.Supported);
            var changed = ImGui.Checkbox("启用耳尾物理增强", ref cfg.Enabled);
            changed |= ImGui.Checkbox("启用耳朵物理", ref cfg.Ears);
            ImGui.BeginDisabled(!cfg.Ears);
            changed |= ImGui.Combo("耳朵响应强度", ref cfg.EarGrade, EarNames, EarNames.Length);
            ImGui.EndDisabled();
            changed |= ImGui.Combo("尾巴类型", ref cfg.TailType, TailNames, TailNames.Length);
            changed |= ImGui.Combo("尾巴惯性", ref cfg.TailStrength, StrengthNames, StrengthNames.Length);
            ImGui.BeginDisabled(cfg.TailStrength == 0);
            changed |= ImGui.Combo("身体碰撞（试验）", ref cfg.BodyCollision, BodyNames, BodyNames.Length);
            changed |= ImGui.Checkbox("地面碰撞（试验）", ref cfg.GroundCollision);
            changed |= ImGui.Checkbox("场景碰撞（试验）", ref cfg.SceneCollision);
            if (ImGui.CollapsingHeader("尾巴节点碰撞与预览", ImGuiTreeNodeFlags.DefaultOpen))
            {
                changed |= ImGui.Checkbox("保护尾根", ref cfg.ProtectRoot);
                ImGui.SameLine();
                changed |= ImGui.Checkbox("保护靠根段", ref cfg.ProtectProximal);
                if (!cfg.ProtectRoot || !cfg.ProtectProximal)
                    ImGui.TextWrapped("取消保护后该节点也参与地面／场景接触，可能使尾根偏离连接处或抖动。");
                for (var i = 0; i < 5; i++)
                {
                    ImGui.BeginDisabled(cfg.IsProtected(i));
                    var centimeters = cfg.GetConfiguredRadius(i) * 100;
                    if (ImGui.SliderFloat(CollisionOverlay.NodeNames[i], ref centimeters, 0, 30, "%.1f cm"))
                    {
                        cfg.ContactNodeRadii![i] = centimeters / 100;
                        changed = true;
                    }
                    if (ImGui.IsItemHovered() || ImGui.IsItemActive()) highlighted = i;
                    ImGui.EndDisabled();
                }
                changed |= ImGui.Checkbox("显示碰撞体预览（设置窗口打开时）", ref cfg.ShowCollisionPreview);
                ImGui.TextWrapped("半径实时生效，用于地面和场景；身体碰撞仍使用上方预设。半径 0 关闭该节点接触，骨链约束仍会联动。");
                if (cfg.ShowCollisionPreview)
                {
                    ImGui.TextWrapped("球体表示节点接触范围，胶囊表示骨段扫掠范围。黄色为当前调整项，橙色为接触，灰色为保护／关闭或未启用碰撞。线框可透过场景显示。");
                    if (runtime.Preview is not { Fresh: true })
                        ImGui.TextWrapped("等待可预览的尾链；需启用尾巴惯性，GPose 暂停时无预览。");
                }
            }
            ImGui.EndDisabled();
            changed |= ImGui.Checkbox("随机耳尾小动作（试验）", ref cfg.RandomIdleMotions);
            if (cfg.RandomIdleMotions)
                ImGui.TextWrapped("静止片刻后，随机轻摆尾巴、轻挑尾尖或动耳；移动／转身时淡出。遵循耳朵开关和尾巴档位，原版对照尾巴不参与。");
            changed |= ImGui.Checkbox("GPose 中暂停接管", ref cfg.PauseInGPose);
            ImGui.EndDisabled();
            if (changed) { pi.SavePluginConfig(cfg); runtime.Configure(cfg); }
            ImGui.Separator();
            ImGui.TextWrapped(runtime.Status);
            if (cfg.RandomIdleMotions) ImGui.TextWrapped(runtime.IdleStatus);
            if (cfg.GroundCollision || cfg.SceneCollision)
            {
                ImGui.TextUnformatted($"场景查询 {runtime.Queries} 次 · 发生接触 {runtime.Contacts} 次");
                ImGui.TextWrapped(runtime.ContactStatus);
            }
            if (ImGui.CollapsingHeader("运行状态（排查）"))
            {
                ImGui.TextUnformatted($"自身角色 {(runtime.LocalCharacterFound ? "已找到" : "等待")} · 场景模块 {(runtime.SceneAvailable ? "已找到" : "等待")}");
                ImGui.TextUnformatted($"环境风入口 {runtime.WindCallbacks} 次（自身 {runtime.LocalWindCallbacks}）");
                ImGui.TextUnformatted($"任务入口 {runtime.JobCallbacks} 次（自身 {runtime.LocalJobCallbacks}）· 已应用 {runtime.Rebuilds} 次");
                ImGui.TextWrapped(runtime.TailStatus);
                ImGui.TextUnformatted($"尾链更新 {runtime.TailPoseCallbacks} 次 · 原生风输入 {runtime.TailWind:F3}");
                ImGui.TextUnformatted($"原生命中：场景 {runtime.SceneHits} · 地面 {runtime.GroundHits} · 无效结果 {runtime.InvalidHits}");
                ImGui.TextUnformatted($"三角面法线补全 {runtime.TriangleNormals} 次");
                if (cfg.RandomIdleMotions)
                    ImGui.TextUnformatted($"随机动作 {runtime.IdleEvents} 次 · 动作目标应用 {runtime.IdleTargetUpdates} 次");
                if (runtime.Preview is { Fresh: true } && float.IsFinite(runtime.PoseOffset))
                    ImGui.TextUnformatted($"物理点与动画混合位置最大偏差 {runtime.PoseOffset * 100:F1} cm");
                ImGui.TextWrapped("风输入由游戏环境决定，不是强度档位。若仍停在等待，请记录这里的状态以便定位。");
            }
            ImGui.TextWrapped("首版仅作用于自身。支持标准尾链及猫耳骨骼；额外骨链与任意模型自动适配尚未提供。");
            ImGui.TextWrapped("物理设置在下一次角色物理更新时应用。首次测试请先对比原动画、跑停和转身，再打开碰撞。");
            if (ImGui.Button("重新应用设置")) runtime.Configure(cfg, true);
        }
        finally { ImGui.End(); }
    }

    public void Dispose()
    {
        pi.UiBuilder.Draw -= Draw;
        pi.UiBuilder.OpenConfigUi -= Open;
        pi.UiBuilder.OpenMainUi -= Open;
        commands.RemoveHandler("/earphysics");
        runtime.Dispose();
    }
}
