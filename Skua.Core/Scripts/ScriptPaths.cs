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
