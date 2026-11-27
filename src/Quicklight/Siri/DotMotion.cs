namespace Quicklight.Siri;

/// <summary>
/// Where each of the ring's six dots is at time t (ms). Three scenes take turns: a wave of size and brightness running
/// round a breathing ring, the ring turning once about its vertical axis like a globe, and a ripple travelling round
/// the ring's rim. All three spin the ring at the same rate and start from the same hexagon, so a switch only moves
/// radius, size and brightness: the dots keep their angles (each is paired with the nearest dot of the next scene,
/// and its angle follows the short way round).
/// Pure maths, no WPF: positions are in design units (a 60 wide ring), angles in radians.
/// </summary>
internal static class DotMotion
{
    public const int Dots = 6;
    public const double Orbit = 22.5, HeadSize = 10.5;

    /// <summary>A dot's place: Angle round the ring, Radius from its centre, Size across, Opacity 0..1.</summary>
    public readonly record struct Pose(double Angle, double Radius, double Size, double Opacity);

    const double Tau = 2 * Math.PI, Top = -Math.PI / 2;
    const double Spin = 0.0004; // rad/ms, shared by every scene so their dots line up
    const double Blend = 800;
    // Hold per scene: wave, globe, ripple. The globe's turn takes exactly its hold, so it hands over as a flat ring.
    static readonly double[] Hold = { 4000, 2400, 4000 };
    static readonly double Cycle = Hold.Sum() + Hold.Length * Blend;

    public static Pose At(int i, double t)
    {
        double u = Mod(t, Cycle), start = 0;
        int k = 0;
        while (u >= start + Hold[k] + Blend) start += Hold[k++] + Blend;
        double local = u - start;
        var a = Scene(k, i, t, local);
        if (local < Hold[k]) return a;

        // Blending into the next scene. Pair dot i with the next scene's nearest dot as it was when the blend began, then
        // ease radius, size and brightness over, and the angle by the short way round (kept on one branch as it drifts).
        int next = (k + 1) % Hold.Length;
        double t0 = t - (local - Hold[k]), w = Smooth((local - Hold[k]) / Blend);
        double a0 = Scene(k, i, t0, Hold[k]).Angle;
        int j = i;
        double best = Math.Abs(Wrap(Scene(next, i, t0, 0).Angle - a0));
        for (int c = 0; c < Dots; c++)
        {
            double d = Math.Abs(Wrap(Scene(next, c, t0, 0).Angle - a0));
            if (d < best - 1e-6) { best = d; j = c; }
        }
        var b = Scene(next, j, t, 0);
        double d0 = Wrap(Scene(next, j, t0, 0).Angle - a0), rw = Wrap(b.Angle - a.Angle);
        double turn = rw + Tau * Math.Round((d0 - rw) / Tau);
        return new Pose(a.Angle + turn * w, Lerp(a.Radius, b.Radius, w), Lerp(a.Size, b.Size, w), Lerp(a.Opacity, b.Opacity, w));
    }

    static Pose Scene(int scene, int i, double t, double local) => scene switch
    {
        0 => Wave(i, t),
        1 => Globe(i, t, Smooth(Math.Clamp(local / Hold[1], 0, 1))),
        _ => Ripple(i, t),
    };

    // The ring breathes in and out while brightness and size run round it.
    static Pose Wave(int i, double t)
    {
        double s = .5 + .5 * Math.Cos(Tau * Mod(t / 1300 - (double)i / Dots, 1));
        double breath = .5 - .5 * Math.Cos(Tau * t / 2600);
        return new Pose(Top + i * Tau / Dots + t * Spin, Orbit * (.6 + .4 * breath), HeadSize * (.45 + .55 * s), .25 + .75 * s);
    }

    // Turn (0..1) is one full turn about the vertical axis (nearer dots bigger and brighter) with half a turn of spin on top;
    // both start and stop gently, and it ends where it began, a flat ring.
    static Pose Globe(int i, double t, double turn)
    {
        double a = Top + i * Tau / Dots + t * Spin + Math.PI * turn, ph = Tau * turn;
        double x = Orbit * Math.Cos(a), y = Orbit * Math.Sin(a), z = x * Math.Sin(ph) / Orbit;
        x *= Math.Cos(ph);
        return new Pose(Math.Atan2(y, x), Math.Sqrt(x * x + y * y), HeadSize * (.85 + .3 * z), .55 + .45 * (z + 1) / 2);
    }

    // Three crests run round the rim; the swell itself breathes.
    static Pose Ripple(int i, double t)
    {
        double a = Top + i * Tau / Dots + t * Spin;
        double amp = .12 + .1 * (.5 - .5 * Math.Cos(Tau * t / 4200)), w = Math.Sin(3 * a - t * .0028);
        return new Pose(a, Orbit * (1 + amp * w), HeadSize * (.72 + .18 * w), .7 + .3 * w);
    }

    static double Mod(double a, double n) => (a % n + n) % n;
    static double Wrap(double a) => Mod(a + Math.PI, Tau) - Math.PI;
    static double Smooth(double c) => c * c * (3 - 2 * c);
    static double Lerp(double a, double b, double w) => a + (b - a) * w;
}
