namespace HikiHackVision.Features.Cut;

/// <summary>
/// Relaciona a posição no arquivo com o relógio gravado na imagem. Nas gravações por evento do NVR
/// o relógio não anda 1:1 com o arquivo (saltos entre eventos, FPS variável), então a única
/// premissa é que ele nunca volta: os limites de corte são achados por busca binária no OSD.
/// </summary>
public sealed class VideoClock(OsdTimestampReader reader, ClockReference start, double duration)
{
    private double _span = double.MaxValue;

    /// <summary>Segundos de relógio decorridos entre o início e o fim do arquivo; null se o fim for ilegível.</summary>
    public async Task<double?> ReadClockSpanAsync()
    {
        foreach (var back in new[] { 1.0, 5.0, 15.0, 40.0 })
        {
            var t = duration - back;
            if (t <= 0) break;
            var sample = await reader.ReadAtAsync(t);
            if (sample == null) continue;
            var e = start.Elapsed(sample.Reading);
            if (e < 0) continue; // leitura incoerente (relógio não volta)
            _span = e;
            return e;
        }
        return null;
    }

    /// <summary>Resolução da busca: menor que o intervalo entre quadros, para achar o quadro exato do limite.</summary>
    private const double Resolution = 0.02;

    /// <summary>
    /// Posição t tal que todo quadro com pts &lt; t está antes de <paramref name="targetElapsed"/> e todo quadro
    /// com pts ≥ t já o atingiu. Serve direto como fim (-to) e, ajustado ao keyframe seguinte, como início.
    /// Em gravações por evento o relógio pode andar dezenas de vezes mais rápido que o arquivo,
    /// por isso a busca vai até a resolução de um quadro.
    /// </summary>
    public async Task<double> OffsetForElapsedAsync(double targetElapsed, double span)
    {
        if (targetElapsed <= 0) return 0;
        if (targetElapsed >= span) return duration;

        double lo = 0, hi = duration;
        while (hi - lo > Resolution)
        {
            var probe = await ProbeBetweenAsync(lo, hi);
            if (probe == null) break; // OSD ilegível nessa região: fica com o limite seguro (hi)

            if (probe.Value.Elapsed >= targetElapsed) hi = probe.Value.Offset;
            else lo = probe.Value.Offset;
        }
        return hi;
    }

    /// <summary>Lê o quadro no meio de (lo, hi); se ilegível, tenta outros pontos do intervalo.</summary>
    private async Task<(double Offset, double Elapsed)?> ProbeBetweenAsync(double lo, double hi)
    {
        foreach (var frac in new[] { 0.5, 0.3, 0.7, 0.15, 0.85 })
        {
            var t = lo + (hi - lo) * frac;
            var sample = await reader.ReadAtAsync(t, exact: true);
            if (sample == null) continue;
            var e = start.Elapsed(sample.Reading);
            if (e >= -2 && e <= _span + 2) return (t, e); // fora disso: leitura incoerente
        }
        return null;
    }
}
