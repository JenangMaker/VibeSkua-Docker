namespace Skua.Core.Services;

/// <summary>
/// The repository scripts are synced from: auqw/Scripts (branch Skua) on GitHub unless
/// SKUA_SCRIPTS_REPO names another one, on GitHub or a Gitea server
/// (e.g. https://gitea.example.com/owner/repo). SKUA_SCRIPTS_BRANCH picks the branch.
/// </summary>
public static class ScriptsSource
{
    public const string DefaultRepo = "https://github.com/auqw/Scripts";
    public const string DefaultBranch = "Skua";

    public static string Owner { get; }
    public static string Repo { get; }
    public static string Branch { get; }
    public static bool IsGitHub { get; }
    public static bool IsDefault { get; }

    /// <summary>owner/repo, for messages.</summary>
    public static string Name => $"{Owner}/{Repo}";

    /// <summary>Base URL of raw files on <see cref="Branch"/>, ending in a slash.</summary>
    public static string RawBase { get; }

    /// <summary>The repository's REST API root (GitHub or Gitea v1), no trailing slash.</summary>
    public static string ApiBase { get; }

    static ScriptsSource()
    {
        string repo = Env("SKUA_SCRIPTS_REPO") ?? DefaultRepo;
        Branch = Env("SKUA_SCRIPTS_BRANCH") ?? DefaultBranch;

        if (!Uri.TryCreate(repo.TrimEnd('/'), UriKind.Absolute, out Uri? uri) || uri.AbsolutePath.Trim('/').Split('/').Length < 2)
        {
            Console.Error.WriteLine($"[scripts] SKUA_SCRIPTS_REPO should look like https://host/owner/repo, not '{repo}'; using {DefaultRepo}");
            uri = new Uri(DefaultRepo);
        }
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');

        Owner = parts[^2];
        Repo = parts[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[^1][..^4] : parts[^1];
        IsGitHub = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        IsDefault = IsGitHub && Name.Equals("auqw/Scripts", StringComparison.OrdinalIgnoreCase) && Branch == DefaultBranch;

        // Gitea may live under a sub-path (https://host/gitea/owner/repo).
        string site = uri.GetLeftPart(UriPartial.Authority) + "/" + string.Join('/', parts[..^2]);
        site = site.TrimEnd('/');
        string branch = Uri.EscapeDataString(Branch);
        if (IsGitHub)
        {
            RawBase = $"https://raw.githubusercontent.com/{Owner}/{Repo}/refs/heads/{branch}/";
            ApiBase = $"https://api.github.com/repos/{Owner}/{Repo}";
        }
        else
        {
            RawBase = $"{site}/{Owner}/{Repo}/raw/branch/{branch}/";
            ApiBase = $"{site}/api/v1/repos/{Owner}/{Repo}";
        }
    }

    /// <summary>The raw URL of a file in the repository, by its repository path.</summary>
    public static string Raw(string path)
    {
        return RawBase + string.Join('/', path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
    }

    private static string? Env(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name)?.Trim().Trim('"', '\'').Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
