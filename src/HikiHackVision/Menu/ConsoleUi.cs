namespace HikiHackVision.Menu;

public static class ConsoleUi
{
    public static void ShowBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==============================================================");
        Console.WriteLine("                    HIKI HACK VISION");
        Console.WriteLine("==============================================================");
        Console.ResetColor();
        Console.WriteLine("Ferramentas rápidas para vídeos exportados de câmeras/NVR Hikvision:");
        Console.WriteLine(" - Converte os .mp4 proprietários da Hikvision para MP4 padrão,");
        Console.WriteLine("   que abrem em qualquer player.");
        Console.WriteLine(" - Corta os vídeos mantendo apenas a filmagem das 06:00 às 18:00,");
        Console.WriteLine("   lendo o horário gravado na imagem (canto superior esquerdo).");
        Console.WriteLine(" - Normatiza os dias: corta os vídeos em períodos de 15 minutos,");
        Console.WriteLine("   nomeados ddMMhhmm_x (x = número do dia na contagem).");
        Console.WriteLine();
    }

    public static string ShowMenu()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("---------------------------- MENU ----------------------------");
        Console.ResetColor();
        Console.WriteLine(" 1) Converter vídeos para MP4");
        Console.WriteLine(" 2) Cortar vídeos (manter 06h às 18h)");
        Console.WriteLine(" 3) Normatizar dias (períodos de 15 min, ddMMhhmm_x)");
        Console.WriteLine(" 0) Sair");
        Console.Write("O que deseja fazer? ");
        return (Console.ReadLine() ?? "0").Trim();
    }

    /// <summary>Pede uma pasta existente. Retorna null se o usuário deixar em branco.</summary>
    public static string? AskFolder(string prompt)
    {
        while (true)
        {
            Console.Write($"{prompt} (Enter para voltar): ");
            var input = (Console.ReadLine() ?? "").Trim().Trim('"', '\'').Trim();
            if (input.Length == 0) return null;
            if (Directory.Exists(input)) return Path.GetFullPath(input);
            Warn($"Pasta não encontrada: {input}");
        }
    }

    public static void Progress(int index, int total, string name) =>
        Console.Write($"[{index}/{total}] {name} ... ");

    public static void Ok(string msg = "OK") => WriteColored(msg, ConsoleColor.Green);
    public static void Warn(string msg) => WriteColored(msg, ConsoleColor.Yellow);
    public static void Error(string msg) => WriteColored(msg, ConsoleColor.Red);
    public static void Info(string msg) => Console.WriteLine(msg);

    public static void Pause()
    {
        Console.WriteLine("Pressione Enter para continuar...");
        Console.ReadLine();
    }

    private static void WriteColored(string msg, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(msg);
        Console.ResetColor();
    }
}
