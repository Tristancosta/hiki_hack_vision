namespace HikiHackVision.Infra;

public static class ProjectPaths
{
    /// <summary>
    /// Raiz do projeto: sobe a partir da pasta do executável até achar a solução.
    /// Se não achar (ex.: exe publicado e copiado para outro lugar), usa a pasta do exe.
    /// </summary>
    public static string FindRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("HikiHackVision.sln*").Any())
                return dir.FullName;
        }
        return baseDir.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static string EnsureDir(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static List<string> ListVideos(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => string.Equals(Path.GetExtension(f), ".mp4", StringComparison.OrdinalIgnoreCase))
            .Where(f => new FileInfo(f).Length > 0) // o NVR pré-aloca blocos vazios

            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
