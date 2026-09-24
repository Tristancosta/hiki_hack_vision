using HikiHackVision.Infra;

namespace HikiHackVision.Features.Cut;

/// <summary>Leitura do OSD associada à posição (s) no arquivo de onde o quadro foi tirado.</summary>
public record OsdSample(double Offset, OsdReading Reading);

/// <summary>Lê o horário gravado na imagem (OSD, canto superior esquerdo) de um vídeo.</summary>
public class OsdTimestampReader(FfmpegTools ffmpeg, string video, int frameWidth, int frameHeight, double duration)
{
    // Região analisada: 45% x 15% do quadro a partir do canto superior esquerdo (valores pares)
    private readonly int _cropW = (int)(frameWidth * 0.45) & ~1;
    private readonly int _cropH = (int)(frameHeight * 0.15) & ~1;
    private readonly bool _debug = Environment.GetEnvironmentVariable("HIKI_OCR_DEBUG") == "1";

    /// <summary>Posição do OSD, fixa no vídeo: medida na 1ª leitura bem-sucedida e reaproveitada.</summary>
    private OsdLayout? _layout;

    /// <summary>Com o layout conhecido lê direto (resiste a fundo claro); senão localiza o texto e guarda o layout.</summary>
    private OsdReading? Recognize(byte[] gray)
    {
        if (_layout != null && OsdGlyphReader.ReadWithLayout(gray, _cropW, _cropH, _layout) is { } fast)
            return fast;

        var calibrated = OsdGlyphReader.Calibrate(gray, _cropW, _cropH, frameHeight);
        if (calibrated == null) return null;
        _layout ??= calibrated.Value.Layout;
        return calibrated.Value.Reading;
    }

    /// <summary>
    /// Lê o OSD do primeiro quadro em/após <paramref name="seconds"/>; se falhar e <paramref name="exact"/> for false,
    /// tenta quadros logo em seguida.
    /// </summary>
    public async Task<OsdSample?> ReadAtAsync(double seconds, bool exact = false)
    {
        foreach (var delta in exact ? [0.0] : new[] { 0.0, 0.4, 0.8, 1.5 })
        {
            var t = Math.Min(seconds + delta, Math.Max(0, duration - 0.2));
            var gray = await ffmpeg.ExtractGrayCornerAsync(video, t, _cropW, _cropH);
            if (gray == null || gray.Length < _cropW * _cropH) continue;

            var reading = Recognize(gray);
            if (_debug)
                Console.Error.WriteLine($"\n  [OSD t={t:0.##}s] {(reading == null ? "?" : $"{reading.Date:yyyy-MM-dd} {reading.Time:HH:mm:ss} (dist {reading.Distance})")}");
            if (reading != null) return new OsdSample(t, reading);
        }
        return null;
    }

    /// <summary>
    /// Relógio no início do arquivo. Exige duas leituras próximas coerentes entre si
    /// (evita que um dígito mal lido defina o horário do vídeo inteiro).
    /// </summary>
    public async Task<ClockReference?> ReadStartAsync()
    {
        foreach (var t in new[] { 0.0, 3.0, 8.0, 20.0, 45.0, 90.0 })
        {
            if (t + 2 >= duration) break;
            var a = await ReadAtAsync(t);
            if (a == null) continue;
            var b = await ReadAtAsync(a.Offset + 1);
            if (b == null) continue;

            // Gravação por evento: o relógio pode andar mais rápido que o arquivo, mas nunca volta
            // e não salta muito em 1 segundo de arquivo
            var start = new ClockReference(a.Reading);
            var delta = start.Elapsed(b.Reading);
            if (delta >= 0 && delta <= 120) return start;
        }
        return null;
    }
}

/// <summary>Horário (e data, se lida) no início do arquivo; referência para medir o tempo de relógio decorrido.</summary>
public class ClockReference(OsdReading first)
{
    public TimeOnly StartClock { get; } = first.Time;
    public DateTime? StartDateTime { get; } = first.Date?.ToDateTime(first.Time);

    /// <summary>
    /// Segundos de relógio decorridos desde o início até a leitura. Usa a data do OSD quando disponível
    /// (funciona para gravações de vários dias); senão assume o menor avanço dentro de 24h.
    /// </summary>
    public double Elapsed(OsdReading reading)
    {
        if (StartDateTime is { } start && reading.Date is { } date)
            return (date.ToDateTime(reading.Time) - start).TotalSeconds;
        return CutWindowCalculator.Elapsed(StartClock, reading.Time, 0);
    }
}
