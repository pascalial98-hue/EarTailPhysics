using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace EarTailPhysics;

public sealed class CollisionOverlay(IGameGui gameGui)
{
    public static readonly string[] NodeNames = ["尾根 · b", "靠根段 · c", "中段 · d", "后段 · e", "末端 · 虚拟节点"];
    private static readonly uint[] Colors = [0xFFDFB477, 0xFF78D5AB, 0xFFFFC26C, 0xFFDC8FD9, 0xFF8DD7ED];

    public void Draw(CollisionPreview? preview, int highlighted)
    {
        if (preview is not { Fresh: true }) return;
        var draw = ImGui.GetBackgroundDrawList();
        var viewport = ImGui.GetMainViewport();
        draw.PushClipRect(viewport.Pos, viewport.Pos + viewport.Size, true);
        try
        {
            foreach (var sweep in preview.Sweeps)
            {
                var color = sweep.NodeIndex == highlighted ? 0xFF40FFFFu
                    : sweep.Observed ? (Colors[sweep.NodeIndex] & 0xFFFFFF) | 0x75000000 : 0x75888888;
                foreach (var line in ColliderWireGeometry.Capsule(sweep)) Line(line, color, 1);
            }
            for (var i = 0; i < preview.Nodes.Length; i++)
            {
                var node = preview.Nodes[i];
                var color = node.Protected || node.Radius == 0 ? 0xFF999999u : !preview.Active ? 0xFFBBBBBBu : Colors[i];
                if (node.Touching) color = 0xFF4080FF;
                if (i == highlighted) color = 0xFF40FFFF;
                foreach (var line in ColliderWireGeometry.Sphere(node.Position, node.Radius))
                    Line(line, color, i == highlighted ? 2 : 1.3f);
                if (!Project(node.Position, out var screen)) continue;
                draw.AddCircleFilled(screen, 3, color);
                var suffix = node.Protected ? "保护" : node.Radius == 0 ? "关闭" : $"{node.Radius * 100:F1} cm";
                var label = $"{NodeNames[i]}  {suffix}";
                draw.AddText(screen + new Vector2(9, -15), 0xFF000000, label);
                draw.AddText(screen + new Vector2(8, -16), color, label);
            }
        }
        finally { draw.PopClipRect(); }

        void Line(WireLine line, uint color, float thickness)
        {
            if (Project(line.Start, out var a) && Project(line.End, out var b)) draw.AddLine(a, b, color, thickness);
        }
    }

    private bool Project(Vector3 world, out Vector2 screen)
        => gameGui.WorldToScreen(world, out screen) && float.IsFinite(screen.X) && float.IsFinite(screen.Y);
}
