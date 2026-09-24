using System.Diagnostics;

namespace HikiHackVision.Infra;

public record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string LastErrorLine =>
        StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? $"código de saída {ExitCode}";
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Falha ao iniciar {exe}");
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdOut, await stdErr);
    }

    /// <summary>Executa e devolve o stdout em bytes (ex.: quadro em rawvideo). Null se o processo falhar.</summary>
    public static async Task<byte[]?> RunBinaryAsync(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Falha ao iniciar {exe}");
        using var buffer = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        var stdErr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await copy;
        await stdErr;
        return process.ExitCode == 0 ? buffer.ToArray() : null;
    }
}
