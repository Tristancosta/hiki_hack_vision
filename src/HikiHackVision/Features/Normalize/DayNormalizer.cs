using HikiHackVision.Features.Cut;
using HikiHackVision.Infra;
using HikiHackVision.Menu;

namespace HikiHackVision.Features.Normalize;

/// <summary>
/// Normatiza os vídeos em períodos de 15 minutos de relógio, dentro da janela das 06:00 às 18:00.
/// Cada período vira um arquivo <c>ddMMhhmm_x.mp4</c>: dd/MM a data, hh:mm o início do período
/// (0600, 0615, ..., 1745) e x o número do dia na contagem (1 para o primeiro dia, 2 para o segundo...).
/// Trechos de arquivos diferentes que caem no mesmo período são emendados na ordem do relógio.
/// O horário vem do relógio gravado na imagem (OSD); nome e metadados do arquivo não são usados.
/// </summary>
public class DayNormalizer(FfmpegTools ffmpeg, string root)
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    /// <summary>Menor trecho considerado útil; evita gerar arquivos de 1 segundo por imprecisão da leitura.</summary>
    private const double MinSegmentSeconds = 2;

    /// <summary>Pedaço já cortado de um arquivo de origem, com o horário de relógio em que começa.</summary>
    private sealed record Piece(DateTime SlotStart, DateTime Start, string Path, string Source);

    public async Task RunAsync()
    {
        var folder = ConsoleUi.AskFolder("Cole o caminho da pasta com os vídeos (normalmente a pasta 'cortados')");
        if (folder == null) return;

        var files = ProjectPaths.ListVideos(folder);
        ConsoleUi.Info($"Encontrados {files.Count} vídeo(s).");
        if (files.Count == 0) return;

        var outDir = ProjectPaths.EnsureDir(root, "normatizados");
        var tempDir = Path.Combine(outDir, "_tmp");
        Directory.CreateDirectory(tempDir);

        var pieces = new List<Piece>();
        var errors = new List<(string Name, string Error)>();
        var discarded = new List<string>();

        try
        {
            // 1) Corta cada arquivo nos limites de 15 minutos do relógio
            ConsoleUi.Info("Lendo o relógio na imagem e cortando em períodos de 15 minutos...");
            for (var i = 0; i < files.Count; i++)
            {
                var input = files[i];
                var name = Path.GetFileName(input);
                ConsoleUi.Progress(i + 1, files.Count, name);
                await SplitFileAsync(input, name, tempDir, pieces, errors, discarded);
            }

            if (pieces.Count == 0)
            {
                Console.WriteLine();
                ConsoleUi.Error("Nenhum trecho dentro do horário; nada foi gerado.");
                foreach (var d in discarded) ConsoleUi.Warn($"  descartado: {d}");
                foreach (var (n, e) in errors) ConsoleUi.Error($"  {n}: {e}");
                return;
            }

            // 2) Numera os dias e monta um arquivo por período
            var dayNumber = pieces
                .Select(p => DateOnly.FromDateTime(p.SlotStart))
                .Distinct()
                .OrderBy(d => d)
                .Select((d, idx) => (Day: d, Number: idx + 1))
                .ToDictionary(x => x.Day, x => x.Number);

            var slots = pieces
                .GroupBy(p => p.SlotStart)
                .OrderBy(g => g.Key)
                .ToList();

            Console.WriteLine();
            ConsoleUi.Info($"{dayNumber.Count} dia(s), {slots.Count} período(s). Gerando em {outDir} ...");
            var generated = 0;
            foreach (var slot in slots)
            {
                var ordered = slot.OrderBy(p => p.Start).ThenBy(p => p.Source, StringComparer.OrdinalIgnoreCase).ToList();
                var outName = $"{slot.Key:ddMMHHmm}_{dayNumber[DateOnly.FromDateTime(slot.Key)]}.mp4";
                var output = Path.Combine(outDir, outName);
                var sources = string.Join(" + ", ordered.Select(p => p.Source).Distinct());
                Console.Write($"  {outName} ({slot.Key:dd/MM HH:mm}, {sources}) ... ");

                try
                {
                    if (File.Exists(output)) File.Delete(output);
                    if (ordered.Count == 1)
                    {
                        File.Move(ordered[0].Path, output);
                    }
                    else
                    {
                        var r = await ConcatAsync(ordered.Select(p => p.Path), output, tempDir);
                        if (!r.Success || !File.Exists(output) || new FileInfo(output).Length < 1024)
                        {
                            ConsoleUi.Error("FALHOU");
                            errors.Add((outName, r.LastErrorLine));
                            if (File.Exists(output)) File.Delete(output);
                            continue;
                        }
                    }
                    ConsoleUi.Ok($"OK ({new FileInfo(output).Length / (1024 * 1024)} MB)");
                    generated++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ConsoleUi.Error("FALHOU");
                    errors.Add((outName, ex.Message));
                }
            }

            Console.WriteLine();
            ConsoleUi.Info($"Resumo: {generated} período(s) gerado(s) em {dayNumber.Count} dia(s), {discarded.Count} descartado(s), {errors.Count} erro(s).");
            foreach (var (d, n) in dayNumber.OrderBy(x => x.Key))
                ConsoleUi.Info($"  dia {n}: {d:dd/MM/yyyy}");
            foreach (var d in discarded) ConsoleUi.Warn($"  descartado: {d}");
            foreach (var (n, e) in errors) ConsoleUi.Error($"  {n}: {e}");
            ConsoleUi.Info($"Saída: {outDir}");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Lê o relógio no início e no fim do arquivo, calcula os períodos de 15 minutos cobertos
    /// (dentro das 06:00–18:00) e corta cada um, por busca binária no OSD, para a pasta temporária.
    /// </summary>
    private async Task SplitFileAsync(string input, string name, string tempDir,
        List<Piece> pieces, List<(string, string)> errors, List<string> discarded)
    {
        var duration = await ffmpeg.GetDurationAsync(input);
        var size = await ffmpeg.GetFrameSizeAsync(input);
        if (duration is null or <= 0 || size == null)
        {
            ConsoleUi.Error("vídeo ilegível");
            errors.Add((name, "não foi possível ler duração/resolução"));
            return;
        }

        var reader = new OsdTimestampReader(ffmpeg, input, size.Value.Width, size.Value.Height, duration.Value);
        var start = await reader.ReadStartAsync();
        if (start == null)
        {
            ConsoleUi.Error("horário não lido");
            errors.Add((name, "horário não lido na imagem (OSD)"));
            return;
        }

        var video = new VideoClock(reader, start, duration.Value);
        var span = await video.ReadClockSpanAsync();
        if (span == null)
        {
            ConsoleUi.Error("horário final não lido");
            errors.Add((name, "horário do fim do vídeo não lido na imagem (OSD)"));
            return;
        }

        var startDt = start.StartDateTime ?? await FindStartDateAsync(reader, start, duration.Value);
        if (startDt is not { } startDateTime)
        {
            ConsoleUi.Error("data não lida");
            errors.Add((name, "data não lida na imagem (OSD); sem ela não dá para saber o dia"));
            return;
        }
        Console.Write($"{startDateTime:dd/MM HH:mm:ss} → {startDateTime.AddSeconds(span.Value):dd/MM HH:mm:ss} ... ");

        var slots = CalculateSlots(startDateTime, span.Value);
        if (slots.Count == 0)
        {
            ConsoleUi.Warn("fora do horário, descartado");
            discarded.Add(name);
            return;
        }

        // Cada limite de relógio vira uma posição no arquivo; limites compartilhados entre períodos
        // vizinhos são resolvidos uma vez só
        var keyframes = await ffmpeg.GetKeyframesAsync(input);
        var offsets = new Dictionary<double, double>();
        async Task<double> OffsetAsync(double elapsed)
        {
            if (!offsets.TryGetValue(elapsed, out var o))
                offsets[elapsed] = o = await video.OffsetForElapsedAsync(elapsed, span.Value);
            return o;
        }

        var made = 0;
        var baseName = Path.GetFileNameWithoutExtension(name);
        foreach (var (slotStart, seg) in slots)
        {
            var s = ClipCutter.SnapToKeyframe(await OffsetAsync(seg.Start), keyframes);
            var e = await OffsetAsync(seg.End);
            if (e - s < 1) continue;

            var piecePath = Path.Combine(tempDir, $"{slotStart:ddMMHHmm}__{baseName}.mp4");
            var r = await ClipCutter.CutAsync(ffmpeg, input, new Segment(s, e), piecePath);
            if (!r.Success || !File.Exists(piecePath))
            {
                errors.Add((name, $"período {slotStart:dd/MM HH:mm}: {r.LastErrorLine}"));
                if (File.Exists(piecePath)) File.Delete(piecePath);
                continue;
            }

            pieces.Add(new Piece(slotStart, startDateTime.AddSeconds(seg.Start), piecePath, name));
            made++;
        }

        if (made == 0)
        {
            ConsoleUi.Warn("fora do horário, descartado");
            discarded.Add(name);
        }
        else ConsoleUi.Ok($"{made} período(s)");
    }

    /// <summary>
    /// Períodos de 15 minutos cobertos por [início, início + span], restritos às 06:00–18:00 de cada dia.
    /// Retorna, para cada período, seu horário inicial e o trecho em segundos decorridos desde o início.
    /// </summary>
    public static List<(DateTime SlotStart, Segment Elapsed)> CalculateSlots(DateTime start, double spanSeconds)
    {
        var result = new List<(DateTime, Segment)>();
        var end = start.AddSeconds(spanSeconds);

        // Primeiro período: o de 15 minutos que contém o início
        var slot = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute / 15 * 15, 0);
        for (; slot < end; slot = slot.Add(Period))
        {
            var tod = slot.TimeOfDay;
            if (tod < CutWindowCalculator.DayStart || tod >= CutWindowCalculator.DayEnd) continue;

            var s = slot > start ? slot : start;
            var e = slot.Add(Period) < end ? slot.Add(Period) : end;
            if ((e - s).TotalSeconds >= MinSegmentSeconds)
                result.Add((slot, new Segment((s - start).TotalSeconds, (e - start).TotalSeconds)));
        }
        return result;
    }

    /// <summary>
    /// Quando a data não é legível no início (fundo claro, por exemplo), procura uma leitura com data
    /// mais adiante no arquivo e deriva a data inicial descontando o relógio decorrido (que nunca volta).
    /// </summary>
    private static async Task<DateTime?> FindStartDateAsync(OsdTimestampReader reader, ClockReference start, double duration)
    {
        foreach (var t in new[] { 10.0, 30.0, 60.0, 120.0, 300.0, 600.0, duration * 0.5, duration * 0.75, duration - 5 })
        {
            if (t <= 0 || t >= duration) continue;
            var sample = await reader.ReadAtAsync(t);
            if (sample?.Reading.Date is not { } date) continue;

            var elapsed = start.Elapsed(sample.Reading);
            if (elapsed < 0) continue; // leitura incoerente
            return date.ToDateTime(sample.Reading.Time).AddSeconds(-elapsed);
        }
        return null;
    }

    /// <summary>
    /// Emenda os trechos (mesma câmera, mesmo codec) sem recodificar, com o demuxer concat do ffmpeg.
    /// A lista de entradas vai num arquivo temporário porque o ffmpeg só a aceita assim.
    /// </summary>
    private async Task<ProcessResult> ConcatAsync(IEnumerable<string> inputs, string output, string tempDir)
    {
        var list = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(output) + ".txt");
        var lines = inputs.Select(p => "file '" + p.Replace('\\', '/').Replace("'", "'\\''") + "'");
        await File.WriteAllLinesAsync(list, lines);
        return await ffmpeg.FfmpegAsync("-y", "-f", "concat", "-safe", "0", "-i", list,
            "-map", "0", "-c", "copy", "-movflags", "+faststart", output);
    }
}
