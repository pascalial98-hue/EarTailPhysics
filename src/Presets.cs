namespace EarTailPhysics;

// Numerical tuning from the local v0.2.3 / v1.1.1 experiments; no PHYB assets embedded.
public static class Presets
{
    public readonly record struct Ear(float Damping, float Speed, float Attraction, float Wind);
    public readonly record struct Tail(float Damping, float Speed, float[] Attraction, float Terminal, float[] Angles);
    // Wind alone is halved in 0.1.5; movement response and idle-ear angles stay intact.
    public static readonly Ear[] Ears = [new(.18f, 3, .1f, .003f), new(.14f, 3.5f, .075f, .006f),
        new(.1f, 4, .05f, .009f), new(.08f, 4, .0325f, .0135f)];
    public static readonly Tail[,] Tails = {
        { new(.14f, 4, [.5f,.35f,.25f], .04f, [30,40,25]), new(.095f,4,[.28f,.18f,.12f],.015f,[45,55,35]) },
        { new(.20f,2.8f,[.30f,.20f,.14f],.02f,[45,55,35]), new(.14f,2.5f,[.15f,.10f,.10f],.005f,[55,65,45]) },
        { new(.20f,3.3f,[.65f,.32f,.18f],.025f,[22,32,28]), new(.15f,2.8f,[.40f,.20f,.12f],.01f,[32,45,38]) }
    };
    public static readonly float[][] Radii = [[], [.022f,.016f,.010f], [.045f,.030f,.016f], [.080f,.050f,.025f]];
}
