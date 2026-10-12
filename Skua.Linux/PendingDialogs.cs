namespace Skua.Linux;

/// <summary>
/// The message boxes a tab's Skua has open (a script's Bot.ShowMessageBox and
/// Skua's own), so they can be answered through the control API as well as on
/// the desktop: GET /status lists them (dialogs), POST /dialog/answer answers
/// one. The window that shows a message box adds it with what to do for each
/// button and removes it when it closes; whichever answer comes first wins.
/// </summary>
public static class PendingDialogs
{
    public sealed record Dialog(int Id, string Title, string Message, string[] Buttons, DateTime At);

    private sealed record Entry(Dialog Dialog, Action<int> Answer);

    private static readonly object Lock = new();
    private static readonly List<Entry> Open = new();
    private static int _nextId;

    /// <summary>Adds an open message box; answer(i) presses button i (on any thread).</summary>
    public static int Add(string title, string message, string[] buttons, Action<int> answer)
    {
        lock (Lock)
        {
            int id = ++_nextId;
            Open.Add(new Entry(new Dialog(id, title, message, buttons, DateTime.UtcNow), answer));
            return id;
        }
    }

    public static void Remove(int id)
    {
        lock (Lock)
            Open.RemoveAll(e => e.Dialog.Id == id);
    }

    /// <summary>The open message boxes, oldest first.</summary>
    public static List<Dialog> List()
    {
        lock (Lock)
            return Open.Select(e => e.Dialog).ToList();
    }

    /// <summary>Presses a button of an open message box, by its index or its text; null when it did, else why not.</summary>
    public static string? Answer(int id, string? button)
    {
        Entry? entry;
        lock (Lock)
            entry = Open.Find(e => e.Dialog.Id == id);
        if (entry is null)
            return $"no open message box {id} (answered already, or closed)";
        string[] buttons = entry.Dialog.Buttons;
        int index = int.TryParse(button, out int i) ? i
            : Array.FindIndex(buttons, b => string.Equals(b, button, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index >= buttons.Length)
            return $"no button '{button}': {string.Join(", ", buttons)}";
        Remove(id);
        entry.Answer(index);
        return null;
    }
}
