namespace Skua.Core.Interfaces;

/// <summary>
/// Represents the inventory.
/// </summary>
/// <remarks>Implementations of this interface provide functionality for storing and equipping script items. This
/// interface extends <see cref="ICanEquip"/>, indicating that it supports equipping operations.</remarks>
public interface IScriptInventory : ICanEquip
{
    /// <summary>Checks the current character's local favorite mark.</summary>
    bool IsFavorited(int itemId);
}