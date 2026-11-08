using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.ViewModels;

namespace Skua.Core.AppStartup;

internal static class LogTabs
{
    internal static IEnumerable<LogTabViewModel> CreateViewModels(IServiceProvider s)
    {
        ILogService logService = s.GetRequiredService<ILogService>();
        IDispatcherService dispatcherService = s.GetRequiredService<IDispatcherService>();
        return new[]
        {
            Create("Debug", LogType.Debug),
            Create("Script", LogType.Script),
            // The game runs in Ruffle, not Flash: this tab shows the player's own log lines
            // (Skua.Linux/NativeGame.cs) and the game calls that failed.
            Create("Ruffle", LogType.Flash)
        };

        LogTabViewModel Create(string title, LogType logType) => new(title, logService, dispatcherService, logType);
    }
}