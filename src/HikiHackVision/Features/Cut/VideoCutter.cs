using System.Globalization;
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
                // Com -c copy o corte começa num keyframe; usa o primeiro keyframe >= início
                // para não incluir nada de antes das 06:00 (nem de um evento anterior ao salto)
                if (s > 0.05) s = keyframes.FirstOrDefault(k => k >= s - 0.001, double.MaxValue);
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

                // Início levemente após o keyframe: o seek de entrada volta para o keyframe exato
                var ss = seg.Start > 0 ? seg.Start + 0.01 : 0;
                var r = await ffmpeg.FfmpegAsync("-y",
                    "-ss", Fmt(ss), "-to", Fmt(seg.End), "-i", input,
                    "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", output);

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

    private static string Fmt(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Describe(ClockReference start, double elapsed, bool timeOnly = false) =>
        start.StartDateTime is { } dt && !timeOnly
            ? dt.AddSeconds(elapsed).ToString("dd/MM HH:mm:ss")
            : CutWindowCalculator.ClockAt(start.StartClock, elapsed).ToString("HH:mm:ss");

    /// <summary>
    /// Relaciona a posição no arquivo com o relógio gravado na imagem. Nas gravações por evento do NVR
    /// o relógio não anda 1:1 com o arquivo (saltos entre eventos, FPS variável), então a única
    /// premissa é que ele nunca volta: os limites de corte são achados por busca binária no OSD.
    /// </summary>
    private sealed class VideoClock(OsdTimestampReader reader, ClockReference start, double duration)
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
}
