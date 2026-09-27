using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using System.Text;

namespace Skua.Core.ViewModels;

public partial class CoreBotsViewModel : BotControlViewModelBase
{
    public CoreBotsViewModel(List<TabItemViewModel> tabs, IScriptPlayer player, IDialogService dialogService)
        : base("CoreBots Options")
    {
        CoreBotsTabs = tabs;
        _selectedTab = CoreBotsTabs[0];
        _player = player;
        _dialogService = dialogService;
    }

    protected override void OnActivated()
    {
        if (!string.IsNullOrWhiteSpace(_player?.Username))
        {
            Title = $"CoreBots Options - {_player.Username}";
            OnPropertyChanged(nameof(Title));
        }
        Load();
    }

    protected override void OnDeactivated()
    {
        // Auto-save when closing the window
        if (!string.IsNullOrEmpty(_player.Username))
        {
            SaveInternal(showDialog: false);
        }
        base.OnDeactivated();
    }

    private readonly IScriptPlayer _player;
    private readonly IDialogService _dialogService;
    private Dictionary<string, Dictionary<string, string>> _readValues = new();

    [ObservableProperty]
    private TabItemViewModel _selectedTab;

    [ObservableProperty]
    private string _currentPlayer = string.Empty;

    public List<TabItemViewModel> CoreBotsTabs { get; }

    [RelayCommand]
    private void Save()
    {
        SaveInternal(showDialog: true);
    }

    private void SaveInternal(bool showDialog)
    {
        if (string.IsNullOrEmpty(_player.Username))
        {
            if (showDialog)
            {
                _dialogService.ShowMessageBox("Login first so that we can fetch your username for the save file", "Save");
                CurrentPlayer = string.Empty;
            }
            return;
        }

        StringBuilder bob = new();
        foreach (TabItemViewModel tab in CoreBotsTabs)
        {
            if (tab.Content is IManageCBOptions cbo)
                cbo.Save(bob);
        }
        string file = StorageFile(_player.Username);
        Directory.CreateDirectory(ClientFileSources.SkuaOptionsDIR);
        File.WriteAllText(file, bob.ToString());
        if (showDialog)
        {
            _dialogService.ShowMessageBox($"Saved to {file}", "Save Successful!");
        }
        _readValues[_player.Username] = ReadValues(File.ReadAllLines(file));
    }

    /// <summary>
    /// options/CBO_Storage(user).txt, where CoreBots reads it. This was built
    /// as SkuaOptionsDIR + @"\CBO_Storage(...)", which off Windows is a file
    /// named "options\CBO_Storage(...).txt" beside the options folder, so the
    /// scripts never saw what was saved here; such a file is moved into place.
    /// </summary>
    private static string StorageFile(string username)
    {
        string file = Path.Combine(ClientFileSources.SkuaOptionsDIR, $"CBO_Storage({username}).txt");
        if (!OperatingSystem.IsWindows() && !File.Exists(file))
        {
            string misplaced = ClientFileSources.SkuaOptionsDIR + $@"\CBO_Storage({username}).txt";
            try
            {
                if (File.Exists(misplaced))
                {
                    Directory.CreateDirectory(ClientFileSources.SkuaOptionsDIR);
                    File.Move(misplaced, file);
                }
            }
            catch
            {
            }
        }
        return file;
    }

    [RelayCommand]
    private void Load()
    {
        if (string.IsNullOrEmpty(_player.Username))
        {
            _dialogService.ShowMessageBox("Login first so that we can fetch your username to load the options file.", "Load");
            CurrentPlayer = string.Empty;
            return;
        }

        CurrentPlayer = _player.Username;
        if (_readValues.ContainsKey(_player.Username))
        {
            SetValues(_readValues[_player.Username]);
            return;
        }

        string file = StorageFile(_player.Username);
        if (!File.Exists(file))
            return;

        Dictionary<string, string> optionsDict = ReadValues(File.ReadAllLines(file));

        SetValues(optionsDict);

        _readValues.Add(_player.Username, optionsDict);
    }

    private Dictionary<string, string> ReadValues(IEnumerable<string> lines)
    {
        Dictionary<string, string> optionsDict = new();
        foreach (string option in lines)
        {
            ReadOnlySpan<string> value = option.Split(_separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (value.Length == 2)
                optionsDict.Add(value[0], value[1]);
        }
        return optionsDict;
    }

    private void SetValues(Dictionary<string, string> options)
    {
        foreach (TabItemViewModel tab in CoreBotsTabs)
        {
            if (tab.Content is IManageCBOptions setable)
                setable.SetValues(options);
        }
    }

    private readonly char _separator = ':';
}

internal interface IManageCBOptions
{
    StringBuilder Save(StringBuilder builder);

    void SetValues(Dictionary<string, string> values);
}