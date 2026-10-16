using HikiHackVision.Features.Convert;
using HikiHackVision.Features.Cut;
using HikiHackVision.Features.Normalize;
using HikiHackVision.Infra;
using HikiHackVision.Menu;

namespace HikiHackVision.Features.Pipeline;

/// <summary>
/// Processo completo: converte (1), corta nas 06:00–18:00 (2) e normatiza em períodos de 15 minutos (3).
/// Cada etapa lê a saída da anterior. Os vídeos intermediários ficam numa pasta temporária que é apagada
/// no fim; só o resultado da normatização fica, em <c>&lt;raiz&gt;/prontos</c>.
/// </summary>
public class FullPipeline(FfmpegTools ffmpeg, string root)
{
    public async Task RunAsync()
    {
        var folder = ConsoleUi.AskFolder("Cole o caminho da pasta com os vídeos originais");
        if (folder == null) return;

        var outDir = ProjectPaths.EnsureDir(root, "prontos");
        var workDir = Path.Combine(Path.GetTempPath(), "hiki_completo");
        DeleteDir(workDir); // sobras de uma execução interrompida
        var convertedDir = Directory.CreateDirectory(Path.Combine(workDir, "convertidos")).FullName;
        var cutDir = Directory.CreateDirectory(Path.Combine(workDir, "cortados")).FullName;

        try
        {
            Stage(1, "Converter para MP4");
            await new VideoConverter(ffmpeg, root).ConvertFolderAsync(folder, convertedDir);
            if (ProjectPaths.ListVideos(convertedDir).Count == 0)
            {
                ConsoleUi.Error("Nenhum vídeo convertido; processo interrompido.");
                return;
            }

            Stage(2, "Cortar (manter 06h às 18h)");
            await new VideoCutter(ffmpeg, root).CutFolderAsync(convertedDir, cutDir);
            DeleteDir(convertedDir); // libera espaço antes da última etapa
            if (ProjectPaths.ListVideos(cutDir).Count == 0)
            {
                ConsoleUi.Error("Nenhum vídeo restou após o corte; processo interrompido.");
                return;
            }

            Stage(3, "Normatizar dias");
            await new DayNormalizer(ffmpeg, root).NormalizeFolderAsync(cutDir, outDir);

            Console.WriteLine();
            ConsoleUi.Ok($"Processo completo concluído. Vídeos prontos em: {outDir}");
        }
        finally
        {
            DeleteDir(workDir);
        }
    }

    private static void Stage(int number, string title)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"=== Etapa {number}/3: {title} ===");
        Console.ResetColor();
    }

    private static void DeleteDir(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { }
    }
}
