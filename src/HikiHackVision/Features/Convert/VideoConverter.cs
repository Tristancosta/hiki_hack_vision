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

        await ConvertFolderAsync(folder, ProjectPaths.EnsureDir(root, "convertidos"));
    }

    /// <summary>Converte os vídeos de <paramref name="folder"/> para <paramref name="outDir"/> (já existente).</summary>
    public async Task ConvertFolderAsync(string folder, string outDir)
    {
        var files = ProjectPaths.ListVideos(folder);
        ConsoleUi.Info($"Encontrados {files.Count} vídeo(s).");
        if (files.Count == 0) return;

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

                if (IsDone(output) || IsDone(PartPath(output, 1)))
                {
                    ConsoleUi.Warn("já convertido, pulando");
                    skipped++;
                    continue;
                }

                var probe = await ffmpeg.ProbeAsync(input);
                var isPs = IsProgramStream(probe) && ElementaryFormat(probe!.VideoCodec) != null;
                var fps = isPs ? await ffmpeg.MeasureFrameRateAsync(input) ?? FrameRateOf(probe!) : 0;
                var r = isPs
                    ? await RemuxElementaryStreamAsync(input, output, ElementaryFormat(probe!.VideoCodec)!, fps, tempDir)
                    : await ConvertMp4Async(input, output);

                if (IsValidOutput(r, output) && isPs)
                    r = await SplitByResolutionAsync(output, ElementaryFormat(probe!.VideoCodec)!, fps, tempDir);

                if (IsValidOutput(r, output))
                {
                    ConsoleUi.Ok($"OK ({new FileInfo(output).Length / (1024 * 1024)} MB)");
                }
                else if (r.Success && IsDone(PartPath(output, 1)))
                {
                    var parts = Enumerable.Range(1, 99).Select(n => PartPath(output, n)).TakeWhile(File.Exists).ToList();
                    ConsoleUi.Ok($"OK em {parts.Count} partes ({parts.Sum(p => new FileInfo(p).Length) / (1024 * 1024)} MB)");
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

    /// <summary>
    /// O NVR pode trocar de resolução no meio de um bloco (ex.: 2560x1440 → 1280x720). Num único MP4 o cabeçalho
    /// fica com a 1ª resolução e os quadros seguintes são mal interpretados (cortes quebrados, OSD fora do lugar).
    /// Nesse caso divide em <c>nome_1.mp4</c>, <c>nome_2.mp4</c>, … um por resolução, e apaga o MP4 misto.
    /// Cada parte é re-extraída como stream cru e remontada, para o cabeçalho refletir a sua própria resolução.
    /// </summary>
    private async Task<ProcessResult> SplitByResolutionAsync(string output, string esFormat, double frameRate, string tempDir)
    {
        var runs = await ffmpeg.GetResolutionRunsAsync(output);
        if (runs.Count <= 1) return new ProcessResult(0, "", "");

        Console.Write($"{runs.Count} resoluções ({string.Join(" → ", runs.Select(x => $"{x.Width}x{x.Height}"))}), dividindo ... ");
        var fps = frameRate.ToString(CultureInfo.InvariantCulture);
        for (var k = 0; k < runs.Count; k++)
        {
            var part = PartPath(output, k + 1);
            var raw = Path.Combine(tempDir, $"part{k + 1}.{esFormat}");
            try
            {
                // Cada trecho começa num keyframe: +0,01 garante que a busca caia nele e não no anterior
                List<string> args = ["-y"];
                if (k > 0) args.AddRange(["-ss", (runs[k].Start + 0.01).ToString("0.###", CultureInfo.InvariantCulture)]);
                if (k + 1 < runs.Count) args.AddRange(["-to", runs[k + 1].Start.ToString("0.###", CultureInfo.InvariantCulture)]);
                args.AddRange(["-i", output, "-map", "0:v:0", "-c", "copy", "-f", esFormat, raw]);

                // Blocos do NVR costumam terminar num pacote truncado: o ffmpeg grava todo o resto mas sai com erro
                // ("Invalid data found when processing input"). Isso descartava o hiv00013 inteiro (22/09 até 09:53).
                // Com o stream extraído, segue; o remux abaixo é que valida a parte.
                var r = await ffmpeg.FfmpegAsync([.. args]);
                if (!r.Success && File.Exists(raw) && new FileInfo(raw).Length > 1024)
                    r = new ProcessResult(0, "", "");
                if (r.Success)
                    r = await ffmpeg.FfmpegAsync("-y", "-r", fps, "-f", esFormat, "-i", raw, "-c", "copy",
                        "-movflags", "+faststart", part);
                if (!IsValidOutput(r, part))
                {
                    for (var j = 1; j <= k + 1; j++) File.Delete(PartPath(output, j));
                    return r.Success ? new ProcessResult(1, "", "falha ao dividir por resolução") : r;
                }
            }
            finally
            {
                if (File.Exists(raw)) File.Delete(raw);
            }
        }
        File.Delete(output);
        return new ProcessResult(0, "", "");
    }

    private static string PartPath(string output, int n) =>
        Path.Combine(Path.GetDirectoryName(output)!, $"{Path.GetFileNameWithoutExtension(output)}_{n}.mp4");

    private static bool IsDone(string path) => File.Exists(path) && new FileInfo(path).Length > 1024;

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
