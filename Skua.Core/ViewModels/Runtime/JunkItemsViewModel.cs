using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.Utils;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Skua.Core.ViewModels;

public sealed partial class JunkItemsViewModel : BotControlViewModelBase
{
    private readonly IScriptInventory _inventory;
    private readonly IScriptBank _bank;
    private readonly IJunkService _junkService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settings;

    public JunkItemsViewModel(
        IScriptInventory inventory,
        IScriptBank bank,
        IJunkService junkService,
        IDialogService dialogService,
        ISettingsService settings)
       : base("Junk Items", 700, 500)
    {
        _inventory = inventory;
        _bank = bank;
        _junkService = junkService;
        _dialogService = dialogService;
        _settings = settings;
        Items = new RangedObservableCollection<JunkItemEntry>();
        _skipSellWarning = _settings.Get("JunkSkipSellWarning", false);
    }

    public RangedObservableCollection<JunkItemEntry> Items { get; }

    // The categories of the listed items, for the view's category filter (upstream 1fb82a8).
    public ObservableCollection<string> CategoryOptions { get; } = new() { AllCategories };
    public const string AllCategories = "All Categories";

    // Bound to the category box; a refresh that rebuilds the list keeps it, or goes back to All.
    [ObservableProperty]
    private string _selectedCategory = AllCategories;

    [ObservableProperty]
    private bool _skipSellWarning;

    public int TotalJunk => Items.Count(i => i.IsJunk);

    protected override void OnActivated()
    {
        base.OnActivated();
        Refresh();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        _junkService.Load();

        var junkIds = _junkService.JunkItems
            .GroupBy(j => j.ID)
            .Select(g => g.Key)
            .ToHashSet();

        // Snapshot current selection state so we can restore it after reload
        var previousStates = Items
            .ToList()
            .ToDictionary(
                e => (e.ID, e.Category),
                e => e.IsSelected);

        List<JunkItemEntry> entries = await Task.Run(() =>
        {
            // Ensure bank is loaded so items list is accurate, but off the UI thread
            if (!_bank.Loaded)
                _bank.Load();

            // Take snapshots to avoid repeated enumeration while we build the list
            List<InventoryItem> inventoryItems = _inventory.Items.ToList();
            List<InventoryItem> bankItems = _bank.Items.ToList();

            return inventoryItems.Concat(bankItems)
                .GroupBy(i => i.ID)
                .Select(g =>
                {
                    InventoryItem item = g.First();
                    // A class's Quantity is its class points, so a class counts as 1.
                    int Count(List<InventoryItem> items) => items.Where(i => i.ID == item.ID).Sum(i => i.Category == ItemCategory.Class ? 1 : i.Quantity);
                    int inventoryQuantity = Count(inventoryItems);
                    int bankQuantity = Count(bankItems);
                    return new JunkItemEntry(item.ID, item.Name, item.Category.ToString(), item.Coins, inventoryQuantity, bankQuantity, junkIds.Contains(item.ID));
                })
                .OrderBy(i => i.Name)
                .ToList();
        });

        // Restore selection state where possible (same ID + category)
        foreach (JunkItemEntry entry in entries)
        {
            if (previousStates.TryGetValue((entry.ID, entry.Category), out bool isSelected))
                entry.IsSelected = isSelected;
        }

        Items.ReplaceRange(entries);
        OnPropertyChanged(nameof(TotalJunk));

        List<string> categories = entries.Select(e => e.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
        if (!categories.SequenceEqual(CategoryOptions.Skip(1)))
        {
            string selected = SelectedCategory;
            CategoryOptions.Clear();
            CategoryOptions.Add(AllCategories);
            foreach (string category in categories)
                CategoryOptions.Add(category);
            SelectedCategory = CategoryOptions.Contains(selected) ? selected : AllCategories;
        }
    }

    [RelayCommand]
    private void MarkAsJunk()
    {
        if (Items.Count == 0)
            return;

        var updated = Items.ToList();
        foreach (JunkItemEntry entry in updated.Where(e => e.IsSelected))
        {
            entry.IsJunk = true;
        }

        PersistFromEntries(updated);
    }

    [RelayCommand]
    private void UnmarkAsJunk()
    {
        if (Items.Count == 0)
            return;

        var updated = Items.ToList();
        foreach (JunkItemEntry entry in updated.Where(e => e.IsSelected))
        {
            entry.IsJunk = false;
            entry.IsSelected = false;
        }

        PersistFromEntries(updated);
    }

    [RelayCommand]
    private void UnmarkAllJunk()
    {
        if (Items.Count == 0)
            return;

        var updated = Items.ToList();
        foreach (JunkItemEntry entry in updated)
        {
            entry.IsJunk = false;
            entry.IsSelected = false;
        }

        PersistFromEntries(updated);
    }

    [RelayCommand]
    private void SellAllJunk()
    {
        if (Items.Count == 0)
            return;

        if (TotalJunk == 0)
            return;

        if (!SkipSellWarning)
        {
            bool? confirm = _dialogService.ShowMessageBox(
                "This will sell all items marked as junk in your inventory. This cannot be undone. Continue?",
                "Sell All Junk",
                yesAndNo: true);

            if (confirm != true)
                return;
        }

        _junkService.SellAllJunk();
        Refresh();
    }

    partial void OnSkipSellWarningChanged(bool value)
    {
        // When enabling skip, double-check user intent once.
        if (value)
        {
            bool? confirm = _dialogService.ShowMessageBox(
                "Are you sure you want to skip the confirmation when selling all junk?",
                "Skip Sell Warning",
                yesAndNo: true);

            if (confirm != true)
            {
                _skipSellWarning = false;
                OnPropertyChanged(nameof(SkipSellWarning));
                return;
            }
        }

        _settings.Set("JunkSkipSellWarning", value);
    }

    private void PersistFromEntries(List<JunkItemEntry> entries)
    {
        var configs = entries
            .Where(e => e.IsJunk)
            .Select(e => new JunkItemConfig
            {
                ID = e.ID,
                Name = e.Name,
                Category = e.Category,
                Meta = string.Empty
            })
            .ToList();

        _junkService.SetJunk(configs);

        OnPropertyChanged(nameof(TotalJunk));
    }
}

public sealed partial class JunkItemEntry : ObservableObject
{
    public JunkItemEntry(int id, string name, string category, bool isAc, int inventoryQuantity, int bankQuantity, bool isJunk)
    {
        ID = id;
        Name = name;
        Category = category;
        IsAc = isAc;
        InventoryQuantity = inventoryQuantity;
        BankQuantity = bankQuantity;
        _isJunk = isJunk;
    }

    public int ID { get; }
    public string Name { get; }
    public string Category { get; }
    public bool IsAc { get; }
    public int InventoryQuantity { get; }
    public int BankQuantity { get; }
    public string QuantityDisplay => $"Inv: {InventoryQuantity}  |  Bank: {BankQuantity}";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isJunk;

    public string Display => $"[{ID}] {Name} ({Category})";
}