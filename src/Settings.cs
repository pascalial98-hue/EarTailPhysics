using Dalamud.Configuration;

namespace EarTailPhysics;

[Serializable]
public sealed class Settings : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled = true;
    public bool Ears = true;
    public int EarGrade = 1;
    public int TailType = 1;
    public int TailStrength = 2;
    public int BodyCollision = 0;
    public bool GroundCollision;
    public bool SceneCollision;
    public float ContactRadius = 0.045f;
    public bool PauseInGPose = true;

    public Settings Snapshot()
    {
        var s = (Settings)MemberwiseClone();
        s.EarGrade = Math.Clamp(s.EarGrade, 0, 3);
        s.TailType = Math.Clamp(s.TailType, 0, 2);
        s.TailStrength = Math.Clamp(s.TailStrength, 0, 2);
        s.BodyCollision = Math.Clamp(s.BodyCollision, 0, 3);
        s.ContactRadius = float.IsFinite(s.ContactRadius) ? Math.Clamp(s.ContactRadius, 0.005f, 0.15f) : 0.045f;
        return s;
    }
}
