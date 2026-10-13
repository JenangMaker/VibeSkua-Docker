namespace Skua.Core.Scripts;

/// <summary>
/// Script paths as written in <c>//cs_include</c> / <c>//cs_ref</c> lines. Scripts
/// are written on Windows, where the file system ignores case, and some
/// spell a folder differently from the repository (Story/AgeofRuin for
/// Story/AgeOfRuin); elsewhere those would not be found.
/// </summary>
public static class ScriptPaths
{
    /// <summary>
    /// Folder under the Scripts folder whose files replace the ones a
    /// <c>//cs_include</c> names (Scripts/Overrides/CoreBots.cs for
    /// Scripts/CoreBots.cs): patched copies kept apart from the synced scripts.
    /// VibeSkua syncs it from SKUA_SCRIPTS_OVERRIDE.
    /// </summary>
    public const string OverridesFolder = "Overrides";

    private static readonly HashSet<string> _noted = new();

    /// <summary>
    /// The file a <c>//cs_include</c> path (Scripts/X.cs) names under
    /// <paramref name="scriptsDir"/>: its override when there is one, else the file itself.
    /// </summary>
    public static string Include(string scriptsDir, string includePath)
    {
        string relative = includePath.Replace("Scripts/", "");
        string over = FixCase(Path.Combine(scriptsDir, OverridesFolder, relative));
        if (File.Exists(over))
        {
            lock (_noted)
                if (_noted.Add(over))
                    Console.WriteLine($"[scripts] {relative}: using the override {OverridesFolder}/{relative}");
            return over;
        }
        return FixCase(Path.Combine(scriptsDir, relative));
    }

    /// <summary>
    /// The path as it exists on disk, matching each missing part without
    /// regard to case; the path unchanged if it exists or nothing matches.
    /// </summary>
    public static string FixCase(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(path) || File.Exists(path) || Directory.Exists(path))
            return path;
        // Scripts\Story\X.cs: a folder separator on Windows, a file name character here.
        if (path.Contains('\\'))
        {
            string slashed = path.Replace('\\', '/');
            if (File.Exists(slashed) || Directory.Exists(slashed))
                return slashed;
            path = slashed;
        }
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full) ?? "/";
            string current = root;
            foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                string exact = Path.Combine(current, part);
                if (File.Exists(exact) || Directory.Exists(exact))
                {
                    current = exact;
                    continue;
                }
                if (!Directory.Exists(current))
                    return path;
                string? match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    return path;
                current = match;
            }
            return current;
        }
        catch
        {
            return path;
        }
    }
}
