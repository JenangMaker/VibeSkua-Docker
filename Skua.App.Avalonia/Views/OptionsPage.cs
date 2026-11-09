using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using Skua.Core.ViewModels;

namespace Skua.App.Avalonia.Views;

/// <summary>
/// A section of an options window: a chip in the chip row and, under "All", a header over its
/// rows. From upstream VibeSkua 2.1.2 (ViewModels/Options/OptionGroupViewModel), kept in the app
/// so Skua.Core's options view models stay as they are.
/// </summary>
public sealed class OptionGroup
{
    public string Title { get; init; } = "";
    public string ShortTitle { get; init; } = "";
    public MaterialIconKind Icon { get; init; } = MaterialIconKind.CogOutline;
    public List<DisplayOptionItemViewModelBase> Items { get; init; } = new();
    public bool IsAll { get; init; }
    public int Count => Items.Count;
}

/// <summary>
/// The 2.x options page around a list of options: search, section chips ("All" first), and the
/// visible groups. Group the options with <see cref="Group"/>; what no group takes goes to the last.
/// </summary>
public sealed class OptionsPage : ObservableObject
{
    private string _searchText = "";
    private OptionGroup _selected;

    public OptionsPage(IReadOnlyList<OptionGroup> groups)
    {
        Groups = groups.Where(g => g.Items.Count > 0).ToList();
        Sections = new[] { new OptionGroup { Title = "All", ShortTitle = "All", Icon = MaterialIconKind.Apps, IsAll = true, Items = Groups.SelectMany(g => g.Items).ToList() } }
            .Concat(Groups).ToList();
        _selected = Sections[0];
    }

    public List<OptionGroup> Groups { get; }
    public List<OptionGroup> Sections { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
                OnPropertyChanged(nameof(FilteredGroups));
        }
    }

    public OptionGroup SelectedSection
    {
        get => _selected;
        set
        {
            // The chip list clears its selection when its items change: keep the last one.
            if (value is not null && SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(FilteredGroups));
                OnPropertyChanged(nameof(ShowSectionHeaders));
            }
        }
    }

    public bool ShowSectionHeaders => _selected.IsAll;

    public IEnumerable<OptionGroup> FilteredGroups
    {
        get
        {
            IEnumerable<OptionGroup> groups = _selected.IsAll ? Groups : new[] { _selected };
            string text = _searchText.Trim();
            if (text.Length == 0)
                return groups;
            return groups.Select(g => new OptionGroup
            {
                Title = g.Title, ShortTitle = g.ShortTitle, Icon = g.Icon,
                Items = g.Items.Where(i => i.Content.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || (i.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)).ToList(),
            }).Where(g => g.Items.Count > 0).ToList();
        }
    }

    /// <summary>Groups by key; <paramref name="key"/> gives an option's key. Each spec is (title,
    /// short title, icon, keys); a spec with null keys takes everything left over.</summary>
    public static List<OptionGroup> Group(IEnumerable<DisplayOptionItemViewModelBase> options, Func<DisplayOptionItemViewModelBase, string> key,
        params (string Title, string Short, string Icon, string[]? Keys)[] specs)
    {
        var all = options.ToList();
        var used = new HashSet<DisplayOptionItemViewModelBase>();
        var groups = new List<OptionGroup>();
        foreach (var spec in specs)
        {
            List<DisplayOptionItemViewModelBase> items = spec.Keys is null
                ? new()
                : spec.Keys.Select(k => all.FirstOrDefault(o => key(o) == k)).OfType<DisplayOptionItemViewModelBase>().ToList();
            used.UnionWith(items);
            groups.Add(new OptionGroup { Title = spec.Title, ShortTitle = spec.Short, Icon = Enum.Parse<MaterialIconKind>(spec.Icon), Items = items });
        }
        int rest = Array.FindIndex(specs, s => s.Keys is null);
        if (rest >= 0)
            groups[rest].Items.AddRange(all.Where(o => !used.Contains(o)));
        return groups;
    }
}
