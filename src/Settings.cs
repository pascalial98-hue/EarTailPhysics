using Dalamud.Configuration;

namespace EarTailPhysics;

[Serializable]
public sealed class Settings : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool Enabled = true;
    public bool Ears = true;
    public int EarGrade = 1;
    public int TailType = 1;
    public int TailStrength = 2;
    public int BodyCollision = 0;
    public bool GroundCollision;
    public bool SceneCollision;
    public float ContactRadius = 0.045f;
    // ContactRadius is retained only for migration from version 1.
    public float[]? ContactNodeRadii;
    public bool ShowCollisionPreview;
    public bool ProtectRoot = true;
    public bool ProtectProximal = true;
    public bool PauseInGPose = true;

    public Settings Snapshot()
    {
        var s = (Settings)MemberwiseClone();
        s.EarGrade = Math.Clamp(s.EarGrade, 0, 3);
        s.TailType = Math.Clamp(s.TailType, 0, 2);
        s.TailStrength = Math.Clamp(s.TailStrength, 0, 2);
        s.BodyCollision = Math.Clamp(s.BodyCollision, 0, 3);
        s.ContactRadius = float.IsFinite(s.ContactRadius) ? Math.Clamp(s.ContactRadius, 0.005f, 0.15f) : 0.045f;
        s.ContactNodeRadii = Enumerable.Range(0, 5).Select(GetConfiguredRadius).ToArray();
        s.Version = 2;
        return s;
    }

    public bool IsProtected(int index) => index == 0 ? ProtectRoot : index == 1 && ProtectProximal;
    public float GetContactRadius(int index) => IsProtected(index) ? 0 : GetConfiguredRadius(index);

    public float GetConfiguredRadius(int particleIndex)
    {
        if (particleIndex is < 0 or > 4) return 0;
        var legacy = float.IsFinite(ContactRadius) ? Math.Clamp(ContactRadius, .005f, .15f) : .045f;
        var fallback = legacy * (particleIndex switch { 0 => .55f, 1 => .85f, 2 => 1, 3 => .7f, _ => .45f });
        var value = ContactNodeRadii is { Length: 5 } ? ContactNodeRadii[particleIndex]
            : ContactNodeRadii is { Length: 3 } && particleIndex >= 2 ? ContactNodeRadii[particleIndex - 2] : fallback;
        return float.IsFinite(value) ? Math.Clamp(value, 0, .30f) : fallback;
    }

    // Contact radii and visualization are read per physics callback and require no native rebuild.
    public bool SameNativeConfiguration(Settings other) =>
        Enabled == other.Enabled && Ears == other.Ears && EarGrade == other.EarGrade &&
        TailType == other.TailType && TailStrength == other.TailStrength && BodyCollision == other.BodyCollision &&
        GroundCollision == other.GroundCollision && SceneCollision == other.SceneCollision && PauseInGPose == other.PauseInGPose;
}
