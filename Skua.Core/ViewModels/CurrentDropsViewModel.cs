using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models.Items;

namespace Skua.Core.ViewModels;

public partial class CurrentDropsViewModel : BotControlViewModelBase
{
    public CurrentDropsViewModel(IScriptDrop drops, IScriptPlayer player)
        : base("Current Drops", 500, 400)
    {
        _drops = drops;
        _player = player;
    }

    protected override void OnActivated()
    {
        StrongReferenceMessenger.Default.Register<CurrentDropsViewModel, ItemDroppedMessage, int>(this, (int)MessageChannels.GameEvents, CurrentDropsChanged);
        StrongReferenceMessenger.Default.Register<CurrentDropsViewModel, DropPickupResultMessage, int>(this, (int)MessageChannels.GameEvents, PickupResultReceived);
        OnPropertyChanged(nameof(CurrentDrops));
    }

    protected override void OnDeactivated()
    {
        StrongReferenceMessenger.Default.UnregisterAll(this);
    }

    private readonly IScriptPlayer _player;
    private readonly IScriptDrop _drops;

    [ObservableProperty]
    private ItemBase? _selectedDrop;

    public List<ItemBase> CurrentDrops
    {
        get
        {
            List<ItemBase> drops = new();
            foreach (ItemBase drop in _drops.CurrentDropInfos)
                drops.Add(drop);
            return drops;
        }
    }

    [RelayCommand]
    private void Pickup()
    {
        if (SelectedDrop is null || !_player.Playing)
            return;

        _drops.Pickup(SelectedDrop.ID);
    }

    [RelayCommand]
    private void PickupSelected(IList<object>? items)
    {
        if (items is null || items.Count == 0 || !_player.Playing)
            return;

        int[] dropIds = new int[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] is ItemBase ib)
                dropIds[i] = ib.ID;
        }
        // Pickup(int[]) waits for the server's reply: don't block the UI thread.
        Task.Run(() => _drops.Pickup(dropIds));
    }

    [RelayCommand]
    private void PickupAll()
    {
        _drops.PickupAll(true);
    }

    [RelayCommand]
    private void PickupAC()
    {
        Task.Run(() => _drops.PickupACItems());
    }

    private void CurrentDropsChanged(CurrentDropsViewModel recipient, ItemDroppedMessage message)
    {
        recipient.OnPropertyChanged(nameof(recipient.CurrentDrops));
    }

    // A drop leaves the list only when the server accepts the pickup.
    private void PickupResultReceived(CurrentDropsViewModel recipient, DropPickupResultMessage message)
    {
        recipient.OnPropertyChanged(nameof(recipient.CurrentDrops));
    }
}