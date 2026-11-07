using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Newtonsoft.Json;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Messaging;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using Skua.Core.Threading;
using Skua.Core.Utils;
using System.Diagnostics;

namespace Skua.Core.Scripts;

public class ScriptInterface : IScriptInterface, IScriptInterfaceManager, IDisposable
{
    private CancellationTokenSource? ScriptInterfaceCTS;
    private readonly Thread ScriptInterfaceThread;
    private GameStateChannel? _stateChannel;
    private GameApiThreadSafeWrapper? _apiWrapper;
    private const int _timerDelay = 20;
    private readonly TimeLimiter _limit = new();
    private readonly ILogService _logger;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;

    public bool ShouldExit => Manager.ShouldExit;
    public Version Version { get; }

    public IScriptStatus Manager { get; }
    public IFlashUtil Flash { get; }
    public IScriptAuto Auto { get; }
    public IMessenger Messenger { get; }
    public IScriptBoost Boosts { get; }
    public IScriptBotStats Stats { get; }
    public IScriptSelfAuras Self { get; }
    public IScriptTargetAuras Target { get; }
    public IAuraMonitorService AuraMonitor { get; }
    public IUltraBossHelper UltraBossHelper { get; }
    public IScriptCombat Combat { get; }
    public IScriptKill Kill { get; }
    public IScriptHunt Hunt { get; }
    public IScriptDrop Drops { get; }
    public IScriptEvent Events { get; }
    public IScriptFaction Reputation { get; }
    public IScriptHouseInv House { get; }
    public IScriptInventory Inventory { get; }
    public IScriptTempInv TempInv { get; }
    public IScriptBank Bank { get; }
    public IScriptInventoryHelper InvHelper { get; }
    public IScriptLite Lite { get; }
    public IScriptOption Options { get; }
    public IScriptMap Map { get; }
    public IScriptMonster Monsters { get; }
    public IScriptPlayer Player { get; }
    public IScriptQuest Quests { get; }
    public IScriptSend Send { get; }
    public IScriptShop Shops { get; }
    public IScriptSkill Skills { get; }
    public IScriptWait Wait { get; }
    public IScriptServers Servers { get; }
    public IScriptHandlers Handlers { get; }
    public ICaptureProxy GameProxy { get; }
    public IScriptAccounts Accounts { get; }
    public IScriptOptionContainer? Config => Manager.Config;
    public Random Random { get; set; } = new Random();

    public ScriptInterface(
        ILogService logger,
        IScriptManager manager,
        IFlashUtil flash,
        IScriptHandlers handlers,
        IScriptServers server,
        IScriptBoost boosts,
        IScriptBotStats stats,
        IScriptSelfAuras scriptSelfAuras,
        IScriptTargetAuras scriptTargetAuras,
        IScriptCombat combat,
        IScriptDrop drops,
        IScriptEvent events,
        IScriptFaction rep,
        IScriptHouseInv house,
        IScriptInventory inventory,
        IScriptTempInv tempInv,
        IScriptBank bank,
        IScriptInventoryHelper invManager,
        IScriptLite lite,
        IScriptOption options,
        IScriptMap map,
        IScriptMonster monsters,
        IScriptPlayer player,
        IScriptQuest quests,
        IScriptSend send,
        IScriptShop shops,
        IScriptSkill skills,
        IScriptWait wait,
        IScriptKill kill,
        IScriptHunt hunt,
        ICaptureProxy gameProxy,
        IScriptAuto auto,
        IDialogService dialogService,
        ISettingsService settingsService,
        IAuraMonitorService auraMonitorService,
        IUltraBossHelper ultraBossHelper,
        IScriptAccounts accounts)
    {
        _logger = logger;
        Manager = manager;
        Boosts = boosts;
        Stats = stats;
        Self = scriptSelfAuras;
        Target = scriptTargetAuras;
        Combat = combat;
        Kill = kill;
        Hunt = hunt;
        GameProxy = gameProxy;
        Auto = auto;
        Messenger = StrongReferenceMessenger.Default;
        _dialogService = dialogService;
        Drops = drops;
        Events = events;
        Reputation = rep;
        House = house;
        Inventory = inventory;
        TempInv = tempInv;
        Bank = bank;
        InvHelper = invManager;
        Lite = lite;
        Options = options;
        Map = map;
        Monsters = monsters;
        Player = player;
        Quests = quests;
        Send = send;
        Shops = shops;
        Skills = skills;
        Wait = wait;
        Servers = server;
        Handlers = handlers;
        Flash = flash;
        AuraMonitor = auraMonitorService;
        UltraBossHelper = ultraBossHelper;
        Accounts = accounts;
        _settingsService = settingsService;

        string applicationVersion = settingsService.Get("ApplicationVersion", "0.0.0.0");
        int versionSuffix = applicationVersion.IndexOfAny(['-', '+']);
        if (versionSuffix >= 0)
            applicationVersion = applicationVersion[..versionSuffix];

        Version = Version.Parse(applicationVersion);

        _stateChannel = new GameStateChannel(_ => { }, 100);
        _stateChannel.Start();
        if (events is ScriptEvent scriptEvent)
            scriptEvent.SetStateChannel(_stateChannel);

        _apiWrapper = new GameApiThreadSafeWrapper(this, 1);
        _apiWrapper.Start();
        Flash.FlashCall += HandleFlashCall;

        ScriptInterfaceThread = new(() =>
        {
            ScriptInterfaceCTS = new();
            ScriptTimer(ScriptInterfaceCTS.Token);
            ScriptInterfaceCTS?.Dispose();
            ScriptInterfaceCTS = null;
        })
        {
            Name = "ScriptInterface",
            IsBackground = true
        };

        IScriptInterface.Instance = this;
    }

    public Task Schedule(int delay, Func<IScriptInterface, Task> function)
    {
        return Task.Run(async () => { await Task.Delay(delay); await function(this); });
    }

    public Task Schedule(int delay, Action<IScriptInterface> action)
    {
        return Task.Run(async () => { await Task.Delay(delay); action(this); });
    }

    public void SellJunk()
    {
        IJunkService? junkService = Ioc.Default.GetService<IJunkService>();
        junkService?.SellAllJunk();
    }

    public void Log(string message)
    {
        CheckScriptTermination();
        _logger.ScriptLog(message);
    }

    public void Sleep(int ms)
    {
        CheckScriptTermination();

        // For longer sleeps, break them up to check for cancellation more frequently
        if (ms > 1000)
        {
            int remaining = ms;
            while (remaining > 0)
            {
                CheckScriptTermination();
                int chunk = Math.Min(500, remaining);
                Thread.Sleep(chunk);
                remaining -= chunk;
            }
        }
        else
        {
            Thread.Sleep(ms);
        }
    }

    public async Task SleepAsync(int ms)
    {
        CheckScriptTermination();
        await Task.Delay(ms);
    }

    private void CheckScriptTermination()
    {
        if (Thread.CurrentThread.Name == "Script Thread")
        {
            // A paused script can still be stopped: the pause wait ends on the script's cancellation.
            if (Manager is IScriptManager scriptManager)
            {
                try
                {
                    scriptManager.ScriptPauseEvent.Wait(scriptManager.ScriptCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (Manager.ShouldExit)
                throw new OperationCanceledException();
        }
    }

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo = false)
    {
        return _dialogService.ShowMessageBox(message, caption, yesAndNo);
    }

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        return _dialogService.ShowMessageBox(message, caption, buttons);
    }

    public void Initialize()
    {
        if (!ScriptInterfaceThread.IsAlive)
            ScriptInterfaceThread.Start();
    }

    public async Task StopTimerAsync()
    {
        ScriptInterfaceCTS?.Cancel();
        await Wait.ForTrueAsync(() => ScriptInterfaceCTS == null, 20);
    }

    public void Stop(bool runScriptStoppingEvent = true)
    {
        _ = Manager.StopScript(runScriptStoppingEvent);
    }

    public async Task StopAsync(bool runScriptStoppingEvent = true)
    {
        await Manager.StopScript(runScriptStoppingEvent);
    }

    private void ScriptTimer(CancellationToken token)
    {
        bool catching = false;
        int lastConnChange = 0;
        string lastConnDetail = "";

        Stopwatch sw = new();

        while (!token.IsCancellationRequested)
        {
            try
            {
                sw.Restart();

                // Everything below needs at most 4 answers a second; asking the game
                // on every 20 ms pass cost about 250 bridge round trips a second.
                _limit.LimitedRun("playing", 250, () =>
                {
                    if (!Flash.IsWorldLoaded || !Player.LoggedIn)
                    {
                        Watchdog();
                        return;
                    }
                    // A logout on purpose is over once we are back in game, so the flag is
                    // cleared on that transition only. Not on every tick: the game's Logout
                    // button sets it while still connected, and the server closes the
                    // connection a moment later; clearing it in between let NativeSession
                    // log back in a minute later (live test 2026-11-07).
                    if (!_wasInGame && Servers.IsIntentionalLogout)
                        Servers.IsIntentionalLogout = false;
                    _wasInGame = true;
                    _dropSeenAt = 0;
                    if (!Player.Alive)
                        return;

                    // Only read on relogin, to pick another server; it can't change while logged in.
                    _limit.LimitedRun("serverIP", 5000, () => Servers.LastIP = Player.ServerIP ?? Servers.LastIP);

                    if (Options.RestPackets && !Player.InCombat)
                        _limit.LimitedRun("rest", 1200, () => Send.Packet("%xt%zm%restRequest%1%%"));

                    if (!catching)
                    {
                        Flash.Call("catchPackets");
                        catching = true;
                    }

                    CheckOptions();
                });

                // Its timeouts are seconds long: 500 ms is soon enough, and saves
                // ~480 isNull calls a minute.
                _limit.LimitedRun("connDetail", 500, () => (lastConnChange, lastConnDetail) = CheckStuckonLoading(lastConnChange, lastConnDetail));
                _limit.LimitedRun("loginStuck", 500, CheckStuckOnLogin);

                if (Manager.ScriptRunning)
                    RunScriptHandlers();

                sw.Stop();
                Thread.Sleep(Math.Max(10, _timerDelay - (int)sw.Elapsed.TotalMilliseconds));
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Error in timer thread: {e.Message}");
            }
        }
    }

    // In game before, now not: a drop nothing else reported (upstream's relogin watchdog).
    private bool _wasInGame;
    private int _dropSeenAt;

    // Application Options > Relogin Watchdog. Not the default-value overload: it turns a saved
    // false into the default. Unsaved, the model default is true.
    private bool WatchdogEnabled => _settingsService.Get<bool>("ReloginWatchdog");

    private void Watchdog()
    {
        if (!_wasInGame)
            return;
        if (_waitForLogin || Servers.IsIntentionalLogout || !Options.AutoRelogin || !WatchdogEnabled)
        {
            _wasInGame = false;
            _dropSeenAt = 0;
            return;
        }
        // Unlike Flash, our player can go away (crash, Reload game, recycle): calls then answer
        // nothing and read as "not logged in". Only the game saying "false" counts, and only when
        // it still says so 2 s later; a starting player (pre-load / loaded) clears _wasInGame, as
        // its login is NativeSession's or the user's.
        if (Flash.Call("isLoggedIn") != "false")
            return;
        if (_dropSeenAt == 0)
        {
            _dropSeenAt = Environment.TickCount;
            return;
        }
        if (Environment.TickCount - _dropSeenAt < 2000)
            return;
        _wasInGame = false;
        _dropSeenAt = 0;
        Log("Unexpected disconnection detected by watchdog. Triggering auto re-login.");
        BeginLogout();
    }

    private void CheckOptions()
    {
        // One call for the lag killer, magnetize, infinite range, skip cutscenes
        // and the Lite untarget settings (skua.swf keepOptions): they were up to
        // six calls four times a second.
        bool playing = Player.Playing;
        Flash.Call("keepOptions", Options.LagKiller, playing && Options.Magnetise, playing && Options.InfiniteRange,
            playing && Options.SkipCutscenes, playing);

        if (!playing)
            return;

        if (Options.AggroMonsters)
            Flash.CallGameFunction("world.aggroAllMon");
        if (Options.AggroAllMonsters)
            Send.Packet($"%xt%zm%aggroMon%{Map.RoomID}%{string.Join("%", Monsters.MapMonsters.Select(m => m.MapID))}%");
        if (Options.WalkSpeed != 8)
            Player.WalkSpeed = Options.WalkSpeed;
    }

    /// <summary>
    /// Checks if the player is stuck in the loading screen.
    /// </summary>
    /// <param name="lastConnChange">Last time the loading message changed.</param>
    /// <param name="lastConnDetail">Last loading message.</param>
    /// <returns>The last loading message and its time</returns>
    private (int newTime, string newText) CheckStuckonLoading(int lastConnChange, string lastConnDetail)
    {
        string connDetail = Flash.IsNull("mcConnDetail.stage") ? "null" : Flash.GetGameObject("mcConnDetail.txtDetail.text", "null")!;
        if (connDetail == "null")
            return (Environment.TickCount, connDetail);
        string connDetailLower = connDetail.ToLowerInvariant();
        if ((connDetailLower.Contains("has been lost") || connDetailLower.Contains("restart") || connDetailLower.Contains("maintenance")) && !_waitForLogin)
        {
            if (!Servers.IsIntentionalLogout)
            {
                Log($"Connection state changed ({connDetail}). Triggering re-login.");
                BeginLogout();
            }
        }
        else if (Environment.TickCount - lastConnChange >= Options.LoadTimeout && connDetail == lastConnDetail && !_waitForLogin)
        {
            if (connDetail.Contains("loading map"))
            {
                Map.Join("battleon");
                Map.Reload();
                Handlers.RegisterOnce(500, b =>
                {
                    if (Flash.GetGameObject("mcConnDetail.txtDetail.text") == "loading map")
                    {
                        LogoutStuck();
                        return;
                    }
                    Map.Join(Map.LastMap);
                });
            }
            else
            {
                LogoutStuck();
            }
        }
        if (connDetail == lastConnDetail)
            return (lastConnChange, connDetail);
        return (Environment.TickCount, connDetail);
    }

    // Stuck loading: relog through auto relogin. A plain Logout() now counts as intentional.
    private void LogoutStuck()
    {
        if (Options.AutoRelogin)
            BeginLogout();
        else
            Servers.Logout();
    }

    private int _loginStuckTime;

    /// <summary>
    /// Checks if the player is stuck on the initial login / authentication screen.
    /// </summary>
    private void CheckStuckOnLogin()
    {
        if (Player.Playing || Flash.IsWorldLoaded || _waitForLogin)
        {
            _loginStuckTime = 0;
            return;
        }

        bool loginVisible = Flash.GetGameObject<bool>("mcLogin.visible", false);
        if (!loginVisible)
        {
            _loginStuckTime = 0;
            return;
        }

        bool serverListExists = !Flash.IsNull("mcLogin.sl.iList");
        int serverEntries = Flash.GetGameObject<int>("mcLogin.sl.iList.numChildren", 0);
        if (serverListExists && serverEntries > 0)
        {
            _loginStuckTime = 0;
            return;
        }

        string strStatus = Flash.GetGameObject<string>("mcLogin.strStatus.text", "") ?? "";
        string txtStatus = Flash.GetGameObject<string>("mcLogin.txtStatus.text", "") ?? "";
        string txtDetail = Flash.GetGameObject<string>("mcLogin.txtDetail.text", "") ?? "";
        string combinedStatus = $"{strStatus} {txtStatus} {txtDetail}".ToLowerInvariant();

        bool isAuthenticatingOrError = combinedStatus.Contains("authenticating") ||
                                       combinedStatus.Contains("loading") ||
                                       combinedStatus.Contains("connecting") ||
                                       combinedStatus.Contains("account info") ||
                                       combinedStatus.Contains("error") ||
                                       combinedStatus.Contains("failed") ||
                                       combinedStatus.Contains("timeout") ||
                                       combinedStatus.Contains("lost") ||
                                       combinedStatus.Contains("offline");

        if (!isAuthenticatingOrError)
        {
            _loginStuckTime = 0;
            return;
        }

        if (_loginStuckTime == 0)
        {
            _loginStuckTime = Environment.TickCount;
            return;
        }

        if (Environment.TickCount - _loginStuckTime >= Options.LoginTimeout)
        {
            Log($"Login stuck on authentication ({combinedStatus.Trim()}) for over {Options.LoginTimeout}ms. Resetting login UI.");
            _loginStuckTime = 0;
            try
            {
                Flash.CallGameFunction("gotoAndPlay", "Login");
            }
            catch { }

            bool canRelog = Options.AutoRelogin || (!string.IsNullOrEmpty(Player.Username) && !string.IsNullOrEmpty(Player.Password));
            if (canRelog && !_waitForLogin && !Servers.IsIntentionalLogout)
            {
                Log("Triggering auto re-login from stuck authentication state.");
                BeginLogout();
            }
        }
    }

    /// <summary>
    /// Run all registered handlers, if the handler returns <see langword="false"/> it is removed from the list.
    /// </summary>
    private void RunScriptHandlers()
    {
        if (Handlers.CurrentHandlers.ToList().Count == 0)
            return;
        List<IHandler> rem = [];
        foreach (IHandler handler in Handlers.CurrentHandlers.ToList())
        {
            _limit.LimitedRun("handler_" + handler.Name, handler.Ticks * _timerDelay, () =>
            {
                if (!handler.Function(this))
                    rem.Add(handler);
            });
        }
        Handlers.Remove(rem);
    }

    private void HandleFlashCall(string name, object[]? args)
    {
        switch (name)
        {
            case "pre-load":
                _wasInGame = false;
                _dropSeenAt = 0;
                Schedule(1000, _ =>
                {
                    try
                    {
                        string sBG = _settingsService.Get("sBG", "Generic2.swf");
                        string? customBackgroundPath = _settingsService.Get<string?>("CustomBackgroundPath", null);
                        Flash.Call("setBackgroundValues", sBG, customBackgroundPath ?? "");
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not set background values: {ex.Message}");
                    }
                });
                break;

            case "loaded":
                _wasInGame = false;
                _dropSeenAt = 0;
                Initialize();
                break;

            case "connectionLost":
                Log("Game connection lost.");
                if (!_waitForLogin && !Servers.IsIntentionalLogout && Options.AutoRelogin && WatchdogEnabled)
                    BeginLogout();
                break;

            case "userIntentionalAction":
                if (args is { Length: > 0 } && args[0] is string action)
                {
                    switch (action)
                    {
                        case "logout":
                        case "changeServer":
                            Log($"{(action == "logout" ? "Logout" : "Change Server")} clicked in the game. Auto re-login disarmed.");
                            Servers.IsIntentionalLogout = true;
                            CancelRelogin();
                            if (Manager.ScriptRunning)
                                _ = Manager.StopScript();
                            break;

                        case "selectServer":
                            Servers.IsIntentionalLogout = false;
                            break;
                    }
                }
                break;

            case "openWebsite":
                if (args is { Length: > 0 } && args[0] is string url)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Log($"Failed to open website: {url}. Error: {ex.Message}");
                    }
                }
                break;

            case "debug":
                Trace.WriteLine(args[0]);
                break;

            case "pext":
                dynamic packet = JsonConvert.DeserializeObject<dynamic>((string)args[0])!;
                string type = packet["params"].type;
                dynamic data = packet["params"].dataObj;
                if (type is not null and "json")
                {
                    string cmd = data.cmd;
                    switch (cmd)
                    {
                        case "event":
                            string zone = data.args?["zoneSet"]!;
                            if (zone is not null)
                                Messenger.Send<RunToAreaMessage, int>(new(zone), (int)MessageChannels.GameEvents);
                            break;

                        case "moveToArea":
                            Stats.GetSpace();
                            Messenger.Send<MapChangedMessage, int>(new(Convert.ToString(data.strMapName)), (int)MessageChannels.GameEvents);
                            Map.FilePath = Convert.ToString(data.strMapFileName);
                            Map.LastMap = Convert.ToString(data.strMapName);
                            break;

                        case "ct":
                            dynamic p = data.p?[Player.Username.ToLower()]!;
                            if (p is not null && p.intHP == 0)
                            {
                                // Combat-state packets can repeat while the player remains dead.
                                if (!_playerDead)
                                {
                                    Stats.Deaths++;
                                    Messenger.Send<PlayerDeathMessage, int>((int)MessageChannels.GameEvents);
                                }

                                _playerDead = true;
                                break;
                            }

                            // Alive again only on HP above 0: a packet with only MP or state would
                            // otherwise let the next HP 0 packet count the same death twice.
                            if (p is not null && p.intHP != null && p.intHP > 0)
                                _playerDead = false;
                            dynamic anims = data.anims?[0]!;
                            if (anims is not null)
                            {
                                string msg = anims["msg"];
                                if (msg is not null && msg.Contains("prepares a counter attack!"))
                                {
                                    Messenger.Send<CounterAttackMessage, int>(new(false), (int)MessageChannels.GameEvents);
                                    break;
                                }
                            }
                            if (data.a is not null)
                            {
                                foreach (dynamic? a in data.a)
                                {
                                    if (a is null)
                                        continue;

                                    if (a.aura is null || (string)a.aura["nam"] is not "Counter Attack")
                                    {
                                        continue;
                                    }

                                    Messenger.Send<CounterAttackMessage, int>(new(true), (int)MessageChannels.GameEvents);
                                    break;
                                }
                            }
                            break;

                        case "sellItem":
                            Stats.GetSpace();
                            Messenger.Send<ItemSoldMessage, int>(new(
                                Convert.ToInt32(data.CharItemID),
                                Convert.ToInt32(data.iQty),
                                Convert.ToInt32(data.iQtyNow),
                                Convert.ToInt32(data.intAmount),
                                Convert.ToInt32(data.bCoins) == 1),
                                (int)MessageChannels.GameEvents);
                            break;

                        case "buyItem":
                            if (data.bitSuccess == 1)
                            {
                                Messenger.Send<ItemBoughtMessage, int>(new(Convert.ToInt32(data.CharItemID)), (int)MessageChannels.GameEvents);
                                Stats.GetSpace();
                            }
                            break;

                        case "dropItem":
                            string items = Convert.ToString(data["items"]);
                            InventoryItem drop = JsonConvert.DeserializeObject<Dictionary<string, InventoryItem>>(items)!.First().Value;
                            Messenger.Send<ItemDroppedMessage, int>(new(drop), (int)MessageChannels.GameEvents);
                            break;

                        case "addItems":
                            string addItems = Convert.ToString(data["items"]);
                            Dictionary<int, dynamic> addedItem = JsonConvert.DeserializeObject<Dictionary<int, dynamic>>(addItems)!;
                            int itemID = addedItem.Keys.First()!;
                            ItemBase invItem = Inventory.GetItem(itemID) ?? TempInv.GetItem(itemID)!;
                            Stats.GetSpace();
                            if (invItem is null)
                            {
                                invItem = Bank.GetItem(itemID)!;
                                Messenger.Send<ItemAddedToBankMessage, int>(new(invItem, invItem.Quantity), (int)MessageChannels.GameEvents);
                                break;
                            }
                            if (!invItem.Temp)
                                Stats.Drops++;
                            Messenger.Send<ItemDroppedMessage, int>(new(invItem, true, Convert.ToInt32(addedItem.Values.First().iQtyNow)), (int)MessageChannels.GameEvents);
                            break;

                        case "getDrop":
                            bool toBank = Convert.ToBoolean(data.bBank);
                            Messenger.Send<DropPickupResultMessage, int>(new(Convert.ToInt32(data.ItemID), data.bSuccess == 1), (int)MessageChannels.GameEvents);
                            if (data.bSuccess == 1)
                            {
                                Stats.Drops += (int)data.iQty;
                                Stats.GetSpace();
                            }
                            if (toBank)
                            {
                                ItemBase bankItem = Bank.GetItem(Convert.ToInt32(data.ItemID))!;
                                Messenger.Send<ItemAddedToBankMessage, int>(new(bankItem, Convert.ToInt32(data.iQtyNow)), (int)MessageChannels.GameEvents);
                            }
                            break;

                        case "addGoldExp":
                            if (data.typ == "m")
                            {
                                Stats.Kills++;
                                Messenger.Send<MonsterKilledMessage, int>(new(Convert.ToInt32(data.id)), (int)MessageChannels.GameEvents);
                            }
                            break;

                        case "ccqr":
                            if (data.bSuccess == 1)
                            {
                                Stats.QuestsCompleted++;
                                Messenger.Send<QuestTurninMessage, int>(new(Convert.ToInt32(data.QuestID)), (int)MessageChannels.GameEvents);
                            }
                            break;

                        case "loadBank":
                            Stats.GetSpace();
                            Messenger.Send<BankLoadedMessage, int>((int)MessageChannels.GameEvents);
                            break;

                        case "loadShop":
                            Stats.GetSpace();
                            Messenger.Send<ShopLoadedMessage, int>(new(new(Shops.ID, Shops.Name, Shops.Items)), (int)MessageChannels.GameEvents);
                            break;
                    }
                }
                else if (type is not null and "str")
                {
                    string cmd = data[0];
                    switch (cmd)
                    {
                        case "popup":
                            string b = Convert.ToString(packet);
                            Debug.WriteLine(b);
                            break;

                        case "uotls":
                            if (Player.Username == (string)data[2] && data[3] == "afk:true")
                                Messenger.Send<PlayerAFKMessage, int>((int)MessageChannels.GameEvents);
                            break;

                        case "loginResponse":
                            _playerDead = false;
                            Servers.IsIntentionalLogout = false;
                            Stats.GetSpace();
                            Options.CustomName = string.Empty;
                            Options.CustomGuild = string.Empty;
                            Messenger.Send<LoginMessage, int>(new(Convert.ToString(data[4])), (int)MessageChannels.GameEvents);
                            break;
                    }
                }
                Messenger.Send<ExtensionPacketMessage, int>(new(packet), (int)MessageChannels.GameEvents);
                break;

            case "packet":
                string[] parts = ((string)args[0]).Split('%', StringSplitOptions.RemoveEmptyEntries);
                switch (parts[2])
                {
                    case "moveToCell":
                        Messenger.Send<CellChangedMessage, int>(new(Map.Name, parts[4], parts[5]), (int)MessageChannels.GameEvents);
                        break;

                    case "buyItem":
                        Stats.GetSpace();
                        Messenger.Send<TryBuyItemMessage, int>(new(int.Parse(parts[5]), int.Parse(parts[4]), int.Parse(parts[6])), (int)MessageChannels.GameEvents);
                        break;

                    case "acceptQuest":
                        Stats.QuestsAccepted++;
                        Messenger.Send<QuestAcceptedMessage, int>(new(int.Parse(parts[4])), (int)MessageChannels.GameEvents);
                        break;

                    case "eLO":
                    case "eLO2":
                    case "eSL":
                    case "eSL2":
                        Log($"Exit packet ({parts[2]}): logout on purpose. Auto re-login disarmed.");
                        Servers.IsIntentionalLogout = true;
                        CancelRelogin();
                        if (Manager.ScriptRunning)
                            _ = Manager.StopScript();
                        break;

                    case "cmd":
                        if (parts.Length >= 5 && parts[4] == "logout" && !_waitForLogin)
                        {
                            Messenger.Send<LogoutMessage, int>((int)MessageChannels.GameEvents);
                            if (Servers.IsIntentionalLogout)
                                Log("Intentional logout confirmed. Auto re-login skipped.");
                            else
                                BeginLogout();
                        }
                        break;
                }
                Messenger.Send<PacketMessage, int>(new((string)args[0]), (int)MessageChannels.GameEvents);
                break;
        }
    }

    private Task? _reloginTask;
    private volatile bool _waitForLogin;
    private CancellationTokenSource? _reloginCTS;
    // 1 from the first OnLogout until its relogin has finished.
    private int _reloginClaimed;

    // Cancels a relogin that is waiting or running (the user logged out on purpose).
    public void CancelRelogin()
    {
        try { _reloginCTS?.Cancel(); } catch { }
    }

    private async void BeginLogout()
    {
        try
        {
            await OnLogout();
        }
        catch (Exception ex)
        {
            Log($"Auto re-login failed: {ex.Message}");
        }
    }

    private async Task OnLogout()
    {
        if (!Options.AutoRelogin || _waitForLogin || Servers.IsIntentionalLogout)
            return;

        // One relogin at a time. The loop calls this on every tick while the
        // "connection lost" screen shows, and stopping the script below takes
        // a while: _waitForLogin used to be set only after it, so each of
        // those calls started a relogin of its own (seen: 7 at once, resetting
        // each other's login screen until all of them gave up).
        if (Interlocked.CompareExchange(ref _reloginClaimed, 1, 0) != 0)
            return;
        _waitForLogin = true;
        // Made now, so a logout on purpose seen while the script stops cancels it: the game's
        // Logout button sends its logout packet (which lands here) before the click reaches us.
        CancellationTokenSource reloginCts = new();
        _reloginCTS = reloginCts;

        try
        {
            Log("Auto re-login triggered.");
            bool wasRunning = Manager.ScriptRunning;
            await Manager.StopScript();
            if (Servers.IsIntentionalLogout || reloginCts.IsCancellationRequested)
            {
                Log("Re-login cancelled: logged out on purpose.");
                _reloginCTS = null;
                reloginCts.Dispose();
                _waitForLogin = false;
                Interlocked.Exchange(ref _reloginClaimed, 0);
                return;
            }
            bool kicked = Player.Kicked;
            Messenger.Send<ReloginTriggeredMessage, int>(new(kicked), (int)MessageChannels.GameEvents);

            int delay = (Options.SafeRelogin && kicked) ? 70000 : Options.ReloginTryDelay;
            Relogin(delay, wasRunning, reloginCts);
        }
        catch
        {
            _reloginCTS = null;
            reloginCts.Dispose();
            _waitForLogin = false;
            Interlocked.Exchange(ref _reloginClaimed, 0);
            throw;
        }
    }

    private void Relogin(int delay, bool startScript, CancellationTokenSource reloginCts)
    {
        Servers.DisconnectForRelogin();
        Log($"Waiting {delay}ms for re-login.");
        _reloginTask = Schedule(delay, async _ =>
        {
            try
            {
                reloginCts.Token.ThrowIfCancellationRequested();
                Stats.Relogins++;
                bool relogged = await Servers.EnsureRelogin(reloginCts.Token);
                if (startScript && relogged)
                    await Ioc.Default.GetService<IScriptManager>()!.StartScript();
                else if (startScript && !relogged)
                    Log("Skipping script restart because re-login did not succeed.");
                Log($"Re-login was {(relogged ? "successful" : "cancelled or unsuccessful")}.");
            }
            catch (OperationCanceledException)
            {
                Log("Re-login cancelled.");
            }
            catch (Exception e)
            {
                Log($"Re-login failed: {e.Message}");
            }
            finally
            {
                // Always, or auto re-login would stay off for good.
                if (ReferenceEquals(_reloginCTS, reloginCts))
                    _reloginCTS = null;
                reloginCts.Dispose();
                _reloginTask = null;
                _waitForLogin = false;
                Interlocked.Exchange(ref _reloginClaimed, 0);
            }
        });
    }

    private bool _disposed = false;
    // Set by a combat packet with HP 0, cleared when HP is back above 0 or on login (deaths are counted once).
    private bool _playerDead;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Unsubscribe from Flash events
                Flash.FlashCall -= HandleFlashCall;

                // Cancel and clean up the script interface thread
                ScriptInterfaceCTS?.Cancel();
                if (ScriptInterfaceThread is { IsAlive: true })
                {
                    // Give the thread time to finish gracefully
                    if (!ScriptInterfaceThread.Join(1000))
                    {
                        // Force abort if it doesn't finish in time
                        try { ScriptInterfaceThread.Interrupt(); } catch {/* ignored */ }
                    }
                }
                ScriptInterfaceCTS?.Dispose();
                ScriptInterfaceCTS = null;
                _apiWrapper?.StopAsync().GetAwaiter().GetResult();
                _apiWrapper = null;

                if (_stateChannel != null)
                {
                    _stateChannel.StopAsync().GetAwaiter().GetResult();
                    _stateChannel.Dispose();
                    _stateChannel = null;
                }

                _reloginCTS?.Cancel();
                _reloginCTS?.Dispose();
                _reloginCTS = null;
                _reloginTask = null;

                // Clear the static instance reference
                if (IScriptInterface.Instance == this)
                {
                    IScriptInterface.Instance = null;
                }
            }

            _disposed = true;
        }
    }

    ~ScriptInterface()
    {
        Dispose(false);
    }
}