using System.Globalization;
using HikiHackVision.Infra;
using HikiHackVision.Menu;

namespace HikiHackVision.Features.Convert;

public class VideoConverter(FfmpegTools ffmpeg, string root)
{
    /// <summary>FPS usado quando o ffprobe não informa um valor plausível (padrão das gravações Hikvision).</summary>
    private const double DefaultFrameRate = 10;

    public async Task RunAsync()
    {
        var folder = ConsoleUi.AskFolder("Cole o caminho da pasta com os vídeos");
        if (folder == null) return;

        var files = ProjectPaths.ListVideos(folder);
        ConsoleUi.Info($"Encontrados {files.Count} vídeo(s).");
        if (files.Count == 0) return;

        var outDir = ProjectPaths.EnsureDir(root, "convertidos");
        var tempDir = Path.Combine(Path.GetTempPath(), "hiki_convert");
        Directory.CreateDirectory(tempDir);
        var failures = new List<(string Name, string Error)>();
        var skipped = 0;

        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var input = files[i];
                var name = Path.GetFileName(input);
                var output = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ".mp4");
                ConsoleUi.Progress(i + 1, files.Count, name);

                if (File.Exists(output) && new FileInfo(output).Length > 1024)
                {
                    ConsoleUi.Warn("já convertido, pulando");
                    skipped++;
                    continue;
                }

                var probe = await ffmpeg.ProbeAsync(input);
                var r = IsProgramStream(probe) && ElementaryFormat(probe!.VideoCodec) is { } esFormat
                    ? await RemuxElementaryStreamAsync(input, output, esFormat, FrameRateOf(probe), tempDir)
                    : await ConvertMp4Async(input, output);

                if (IsValidOutput(r, output))
                {
                    ConsoleUi.Ok($"OK ({new FileInfo(output).Length / (1024 * 1024)} MB)");
                }
                else
                {
                    ConsoleUi.Error("FALHOU");
                    failures.Add((name, r.LastErrorLine));
                    if (File.Exists(output)) File.Delete(output);
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }

        Console.WriteLine();
        ConsoleUi.Info($"Resumo: {files.Count - failures.Count - skipped} convertido(s), {skipped} já existente(s), {failures.Count} falha(s).");
        foreach (var (n, e) in failures) ConsoleUi.Error($"  {n}: {e}");
        ConsoleUi.Info($"Saída: {outDir}");
        ConsoleUi.Warn("Obs.: a gravação é por evento; os trechos ficam emendados, então use o relógio na imagem como referência de horário.");
    }

    /// <summary>
    /// Os blocos hivXXXXX.mp4 do NVR são, na verdade, MPEG Program Stream com H.265/H.264.
    /// Remuxar direto para MP4 gera uma linha de tempo quebrada, então:
    /// 1) extrai o stream de vídeo cru; 2) remonta em MP4 com linha de tempo contínua no FPS da câmera.
    /// Tudo com -c copy (sem recodificação). O áudio é descartado.
    /// </summary>
    private async Task<ProcessResult> RemuxElementaryStreamAsync(
        string input, string output, string esFormat, double frameRate, string tempDir)
    {
        var raw = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(input) + "." + esFormat);
        Console.Write($"{esFormat} {frameRate:0.##} fps ... ");
        try
        {
            var r = await ffmpeg.FfmpegAsync("-y", "-i", input, "-map", "0:v:0", "-c", "copy", "-f", esFormat, raw);
            if (!r.Success) return r;
            if (!File.Exists(raw) || new FileInfo(raw).Length < 1024 * 1024)
                return new ProcessResult(1, "", "stream de vídeo vazio ou ilegível");

            return await ffmpeg.FfmpegAsync("-y", "-r", frameRate.ToString(CultureInfo.InvariantCulture),
                "-f", esFormat, "-i", raw, "-c", "copy", "-movflags", "+faststart", output);
        }
        finally
        {
            if (File.Exists(raw)) File.Delete(raw);
        }
    }

    /// <summary>Para arquivos que já são MP4: copia o vídeo e converte o áudio G.711 para AAC; se falhar, recodifica.</summary>
    private async Task<ProcessResult> ConvertMp4Async(string input, string output)
    {
        var r = await ffmpeg.FfmpegAsync("-y", "-i", input, "-map", "0:v:0", "-map", "0:a?",
            "-c:v", "copy", "-c:a", "aac", "-movflags", "+faststart", output);
        if (IsValidOutput(r, output)) return r;

        Console.Write("recodificando ... ");
        return await ffmpeg.FfmpegAsync("-y", "-i", input, "-map", "0:v:0", "-map", "0:a?",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-movflags", "+faststart", output);
    }

    private static bool IsProgramStream(VideoProbe? probe) =>
        probe != null && probe.Format.Split(',').Contains("mpeg");

    private static string? ElementaryFormat(string codec) => codec switch
    {
        "hevc" => "hevc",
        "h264" => "h264",
        _ => null,
    };

    private static double FrameRateOf(VideoProbe probe) =>
        probe.FrameRate is > 0 and <= 60 ? Math.Round(probe.FrameRate.Value, 3) : DefaultFrameRate;

    private static bool IsValidOutput(ProcessResult r, string output) =>
        r.Success && File.Exists(output) && new FileInfo(output).Length > 1024;
}
