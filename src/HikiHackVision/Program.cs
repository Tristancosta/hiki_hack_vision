using System.Text;
using HikiHackVision.Features.Convert;
using HikiHackVision.Features.Cut;
using HikiHackVision.Infra;
using HikiHackVision.Menu;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

ConsoleUi.ShowBanner(); 

var root = ProjectPaths.FindRoot();
ConsoleUi.Info($"Pasta raiz: {root}");

FfmpegTools ffmpeg;
try
{
    ffmpeg = await FfmpegLocator.EnsureAsync(root);
}
catch (Exception ex)
{
    ConsoleUi.Error($"Não foi possível obter o FFmpeg: {ex.Message}");
    ConsoleUi.Info("Instale manualmente: baixe https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");
    ConsoleUi.Info($"e copie ffmpeg.exe e ffprobe.exe para {Path.Combine(root, "tools")}");
    ConsoleUi.Pause();
    return 1;
}

while (true)
{
    var option = ConsoleUi.ShowMenu();
    switch (option)
    {
        case "1":
            await new VideoConverter(ffmpeg, root).RunAsync();
            break;
        case "2":
            await new VideoCutter(ffmpeg, root).RunAsync();
            break;
        case "0":
            return 0;
        default:
            ConsoleUi.Warn("Opção inválida.");
            break;
    }
}
