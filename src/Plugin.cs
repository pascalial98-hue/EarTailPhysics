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
    private bool open;
    private static readonly string[] EarNames = ["1 · 轻柔", "2 · 自然", "3 · 明显", "4 · 强烈"];
    private static readonly string[] TailNames = ["短毛猫尾", "长毛猫尾 / 狐尾", "龙尾"];
    private static readonly string[] StrengthNames = ["原版对照", "轻惯性", "强惯性"];
    private static readonly string[] BodyNames = ["关闭", "细尾 · 中后段", "中等 · 中后段", "粗尾 · 中后段"];

    public Plugin(IDalamudPluginInterface pi, IObjectTable objects, IClientState client, IFramework framework,
        IGameInteropProvider interop, ICommandManager commands, IPluginLog log)
    {
        this.pi = pi; this.commands = commands;
        cfg = (pi.GetPluginConfig() as Settings ?? new Settings()).Snapshot();
        runtime = new PhysicsRuntime(cfg, objects, client, framework, interop, log);
        commands.AddHandler("/earphysics", new CommandInfo((_, _) => open = !open) { HelpMessage = "耳尾物理增强：打开独立设置" });
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenConfigUi += Open;
        pi.UiBuilder.OpenMainUi += Open;
    }

    private void Open() => open = true;

    private void Draw()
    {
        if (!open) return;
        ImGui.SetNextWindowSize(new Vector2(530, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("耳尾物理增强 · 0.1.0 测试版", ref open, ImGuiWindowFlags.AlwaysAutoResize))
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
            if (cfg.GroundCollision || cfg.SceneCollision)
            {
                changed |= ImGui.SliderFloat("尾巴接触半径", ref cfg.ContactRadius, .005f, .15f, "%.3f 米");
                ImGui.TextWrapped("仅中后段参与。半径需按尾巴粗细调整；只识别游戏已有碰撞体。");
            }
            ImGui.EndDisabled();
            changed |= ImGui.Checkbox("GPose 中暂停接管", ref cfg.PauseInGPose);
            ImGui.EndDisabled();
            if (changed) { pi.SavePluginConfig(cfg); runtime.Configure(cfg); }
            ImGui.Separator();
            ImGui.TextWrapped(runtime.Status);
            if (cfg.GroundCollision || cfg.SceneCollision)
                ImGui.TextUnformatted($"场景查询 {runtime.Queries} 次 · 发生接触 {runtime.Contacts} 次");
            ImGui.TextWrapped("首版仅作用于自身。支持标准尾链及猫耳骨骼；额外骨链与任意模型自动适配尚未提供。");
            ImGui.TextWrapped("物理设置在下一次角色物理更新时应用。首次测试请先对比原动画、跑停和转身，再打开碰撞。");
            if (ImGui.Button("重新应用设置")) runtime.Configure(cfg);
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
