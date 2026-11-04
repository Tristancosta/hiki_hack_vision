namespace HikiHackVision.Features.Cut;

/// <summary>
/// Relaciona a posição no arquivo com o relógio gravado na imagem. Nas gravações por evento do NVR
/// o relógio não anda 1:1 com o arquivo (saltos entre eventos, FPS variável), então a única
/// premissa é que ele nunca volta: os limites de corte são achados por busca binária no OSD.
/// </summary>
public sealed class VideoClock(OsdTimestampReader reader, ClockReference start, double duration)
{
    private double _span = double.MaxValue;

    /// <summary>Leituras ao longo do arquivo (posição → relógio decorrido), em ordem; resolvem a volta de 24h.</summary>
    private readonly List<(double Offset, double Elapsed)> _anchors = [(0, 0)];

    /// <summary>
    /// Data e hora do início. Vem da 1ª leitura se ela tiver data; senão é derivada da primeira leitura
    /// datada da varredura, descontando o relógio decorrido até ela.
    /// </summary>
    public DateTime? StartDateTime { get; private set; } = start.StartDateTime;

    /// <summary>
    /// Segundos de relógio decorridos entre o início e o fim do arquivo; null se o fim for ilegível.
    /// <para>
    /// Uma leitura sem data só diz a hora do dia, e o arquivo pode atravessar a noite (ou vários dias).
    /// Por isso o relógio é amostrado ao longo do arquivo inteiro: entre duas amostras próximas o avanço
    /// é pequeno, e como o relógio nunca volta, uma hora "menor" que a anterior é a virada da meia-noite.
    /// Antes só se lia o fim, e com a data ilegível lá, um arquivo de 24/09 10:18 a 25/09 12:46 virava
    /// 24/09 10:18–12:46 (a noite passava inteira no corte).
    /// </para>
    /// </summary>
    public async Task<double?> ReadClockSpanAsync()
    {
        // Amostras a cada ~4 min de arquivo: mesmo com o relógio 50x mais rápido, < 24h entre vizinhas
        // O fim é lido antes, e a varredura vai só até ele (em arquivos de poucos segundos as amostras
        // cairiam depois da leitura do fim)
        OsdSample? end = null;
        foreach (var back in new[] { 1.0, 5.0, 15.0, 40.0 })
        {
            var t = duration - back;
            if (t <= 0) t = duration / 2;
            if ((end = await reader.ReadAtAsync(t)) != null) break;
            if (duration - back <= 0) break;
        }
        if (end == null) return null;

        var n = Math.Clamp((int)Math.Ceiling(duration / 240), 8, 60);
        for (var k = 1; k < n && duration * k / n < end.Offset; k++)
        {
            var sample = await reader.ReadAtAsync(duration * k / n);
            if (sample != null && sample.Offset < end.Offset) AddAnchor(sample);
        }

        if (!AddAnchor(end)) return null;
        _span = _anchors[^1].Elapsed;
        return _span;
    }

    /// <summary>Acrescenta uma leitura da varredura; false se for incoerente com as anteriores.</summary>
    private bool AddAnchor(OsdSample? sample)
    {
        if (sample == null) return false;
        var prev = _anchors[^1];
        var reading = sample.Reading;

        double e;
        if (StartDateTime is { } startDt && reading.Date is { } date)
        {
            // Leitura datada é absoluta: prevalece sobre amostras sem data que tenham avançado demais
            // (uma hora mal lida sem data pode ter somado 24h indevidamente)
            e = (date.ToDateTime(reading.Time) - startDt).TotalSeconds;
            if (e < -2) return false;
            while (_anchors.Count > 1 && _anchors[^1].Elapsed > e + 2) _anchors.RemoveAt(_anchors.Count - 1);
        }
        else
        {
            e = prev.Elapsed + CutWindowCalculator.Elapsed(ClockAt(prev.Elapsed), reading.Time, 0);
            if (reading.Date is { } d && StartDateTime == null)
                StartDateTime = d.ToDateTime(reading.Time).AddSeconds(-e);
        }

        _anchors.Add((sample.Offset, e));
        return true;
    }

    private TimeOnly ClockAt(double elapsed) => CutWindowCalculator.ClockAt(start.StartClock, elapsed);

    /// <summary>
    /// Relógio decorrido de uma leitura feita na posição <paramref name="offset"/>. Sem data, escolhe a volta
    /// de 24h a partir da amostra da varredura anterior à posição (com folga, pois a varredura não usa seek exato).
    /// </summary>
    private double ElapsedAt(double offset, OsdReading reading)
    {
        if (StartDateTime is { } startDt && reading.Date is { } date)
            return (date.ToDateTime(reading.Time) - startDt).TotalSeconds;

        const double Slack = 3600;
        var before = _anchors.LastOrDefault(a => a.Offset <= offset);
        var min = Math.Max(0, before.Elapsed - Slack);
        return min + CutWindowCalculator.Elapsed(ClockAt(min), reading.Time, 0);
    }

    /// <summary>Faixa plausível do relógio na posição, pelas amostras vizinhas da varredura (± folga).</summary>
    private bool Plausible(double offset, double elapsed)
    {
        const double Slack = 3600;
        var before = _anchors.LastOrDefault(a => a.Offset <= offset);
        var after = _anchors.FirstOrDefault(a => a.Offset >= offset, (Offset: duration, Elapsed: _span));
        return elapsed >= before.Elapsed - Slack && elapsed <= Math.Min(after.Elapsed + Slack, _span + 2) && elapsed >= -2;
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
            var e = ElapsedAt(t, sample.Reading);
            if (Plausible(t, e)) return (t, e); // fora disso: leitura incoerente
        }
        return null;
    }
}
