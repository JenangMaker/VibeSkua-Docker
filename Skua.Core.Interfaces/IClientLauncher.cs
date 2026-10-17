namespace Skua.Core.Interfaces;

/// <summary>
/// Launches an account's client. When one is registered, the Manager's Start
/// (AccountManagerViewModel) uses it instead of starting Skua.exe with -u/-p/-s:
/// VibeSkua's Avalonia Manager opens the account as a tab of a running VibeSkua.
/// </summary>
public interface IClientLauncher
{
    /// <param name="script">The script to load once logged in, or null for none.</param>
    /// <param name="autoStart">Whether to start that script once logged in.</param>
    void Launch(string username, string password, string server, string? script, bool autoStart, string displayName);
}
