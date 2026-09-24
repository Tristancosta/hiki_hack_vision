namespace HikiHackVision.Features.Cut;

/// <summary>Trecho em segundos. Dependendo do contexto, relativo ao relógio (OSD) ou à posição no arquivo.</summary>
public record Segment(double Start, double End)
{
    public double Length => End - Start;
}

public static class CutWindowCalculator
{
    public static readonly TimeSpan DayStart = TimeSpan.FromHours(6);
    public static readonly TimeSpan DayEnd = TimeSpan.FromHours(18);

    /// <summary>Menor trecho considerado útil; evita gerar arquivos de 1 segundo por imprecisão do OCR.</summary>
    private const double MinSegmentSeconds = 2;

    /// <summary>
    /// Cruza o intervalo de relógio [início, início + span] com [06:00, 18:00] de cada dia coberto.
    /// Os trechos retornados são em segundos de relógio decorridos desde <paramref name="startClock"/>.
    /// </summary>
    public static List<Segment> Calculate(TimeOnly startClock, double spanSeconds)
    {
        var result = new List<Segment>();
        var start = startClock.ToTimeSpan().TotalSeconds; // segundos desde 00:00 do dia 0
        var end = start + spanSeconds;

        for (var day = 0; day * 86400.0 < end; day++)
        {
            var windowStart = day * 86400.0 + DayStart.TotalSeconds;
            var windowEnd = day * 86400.0 + DayEnd.TotalSeconds;
            var s = Math.Max(start, windowStart);
            var e = Math.Min(end, windowEnd);
            if (e - s >= MinSegmentSeconds)
                result.Add(new Segment(s - start, e - start));
        }
        return result;
    }

    public static TimeOnly ClockAt(TimeOnly startClock, double elapsedSeconds) =>
        startClock.Add(TimeSpan.FromSeconds(elapsedSeconds));

    /// <summary>
    /// Converte um horário lido em segundos decorridos desde o início, escolhendo a volta de 24h
    /// que não fique antes de <paramref name="minElapsed"/> (o relógio nunca anda mais devagar que o arquivo).
    /// </summary>
    public static double Elapsed(TimeOnly startClock, TimeOnly clock, double minElapsed)
    {
        var e = (clock - startClock).TotalSeconds; // TimeOnly: diferença circular em [0, 24h)
        while (e < minElapsed - 2) e += 86400;
        return e;
    }
}
