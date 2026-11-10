using System.Text.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Linux;

/// <summary>
/// A question one tab asks for all of them (the startup "Script Updates"), shown in every tab:
/// only the first tab syncs the scripts, but every tab's resumed or queued script waits for the
/// answer, and the tab on screen is often not tab 1. The asking tab writes the question to
/// .scripts-question in Skua's folder (shared by all tabs); the other tabs' watchers show it
/// too, so it is also listed in each tab's GET /status and can be answered with that tab's
/// POST /dialog/answer. The first answer, in any tab, goes to .scripts-answer; the asking tab
/// takes it, and every other copy closes.
/// </summary>
public static class SharedQuestion
{
    private static string QuestionFile => Path.Combine(ClientFileSources.SkuaDIR, ".scripts-question");
    private static string AnswerFile => Path.Combine(ClientFileSources.SkuaDIR, ".scripts-answer");

    private sealed record Question(string Owner, string Title, string Message, string[] Buttons);

    /// <summary>Asks in this tab and in every other one; the index of the button pressed, or -1.</summary>
    public static int Ask(IDialogService dialogs, string title, string message, string[] buttons)
    {
        string owner = ScriptSync.ProcessStamp(Environment.ProcessId);
        Delete(AnswerFile);
        Write(QuestionFile, JsonSerializer.Serialize(new Question(owner, title, message, buttons)));
        using var stop = new CancellationTokenSource();
        int remote = -1;
        // Another tab's answer presses the same button here.
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (ReadAnswer(owner) is int i && Find(title, message) is { } open)
                {
                    remote = i;
                    PendingDialogs.Answer(open.Id, i.ToString());
                    return;
                }
                try { await Task.Delay(1000, stop.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
        try
        {
            int answer = dialogs.ShowMessageBox(message, title, buttons).Value;
            if (answer >= 0 && remote < 0)
                Write(AnswerFile, $"{owner}|{answer}");
            return answer;
        }
        finally
        {
            stop.Cancel();
            Delete(QuestionFile);
        }
    }

    /// <summary>In every tab but the asking one: shows the shared question while it is open.</summary>
    public static async Task WatchAsync(IDialogService dialogs)
    {
        string me = ScriptSync.ProcessStamp(Environment.ProcessId);
        string? shown = null;
        while (true)
        {
            await Task.Delay(2000);
            Question? q = ReadQuestion();
            if (q is null || q.Owner == me || q.Owner == shown || !OwnerAlive(q.Owner) || ReadAnswer(q.Owner) is not null)
                continue;
            shown = q.Owner;
            _ = Task.Run(() => ShowCopy(dialogs, q));
        }
    }

    private static async Task ShowCopy(IDialogService dialogs, Question q)
    {
        bool closedFromElsewhere = false;
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(1000, stop.Token); }
                catch (OperationCanceledException) { return; }
                // Answered in another tab, or the asking tab is done (or gone): close this copy.
                bool over = ReadQuestion()?.Owner != q.Owner || ReadAnswer(q.Owner) is not null;
                if (over && Find(q.Title, q.Message) is { } open)
                {
                    closedFromElsewhere = true;
                    PendingDialogs.Answer(open.Id, (ReadAnswer(q.Owner) ?? q.Buttons.Length - 1).ToString());
                    return;
                }
            }
        });
        try
        {
            int answer = await Task.Run(() => dialogs.ShowMessageBox(q.Message, q.Title, q.Buttons).Value);
            if (answer >= 0 && !closedFromElsewhere && ReadAnswer(q.Owner) is null && ReadQuestion()?.Owner == q.Owner)
            {
                Write(AnswerFile, $"{q.Owner}|{answer}");
                Console.WriteLine($"[scripts] {q.Title}: answered {q.Buttons[answer]} in this tab");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[scripts] shared question: {e.Message}");
        }
        finally
        {
            stop.Cancel();
        }
    }

    private static PendingDialogs.Dialog? Find(string title, string message) =>
        PendingDialogs.List().LastOrDefault(d => d.Title == title && d.Message == message);

    private static bool OwnerAlive(string owner) =>
        int.TryParse(owner.Split(' ')[0], out int pid) && ScriptSync.ProcessStamp(pid) == owner;

    private static Question? ReadQuestion()
    {
        try { return File.Exists(QuestionFile) ? JsonSerializer.Deserialize<Question>(File.ReadAllText(QuestionFile)) : null; }
        catch { return null; }
    }

    private static int? ReadAnswer(string owner)
    {
        try
        {
            if (!File.Exists(AnswerFile))
                return null;
            string[] parts = File.ReadAllText(AnswerFile).Trim().Split('|');
            return parts.Length == 2 && parts[0] == owner && int.TryParse(parts[1], out int i) ? i : null;
        }
        catch { return null; }
    }

    // Written whole (temp file, then moved), so a reader never sees half of it.
    private static void Write(string file, string text)
    {
        try
        {
            string tmp = file + ".tmp" + Environment.ProcessId;
            File.WriteAllText(tmp, text);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[scripts] shared question: {e.Message}");
        }
    }

    private static void Delete(string file)
    {
        try { File.Delete(file); }
        catch { }
    }
}
