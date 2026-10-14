using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;

namespace Skua.Linux;

/// <summary>
/// Notifications on a tab's script progress, sent to ntfy and/or by email.
/// Off unless NOTIFY_NTFY_URL or NOTIFY_SMTP_HOST is set. Each setting can be
/// given for one tab as NAME_N (NOTIFY_NTFY_URL_2 is tab 2's), else NAME is
/// every tab's.
///
///   NOTIFY_NTFY_URL      topic URL, e.g. https://ntfy.sh/my-secret-topic
///   NOTIFY_NTFY_TOKEN    access token; or NOTIFY_NTFY_USER + NOTIFY_NTFY_PASS
///   NOTIFY_SMTP_HOST     mail server; NOTIFY_SMTP_PORT (587), NOTIFY_SMTP_TLS
///                        (STARTTLS, on), NOTIFY_SMTP_USER, NOTIFY_SMTP_PASS,
///                        NOTIFY_SMTP_FROM (the user), NOTIFY_SMTP_TO (comma list)
///   NOTIFY_EVENTS        which events (comma list, default all):
///                        start, stop, error, relogin, queue, drop, log, ping
///   NOTIFY_NTFY_EVENTS, NOTIFY_SMTP_EVENTS  the same, for one target only
///   NOTIFY_DROPS         drop: items whose name contains one of these (comma list)
///   NOTIFY_LOG           log: script log lines matching this regex (case ignored)
///   NOTIFY_PING_MINUTES  ping: every N minutes while a script runs
///   NOTIFY_STOP_LINES    the last script log lines a stop shows (5)
///
/// Events close together go out as one notification, and each target sends at
/// most 30 an hour, so a chatty NOTIFY_LOG cannot flood a phone or a mailbox.
/// The account name is left out (the tab number says which); ntfy.sh topics
/// are public to whoever knows the name.
/// </summary>
public sealed class Notifier
{
    public const string Start = "start", Stop = "stop", Error = "error", Relogin = "relogin",
        QueueStep = "queue", Drop = "drop", Log = "log", Ping = "ping";

    private static readonly string[] AllEvents = [Start, Stop, Error, Relogin, QueueStep, Drop, Log, Ping];
    private static readonly ConcurrentDictionary<int, Notifier> Tabs = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly int _tab;
    private readonly List<Target> _targets = new();

    // Script state, in the tab's own Skua.
    private readonly object _lock = new();
    private string? _script;
    private DateTime _startedAt;
    private readonly Queue<string> _recent = new();
    private Regex? _logMatch;
    private string[] _drops = [];
    private Timer? _ping;

    private Notifier(int tab)
    {
        _tab = tab;
        string? ntfy = Setting("NOTIFY_NTFY_URL");
        if (ntfy is not null)
            _targets.Add(new NtfyTarget(this, ntfy));
        string? smtp = Setting("NOTIFY_SMTP_HOST");
        if (smtp is not null)
            _targets.Add(new MailTarget(this, smtp));
    }

    /// <summary>Tab <paramref name="tab"/>'s notifier (tabs count from 1).</summary>
    public static Notifier For(int tab) => Tabs.GetOrAdd(tab, n => new Notifier(n));

    public bool Enabled => _targets.Count > 0;

    private string? Setting(string name) => SkuaRuntime.EnvRaw($"{name}_{_tab}") ?? SkuaRuntime.EnvRaw(name);

    private static string Duration(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

    /// <summary>
    /// Watches this Skua's script for the events: started, stopped, crashed,
    /// relogins, drops and its log.
    /// </summary>
    public void Watch(IServiceProvider services)
    {
        if (!Enabled)
            return;
        Console.WriteLine($"[notify] tab {_tab}: sending to {string.Join(", ", _targets.Select(t => t.Name))}");
        _drops = (Setting("NOTIFY_DROPS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (Setting("NOTIFY_LOG") is { } pattern)
        {
            try
            {
                _logMatch = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine($"[notify] NOTIFY_LOG is not a valid regex: {e.Message}");
            }
        }

        var manager = services.GetRequiredService<IScriptManager>();
        var status = StrongReferenceMessenger.Default;
        status.Register<Notifier, ScriptStartedMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, _) => r.OnStarted(manager));
        status.Register<Notifier, ScriptStoppedMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, _) => r.OnStopped());
        status.Register<Notifier, ScriptErrorMessage, int>(this, (int)MessageChannels.ScriptStatus, (r, m) => r.OnError(m.Exception));
        status.Register<Notifier, ReloginTriggeredMessage, int>(this, (int)MessageChannels.GameEvents, (r, m) =>
            r.Post(Relogin, m.WasKicked ? "Kicked; relogging" : "Disconnected; relogging", ""));
        status.Register<Notifier, ItemDroppedMessage, int>(this, (int)MessageChannels.GameEvents, (r, m) =>
        {
            if (m.AddedToInv && m.Item is { } item)
                r.OnItem(item.Name, m.QuantityNow, null);
        });
        status.Register<Notifier, ItemAddedToBankMessage, int>(this, (int)MessageChannels.GameEvents, (r, m) =>
        {
            if (m.Item is { } item)
                r.OnItem(item.Name, m.QuantityNow, "bank");
        });
        // LogService sends these on the default (weak) messenger; this
        // notifier lives as long as the process (Tabs holds it).
        WeakReferenceMessenger.Default.Register<Notifier, AddLogMessage>(this, (r, m) =>
        {
            if (m.LogType == LogType.Script)
                r.OnScriptLog(m.Text);
        });
    }

    private void OnStarted(IScriptManager manager)
    {
        lock (_lock)
        {
            _script = Path.GetFileNameWithoutExtension(manager.LoadedScript ?? "");
            if (_script.Length == 0)
                _script = "a script";
            _startedAt = DateTime.UtcNow;
            _recent.Clear();
        }
        Post(Start, $"Started {_script}", "");
        if (int.TryParse(Setting("NOTIFY_PING_MINUTES"), out int minutes) && minutes > 0)
        {
            var every = TimeSpan.FromMinutes(minutes);
            _ping?.Dispose();
            _ping = new Timer(_ =>
            {
                string? script;
                DateTime since;
                lock (_lock)
                    (script, since) = (_script, _startedAt);
                if (script is not null)
                    Post(Ping, $"{script} still running", $"Running for {Duration(DateTime.UtcNow - since)}.");
            }, null, every, every);
        }
    }

    private void OnStopped()
    {
        _ping?.Dispose();
        _ping = null;
        string script;
        TimeSpan ran;
        string[] last;
        lock (_lock)
        {
            if (_script is null)
                return;
            script = _script;
            ran = DateTime.UtcNow - _startedAt;
            int lines = int.TryParse(Setting("NOTIFY_STOP_LINES"), out int n) && n >= 0 ? n : 5;
            last = _recent.Skip(Math.Max(0, _recent.Count - lines)).ToArray();
            _script = null;
        }
        var body = new StringBuilder($"Ran for {Duration(ran)}.");
        if (last.Length > 0)
            body.Append("\nLast log lines:\n").AppendJoin("\n", last);
        else
            body.Append("\nIt logged nothing.");
        Post(Stop, $"Stopped {script}", body.ToString());
    }

    private void OnError(Exception e)
    {
        string script;
        lock (_lock)
            script = _script ?? "The script";
        Post(Error, $"{script} crashed", $"{e.GetType().Name}: {e.Message}");
    }

    private void OnItem(string name, int quantity, string? where)
    {
        if (_drops.Any(d => name.Contains(d, StringComparison.OrdinalIgnoreCase)))
            Post(Drop, $"Got {name}", $"Now {quantity}{(where is null ? "" : $" (in the {where})")}.");
    }

    private void OnScriptLog(string line)
    {
        lock (_lock)
        {
            _recent.Enqueue(line);
            while (_recent.Count > 50)
                _recent.Dequeue();
        }
        try
        {
            if (_logMatch?.IsMatch(line) == true)
                Post(Log, "Script log", line);
        }
        catch (RegexMatchTimeoutException)
        {
        }
    }

    /// <summary>Queues a notification for each target that takes <paramref name="kind"/>.</summary>
    public void Post(string kind, string title, string body)
    {
        foreach (var target in _targets)
            target.Post(kind, title, body);
    }

    /// <summary>Sends a test to every target now; each target's error, or null when it went out.</summary>
    public async Task<Dictionary<string, string?>> TestAsync()
    {
        var results = new Dictionary<string, string?>();
        foreach (var target in _targets)
        {
            try
            {
                await target.SendAsync([new Item(Start, "Test notification", "VibeSkua can reach you here.")]);
                results[target.Name] = null;
            }
            catch (Exception e)
            {
                results[target.Name] = e.Message;
            }
        }
        return results;
    }

    private sealed record Item(string Kind, string Title, string Body);

    /// <summary>
    /// One place notifications go: its events, a short wait to send what comes
    /// together as one, and its hourly limit.
    /// </summary>
    private abstract class Target
    {
        private const int PerHour = 30;
        private static readonly TimeSpan Gather = TimeSpan.FromSeconds(5);

        protected readonly Notifier Owner;
        private readonly HashSet<string> _events;
        private readonly List<Item> _pending = new();
        private readonly Queue<DateTime> _sent = new();
        private int _skipped;
        private bool _flushing;

        protected Target(Notifier owner, string eventsSetting)
        {
            Owner = owner;
            string list = owner.Setting(eventsSetting) ?? owner.Setting("NOTIFY_EVENTS") ?? string.Join(',', AllEvents);
            _events = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.ToLowerInvariant()).ToHashSet();
            foreach (string unknown in _events.Except(AllEvents))
                Console.Error.WriteLine($"[notify] unknown event \"{unknown}\" (known: {string.Join(", ", AllEvents)})");
        }

        public abstract string Name { get; }

        public void Post(string kind, string title, string body)
        {
            if (!_events.Contains(kind))
                return;
            lock (_pending)
            {
                _pending.Add(new Item(kind, title, body));
                if (_flushing)
                    return;
                _flushing = true;
            }
            _ = Task.Run(FlushAsync);
        }

        private async Task FlushAsync()
        {
            await Task.Delay(Gather);
            Item[] items;
            int skipped;
            lock (_pending)
            {
                items = _pending.ToArray();
                _pending.Clear();
                _flushing = false;
                while (_sent.Count > 0 && DateTime.UtcNow - _sent.Peek() > TimeSpan.FromHours(1))
                    _sent.Dequeue();
                if (_sent.Count >= PerHour)
                {
                    _skipped += items.Length;
                    return;
                }
                _sent.Enqueue(DateTime.UtcNow);
                skipped = _skipped;
                _skipped = 0;
            }
            if (skipped > 0)
                items = [.. items, new Item(Log, "", $"({skipped} more left out: over {PerHour} notifications an hour)")];
            try
            {
                await SendAsync(items);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[notify] {Name}: {e.Message}");
            }
        }

        public abstract Task SendAsync(IReadOnlyList<Item> items);

        // One item: its own title and body; several: a count, and each
        // item's title and body as a paragraph.
        protected string Title(IReadOnlyList<Item> items) =>
            $"Tab {Owner._tab}: " + (items.Count == 1 ? items[0].Title : $"{items.Count(i => i.Title.Length > 0)} events");

        protected static string Body(IReadOnlyList<Item> items) =>
            items.Count == 1
                ? items[0].Body
                : string.Join("\n\n", items.Select(i => i.Title.Length == 0 ? i.Body : i.Body.Length == 0 ? i.Title : $"{i.Title}\n{i.Body}"));

        protected static bool Urgent(IReadOnlyList<Item> items) => items.Any(i => i.Kind is Error or Drop);
    }

    private sealed class NtfyTarget : Target
    {
        private readonly string _url;

        public NtfyTarget(Notifier owner, string url) : base(owner, "NOTIFY_NTFY_EVENTS") => _url = url.TrimEnd('/');

        public override string Name => "ntfy";

        public override async Task SendAsync(IReadOnlyList<Item> items)
        {
            // Published as JSON to the server root: no header encoding for
            // titles with non-ASCII item names.
            int slash = _url.LastIndexOf('/');
            string server = _url[..slash], topic = _url[(slash + 1)..];
            int priority = Urgent(items) ? 4 : items.All(i => i.Kind is Start or Ping) ? 2 : 3;
            string[] tags = items.Any(i => i.Kind == Error) ? ["warning"]
                : items.Any(i => i.Kind == Drop) ? ["tada"]
                : items.Any(i => i.Kind == Stop) ? ["stop_sign"] : [];
            string body = Body(items);
            string json = JsonSerializer.Serialize(new
            {
                topic,
                title = Title(items),
                message = body.Length > 0 ? body : Title(items),
                priority,
                tags,
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, server) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (Owner.Setting("NOTIFY_NTFY_TOKEN") is { } token)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            else if (Owner.Setting("NOTIFY_NTFY_USER") is { } user)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{Owner.Setting("NOTIFY_NTFY_PASS")}")));
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string reply = await response.Content.ReadAsStringAsync();
                throw new Exception($"{(int)response.StatusCode} {reply.Trim()[..Math.Min(200, reply.Trim().Length)]}");
            }
        }
    }

    private sealed class MailTarget : Target
    {
        private readonly string _host;

        public MailTarget(Notifier owner, string host) : base(owner, "NOTIFY_SMTP_EVENTS") => _host = host;

        public override string Name => "email";

        public override async Task SendAsync(IReadOnlyList<Item> items)
        {
            string? user = Owner.Setting("NOTIFY_SMTP_USER");
            string from = Owner.Setting("NOTIFY_SMTP_FROM") ?? user ?? throw new Exception("NOTIFY_SMTP_FROM (or NOTIFY_SMTP_USER) is not set");
            string to = Owner.Setting("NOTIFY_SMTP_TO") ?? throw new Exception("NOTIFY_SMTP_TO is not set");
            // SmtpClient speaks STARTTLS (port 587), not TLS from the start (465).
            using var client = new SmtpClient(_host, int.TryParse(Owner.Setting("NOTIFY_SMTP_PORT"), out int port) ? port : 587)
            {
                EnableSsl = Owner.Setting("NOTIFY_SMTP_TLS")?.ToLowerInvariant() is not ("0" or "false" or "no" or "off"),
                Timeout = 20000,
            };
            if (user is not null)
                client.Credentials = new System.Net.NetworkCredential(user, Owner.Setting("NOTIFY_SMTP_PASS"));
            using var message = new MailMessage
            {
                From = new MailAddress(from),
                Subject = $"[VibeSkua] {Title(items)}",
                Body = Body(items),
                Priority = Urgent(items) ? MailPriority.High : MailPriority.Normal,
            };
            foreach (string address in to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                message.To.Add(address);
            await client.SendMailAsync(message);
        }
    }
}
