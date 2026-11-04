using System.Globalization;
using System.IO.Compression;
using HikiHackVision.Menu;

namespace HikiHackVision.Infra;

public record FfmpegTools(string Ffmpeg, string Ffprobe)
{
    public Task<ProcessResult> FfmpegAsync(params string[] args) =>
        ProcessRunner.RunAsync(Ffmpeg, ["-hide_banner", "-loglevel", "error", .. args]);

    /// <summary>Duração do vídeo em segundos, ou null se não for possível ler.</summary>
    public async Task<double?> GetDurationAsync(string file)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file]);
        return r.Success && double.TryParse(r.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    /// <summary>Contêiner (ex.: "mpeg", "mov,mp4,...") e codec/fps do primeiro stream de vídeo.</summary>
    public async Task<VideoProbe?> ProbeAsync(string file)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "format=format_name:stream=codec_name,r_frame_rate",
             "-of", "default=noprint_wrappers=1", file]);
        if (!r.Success) return null;

        var values = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);
        return new VideoProbe(
            values.GetValueOrDefault("format_name", ""),
            values.GetValueOrDefault("codec_name", ""),
            ParseRate(values.GetValueOrDefault("r_frame_rate", "")));
    }

    /// <summary>Largura e altura do primeiro stream de vídeo.</summary>
    public async Task<(int Width, int Height)?> GetFrameSizeAsync(string file)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", file]);
        var parts = r.StdOut.Trim().Split(',');
        return r.Success && parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)
            ? (w, h)
            : null;
    }

    /// <summary>Instantes (s) dos keyframes do vídeo, lidos dos pacotes (sem decodificar).</summary>
    public async Task<List<double>> GetKeyframesAsync(string file)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", file]);
        var result = new List<double>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length >= 2 && parts[1].Contains('K')
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                result.Add(t);
        }
        result.Sort();
        return result;
    }

    /// <summary>
    /// FPS real da câmera medido pelos intervalos entre pacotes (sem decodificar). O NVR mistura quadros a
    /// 0,1s (movimento) com quadros a cada 1-2s (sem movimento), e o r_frame_rate do cabeçalho não reflete
    /// nenhum dos dois (visto 1 e 30 para câmeras a 10 fps). Usa a mediana dos intervalos curtos (&lt; 0,5s).
    /// </summary>
    public async Task<double?> MeasureFrameRateAsync(string file)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", file]);
        var times = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => double.TryParse(l.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : double.NaN)
            .Where(t => !double.IsNaN(t))
            .ToList();
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).Where(d => d is > 0.01 and < 0.5).Order().ToList();
        if (gaps.Count < 50) return null;
        var fps = Math.Round(1 / gaps[gaps.Count / 2]);
        return fps is >= 1 and <= 60 ? fps : null;
    }

    /// <summary>
    /// Trechos de resolução constante: início (s) de cada mudança de largura/altura, lida só dos keyframes
    /// (a resolução só muda num keyframe, com novos parâmetros do codec).
    /// </summary>
    public async Task<List<(double Start, int Width, int Height)>> GetResolutionRunsAsync(string file)
    {
        var r = await ProcessRunner.RunAsync(Ffprobe,
            ["-v", "error", "-skip_frame", "nokey", "-select_streams", "v:0",
             "-show_entries", "frame=pts_time,width,height", "-of", "csv=p=0", file]);
        var runs = new List<(double Start, int Width, int Height)>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(',');
            if (p.Length < 3
                || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                || !int.TryParse(p[1], out var w) || !int.TryParse(p[2], out var h)) continue;
            if (runs.Count == 0 || runs[^1].Width != w || runs[^1].Height != h) runs.Add((t, w, h));
        }
        return runs;
    }

    /// <summary>Um quadro na posição indicada, recortado no canto superior esquerdo, em cinza (1 byte/pixel).</summary>
    public Task<byte[]?> ExtractGrayCornerAsync(string file, double seconds, int width, int height) =>
        ProcessRunner.RunBinaryAsync(Ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-ss", seconds.ToString("0.###", CultureInfo.InvariantCulture),
             "-i", file, "-frames:v", "1", "-vf", $"crop={width}:{height}:0:0,format=gray", "-f", "rawvideo", "-"]);

    private static double? ParseRate(string rate)
    {
        var parts = rate.Split('/');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            || den == 0) return null;
        return num / den;
    }
}

public record VideoProbe(string Format, string VideoCodec, double? FrameRate);

public static class FfmpegLocator
{
    private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static async Task<FfmpegTools> EnsureAsync(string root)
    {
        var toolsDir = Path.Combine(root, "tools");
        var local = new FfmpegTools(Path.Combine(toolsDir, "ffmpeg.exe"), Path.Combine(toolsDir, "ffprobe.exe"));
        if (File.Exists(local.Ffmpeg) && File.Exists(local.Ffprobe)) return local;

        var ffmpegOnPath = FindOnPath("ffmpeg.exe");
        var ffprobeOnPath = FindOnPath("ffprobe.exe");
        if (ffmpegOnPath != null && ffprobeOnPath != null) return new FfmpegTools(ffmpegOnPath, ffprobeOnPath);

        ConsoleUi.Info("FFmpeg não encontrado. Baixando (apenas na primeira execução)...");
        Directory.CreateDirectory(toolsDir);
        var zipPath = Path.Combine(toolsDir, "ffmpeg.zip");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long read = 0;
            int n, lastPct = -1;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                read += n;
                if (total > 0)
                {
                    var pct = (int)(read * 100 / total.Value);
                    if (pct != lastPct) { Console.Write($"\r  {pct}%   "); lastPct = pct; }
                }
            }
            Console.WriteLine();
        }

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"{name} não encontrado no pacote baixado");
                entry.ExtractToFile(Path.Combine(toolsDir, name), overwrite: true);
            }
        }
        File.Delete(zipPath);
        ConsoleUi.Ok("FFmpeg instalado em " + toolsDir);
        return local;
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), exe))
            .FirstOrDefault(File.Exists);
}
