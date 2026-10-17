namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Rauschen für Minutenwerte: je (Seed, Quelle, Minute) ein eigener Zufallsgenerator, damit jedes Fenster dieselben
/// Werte liefert wie der Gesamtlauf.
/// </summary>
internal sealed class MinuteNoise
{
    private readonly Random _random;

    private MinuteNoise(Random random) => _random = random;

    public static MinuteNoise For(int seed, string source, DateTimeOffset minute) =>
        new(DeterministicRandom.For(seed, source + "/min", minute.UtcTicks / TimeSpan.TicksPerMinute));

    /// <summary>Normalverteiltes Rauschen, begrenzt auf ±2 σ.</summary>
    public double Next(double sigma) => _random.BoundedGaussian(sigma, 2 * sigma);
}
