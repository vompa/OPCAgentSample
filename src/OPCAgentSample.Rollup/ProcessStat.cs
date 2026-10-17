using System.Buffers.Binary;

namespace OPCAgentSample.Rollup;

/// <summary>
/// Histogramm an den Toleranzgrenzen: <see cref="Edges"/> trennen die Klassen (untere Grenze eingeschlossen). Die Klassen
/// <see cref="BandFirstBin"/> bis <see cref="BandLastBin"/> liegen im Toleranzband; dessen obere Grenze gehört noch dazu
/// (wie <c>value &lt; low OR value &gt; high</c> in der Auswertung).
/// </summary>
public sealed record HistogramSpec(double[] Edges, int BandFirstBin, int BandLastBin)
{
    public int Bins => Edges.Length + 1;

    public int BinOf(double value)
    {
        var bin = 0;
        for (var i = 0; i < Edges.Length; i++)
        {
            if (value > Edges[i] || (value == Edges[i] && i != BandLastBin)) bin = i + 1;
        }

        return bin;
    }

    /// <summary>Werte außerhalb des Toleranzbands.</summary>
    public long Outside(IReadOnlyList<long> hist)
    {
        long outside = 0;
        for (var i = 0; i < hist.Count; i++)
        {
            if (i < BandFirstBin || i > BandLastBin) outside += hist[i];
        }

        return outside;
    }
}

/// <summary>Statistik eines Zeitfachs: Anzahl, Summe, Quadratsumme, Minimum, Maximum und optional ein Histogramm (additiv zusammenführbar).</summary>
public sealed class ProcessStat
{
    private const int FixedBytes = 4 + (4 * 8);

    public ProcessStat(int bins) => Hist = new long[bins];

    public long Count { get; private set; }

    public double Sum { get; private set; }

    public double SumSquares { get; private set; }

    public double Min { get; private set; } = double.PositiveInfinity;

    public double Max { get; private set; } = double.NegativeInfinity;

    public long[] Hist { get; }

    public double Mean => Count > 0 ? Sum / Count : double.NaN;

    /// <summary>Standardabweichung der Grundgesamtheit aus Summe und Quadratsumme.</summary>
    public double StdDev => Count > 0 ? Math.Sqrt(Math.Max(0, (SumSquares / Count) - (Mean * Mean))) : double.NaN;

    /// <param name="value">Messwert.</param>
    /// <param name="bin">Klasse des Histogramms (<c>-1</c>, wenn die Reihe keines führt).</param>
    public void Add(double value, int bin)
    {
        Count++;
        Sum += value;
        SumSquares += value * value;
        if (value < Min) Min = value;
        if (value > Max) Max = value;
        if (bin >= 0) Hist[bin]++;
    }

    public void Merge(ProcessStat other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Count == 0) return;

        Count += other.Count;
        Sum += other.Sum;
        SumSquares += other.SumSquares;
        if (other.Min < Min) Min = other.Min;
        if (other.Max > Max) Max = other.Max;
        for (var i = 0; i < Hist.Length; i++) Hist[i] += other.Hist[i];
    }

    public ProcessStat Clone()
    {
        var copy = new ProcessStat(Hist.Length);
        copy.Merge(this);
        return copy;
    }

    /// <summary>Binär: Anzahl (int32), Summe, Quadratsumme, Min, Max (je double), dann je Klasse uint16 (feine Fächer) oder int32.</summary>
    public byte[] Encode(bool wide)
    {
        var width = wide ? 4 : 2;
        var bytes = new byte[FixedBytes + (Hist.Length * width)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), checked((int)Count));
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(4, 8), Sum);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(12, 8), SumSquares);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(20, 8), Min);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(28, 8), Max);
        for (var i = 0; i < Hist.Length; i++)
        {
            var span = bytes.AsSpan(FixedBytes + (i * width), width);
            if (wide) BinaryPrimitives.WriteInt32LittleEndian(span, checked((int)Hist[i]));
            else BinaryPrimitives.WriteUInt16LittleEndian(span, checked((ushort)Hist[i]));
        }

        return bytes;
    }

    public static ProcessStat Decode(ReadOnlySpan<byte> bytes, int bins, bool wide)
    {
        var stat = new ProcessStat(bins)
        {
            Count = BinaryPrimitives.ReadInt32LittleEndian(bytes[..4]),
            Sum = BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(4, 8)),
            SumSquares = BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(12, 8)),
            Min = BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(20, 8)),
            Max = BinaryPrimitives.ReadDoubleLittleEndian(bytes.Slice(28, 8)),
        };
        var width = wide ? 4 : 2;
        for (var i = 0; i < bins; i++)
        {
            var span = bytes.Slice(FixedBytes + (i * width), width);
            stat.Hist[i] = wide ? BinaryPrimitives.ReadInt32LittleEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span);
        }

        return stat;
    }
}
