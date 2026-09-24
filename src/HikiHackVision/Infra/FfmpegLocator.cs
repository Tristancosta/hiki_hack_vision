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
