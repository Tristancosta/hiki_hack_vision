using System.Globalization;
using HikiHackVision.Infra;

namespace HikiHackVision.Features.Cut;

/// <summary>Corte de um trecho por posição no arquivo, sem recodificar (-c copy).</summary>
public static class ClipCutter
{
    /// <summary>
    /// Com -c copy o corte começa num keyframe; usa o primeiro keyframe ≥ início para não incluir
    /// nada de antes do limite (nem de um evento anterior ao salto do relógio).
    /// </summary>
    public static double SnapToKeyframe(double start, List<double> keyframes) =>
        start > 0.05 ? keyframes.FirstOrDefault(k => k >= start - 0.001, double.MaxValue) : start;

    public static Task<ProcessResult> CutAsync(FfmpegTools ffmpeg, string input, Segment seg, string output)
    {
        // Início levemente após o keyframe: o seek de entrada volta para o keyframe exato
        var ss = seg.Start > 0 ? seg.Start + 0.01 : 0;
        return ffmpeg.FfmpegAsync("-y",
            "-ss", Fmt(ss), "-to", Fmt(seg.End), "-i", input,
            "-map", "0", "-c", "copy", "-avoid_negative_ts", "make_zero", output);
    }

    private static string Fmt(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
