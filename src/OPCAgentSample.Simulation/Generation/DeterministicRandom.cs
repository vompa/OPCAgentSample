namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Leitet aus dem Seed und einem fachlichen Schlüssel (Station, Schicht …) einen eigenen <see cref="Random"/> ab.
/// Damit ist jedes Teilstück (z. B. eine Schicht einer Station) unabhängig vom gewählten Ausgabefenster
/// reproduzierbar: ein Fenster liefert dieselben Werte wie der Gesamtlauf.
/// </summary>
internal static class DeterministicRandom
{
    public static Random For(int seed, string key, long discriminator)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in key)
        {
            hash = (hash ^ character) * 1099511628211UL;
        }

        var mixed = SplitMix(hash ^ SplitMix((ulong)seed) ^ SplitMix((ulong)discriminator));
        return new Random((int)(mixed & int.MaxValue));
    }

    /// <summary>Normalverteilter Wert (Box-Muller); verbraucht immer genau zwei Zufallszahlen.</summary>
    public static double Gaussian(this Random random, double sigma)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Normalverteilt und auf ±<paramref name="limit"/> begrenzt.</summary>
    public static double BoundedGaussian(this Random random, double sigma, double limit) =>
        Math.Clamp(random.Gaussian(sigma), -limit, limit);

    private static ulong SplitMix(ulong value)
    {
        var z = value + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
