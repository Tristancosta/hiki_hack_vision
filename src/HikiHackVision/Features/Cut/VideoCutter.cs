using HikiHackVision.Infra;
using HikiHackVision.Menu;

namespace HikiHackVision.Features.Cut;

public class VideoCutter(FfmpegTools ffmpeg, string root)
{

    public async Task RunAsync()
    {
        var folder = ConsoleUi.AskFolder("Cole o caminho da pasta com os vídeos");
        if (folder == null) return;

        var files = ProjectPaths.ListVideos(folder);
        ConsoleUi.Info($"Encontrados {files.Count} vídeo(s).");
        if (files.Count == 0) return;

        var outDir = ProjectPaths.EnsureDir(root, "cortados");
        int cut = 0, copied = 0;
        var discarded = new List<string>();
        var errors = new List<(string Name, string Error)>();

        for (var i = 0; i < files.Count; i++)
        {
            var input = files[i];
            var name = Path.GetFileName(input);
            ConsoleUi.Progress(i + 1, files.Count, name);

            var duration = await ffmpeg.GetDurationAsync(input);
            var size = await ffmpeg.GetFrameSizeAsync(input);
            if (duration is null or <= 0 || size == null)
            {
                ConsoleUi.Error("vídeo ilegível");
                errors.Add((name, "não foi possível ler duração/resolução"));
                continue;
            }

            var reader = new OsdTimestampReader(ffmpeg, input, size.Value.Width, size.Value.Height, duration.Value);
            var start = await reader.ReadStartAsync();
            if (start == null)
            {
                ConsoleUi.Error("horário não lido");
                errors.Add((name, "horário não lido na imagem (OSD)"));
                continue;
            }

            var video = new VideoClock(reader, start, duration.Value);
            var span = await video.ReadClockSpanAsync();
            if (span == null)
            {
                ConsoleUi.Error("horário final não lido");
                errors.Add((name, "horário do fim do vídeo não lido na imagem (OSD)"));
                continue;
            }
            Console.Write($"{Describe(start, 0)} → {Describe(start, span.Value)} ... ");

            var clockSegments = CutWindowCalculator.Calculate(start.StartClock, span.Value);
            if (clockSegments.Count == 0)
            {
                ConsoleUi.Warn("fora do horário, descartado");
                discarded.Add(name);
                continue;
            }

            // Converte limites de relógio em posições no arquivo
            var keyframes = await ffmpeg.GetKeyframesAsync(input);
            var fileSegments = new List<Segment>();
            foreach (var cs in clockSegments)
            {
                var s = await video.OffsetForElapsedAsync(cs.Start, span.Value);
                var e = await video.OffsetForElapsedAsync(cs.End, span.Value);
                s = ClipCutter.SnapToKeyframe(s, keyframes);
                if (e - s >= 1) fileSegments.Add(new Segment(s, e));
            }

            if (fileSegments.Count == 0)
            {
                ConsoleUi.Warn("fora do horário, descartado");
                discarded.Add(name);
                continue;
            }

            if (fileSegments.Count == 1 && fileSegments[0].Start <= 0.05 && fileSegments[0].End >= duration.Value - 0.5)
            {
                File.Copy(input, Path.Combine(outDir, name), overwrite: true);
                ConsoleUi.Ok("dentro do horário, copiado");
                copied++;
                continue;
            }

            var failed = false;
            for (var s = 0; s < fileSegments.Count; s++)
            {
                var seg = fileSegments[s];
                var outName = fileSegments.Count == 1
                    ? name
                    : $"{Path.GetFileNameWithoutExtension(name)}_parte{s + 1}{Path.GetExtension(name)}";
                var output = Path.Combine(outDir, outName);

                var r = await ClipCutter.CutAsync(ffmpeg, input, seg, output);

                if (!r.Success)
                {
                    failed = true;
                    errors.Add((outName, r.LastErrorLine));
                    if (File.Exists(output)) File.Delete(output);
                }
            }

            if (failed) ConsoleUi.Error("FALHOU");
            else
            {
                var desc = string.Join(", ", clockSegments.Select(cs => $"{Describe(start, cs.Start)}-{Describe(start, cs.End, timeOnly: true)}"));
                ConsoleUi.Ok($"{fileSegments.Count} parte(s): {desc}");
                cut++;
            }
        }

        Console.WriteLine();
        ConsoleUi.Info($"Resumo: {cut} cortado(s), {copied} copiado(s) inteiro(s), {discarded.Count} descartado(s), {errors.Count} erro(s).");
        foreach (var d in discarded) ConsoleUi.Warn($"  descartado: {d}");
        foreach (var (n, e) in errors) ConsoleUi.Error($"  {n}: {e}");
        ConsoleUi.Info($"Saída: {outDir}");
    }

    private static string Describe(ClockReference start, double elapsed, bool timeOnly = false) =>
        start.StartDateTime is { } dt && !timeOnly
            ? dt.AddSeconds(elapsed).ToString("dd/MM HH:mm:ss")
            : CutWindowCalculator.ClockAt(start.StartClock, elapsed).ToString("HH:mm:ss");
}
