using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;

using Newtonsoft.Json;

using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AutoReactFFXIV;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private const string CommandName = "/autoreact";

    private Configuration config;
    private bool showWindow = false;

    // Detection and response
    private SpiteDetector spiteDetector;
    private GuardExecutor guardExecutor;
    private ExecuteEngine executeEngine;

    // Resolved action IDs
    private uint marksmanSpiteActionId = 0;
    private uint guardActionId = 0;
    private uint braveryActionId = 0;
    private uint guardStatusId = 0;

    // ClassJob ID for Machinist
    private const uint MchClassJobId = 31;


    // -----------------------------------------------------------------------
    // Defend tab state (updated each frame poll)
    // -----------------------------------------------------------------------
    private bool mchTargetingUs = false;
    private string mchTargetingName = string.Empty;
    private DateTime lastPoll = DateTime.MinValue;
    private DateTime lastSightPoll = DateTime.MinValue;
    private const double PollIntervalMs = 100;

    private string lastDefendEvent = "None";
    private DateTime lastDefendEventTime = DateTime.MinValue;

    private bool guardReady = true;
    private float guardCooldownRemaining = 0f;

    // -----------------------------------------------------------------------
    // Execute tab state (updated each frame poll)
    // -----------------------------------------------------------------------
    private IPlayerCharacter? executeHost = null;
    private IGameObject? executeHostTarget = null;
    private string executeHostTargetName = string.Empty;
    private bool executeInRange = false;
    private bool executeSpiteReady = false;
    private bool executeBraveryReady = false;
    private bool executeIsLocalMch = false;
    private string executeLastMessage = string.Empty;
    private DateTime executeLastMessageTime = DateTime.MinValue;

    // Deferred fire: waiting for animation lock to clear before using Bravery+Spite
    private ulong pendingFireGameObjectId = 0;
    private DateTime pendingFireExpiry = DateTime.MinValue;
    private const double PendingFireTimeoutMs = 1200.0;

    // -----------------------------------------------------------------------
    // SIGHT tab state (updated each frame poll)
    // -----------------------------------------------------------------------
    private List<EnemySightData> sightEnemies = new List<EnemySightData>();
    private uint limitBreakReadyStatusId = 0;
    private List<Vector3> partyMchLbReadyPositions = new List<Vector3>();

    // Black circle: players who used Guard recently (entityId → time of use)
    private Dictionary<uint, DateTime> guardingEnemies = new Dictionary<uint, DateTime>();
    private const double GuardBlackCircleWindowMs = 5000.0;

    // SIGHT lerp: previous positions for smooth interpolation between polls
    private Dictionary<uint, Vector3> sightPrevPositions = new Dictionary<uint, Vector3>();
    private DateTime lastSightPollTime = DateTime.MinValue;

    // UseAction hook: intercepts Marksman's Spite when LB suppression is active
    private unsafe delegate bool UseActionDelegate(ActionManager* mgr, ActionType type, uint actionId, long targetId, uint a4, uint a5, uint a6, void* a7);
    private Hook<UseActionDelegate>? useActionHook;

    // -----------------------------------------------------------------------
    // Persistent stats (Games + Kills counters, saved to separate JSON file)
    // -----------------------------------------------------------------------
    private PersistentStats stats = new PersistentStats();
    private string statsFilePath = string.Empty;

    // PvP territory IDs (built at startup from ContentFinderCondition sheet)
    private HashSet<uint> pvpTerritoryTypeIds = new HashSet<uint>();
    private bool currentlyInPvpInstance = false;

    // Per-game tracking (cleared on zone change)
    private HashSet<string> seenThisGame = new HashSet<string>();

    // Recent Spite fire targets (name → time fired) for kill detection
    private Dictionary<string, DateTime> spitedRecently = new Dictionary<string, DateTime>();
    private const double SpiteKillWindowMs = 5000.0;

    // Last observed HP of enemy players for death detection
    private Dictionary<string, uint> lastSeenHp = new Dictionary<string, uint>();

    // -----------------------------------------------------------------------
    // Share tab state
    // -----------------------------------------------------------------------
    private const string SyncTag      = "[AR]";
    private const string SyncTagChunk = "[AR:";
    private const int    MaxChatContent = 480;

    private Dictionary<string, (int Total, Dictionary<int, string> Parts)> syncChunkBuffer
        = new Dictionary<string, (int, Dictionary<int, string>)>();

    private string syncAddBuf         = string.Empty;
    private string shareStatusMsg     = string.Empty;
    private bool   shareStatusIsError = false;

    // Debug: last [AR] message seen (regardless of trusted list)
    private string lastSyncRawSender   = string.Empty;
    private string lastSyncNormSender  = string.Empty;
    private bool   lastSyncWasTrusted  = false;

    private static readonly string[] SyncChannelDisplayNames =
    {
        "Party",
        "LS 1", "LS 2", "LS 3", "LS 4", "LS 5", "LS 6", "LS 7", "LS 8",
        "CWLS 1", "CWLS 2", "CWLS 3", "CWLS 4", "CWLS 5", "CWLS 6", "CWLS 7", "CWLS 8",
        "FC",
    };

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ResolveActionIds();

        unsafe
        {
            useActionHook = GameInteropProvider.HookFromAddress<UseActionDelegate>(
                (nint)ActionManager.MemberFunctionPointers.UseAction,
                UseActionDetour);
            useActionHook.Enable();
        }

        guardExecutor = new GuardExecutor(Log);
        guardExecutor.GuardActionId = guardActionId;

        executeEngine = new ExecuteEngine(Log, ObjectTable, TargetManager);
        executeEngine.MarksmanSpiteActionId = marksmanSpiteActionId;
        executeEngine.BraveryActionId = braveryActionId;

        spiteDetector = new SpiteDetector(GameInteropProvider, Log, ObjectTable);
        spiteDetector.MarksmanSpiteActionId = marksmanSpiteActionId;
        spiteDetector.BraveryActionId = braveryActionId;
        spiteDetector.GuardActionId = guardActionId;
        spiteDetector.OnSpiteDetected += OnSpiteDetected;
        spiteDetector.OnHostFiredSpite += OnHostFiredSpite;
        spiteDetector.OnHostUsedBravery += OnHostUsedBravery;
        spiteDetector.OnAnyPlayerUsedGuard += OnAnyPlayerUsedGuardHandler;
        spiteDetector.OnSpiteHitTargets += OnSpiteHitTargetsHandler;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Auto React commands:\n" +
                "  /autoreact          - Toggle the window\n" +
                "  /autoreact on       - Enable auto-guard\n" +
                "  /autoreact off      - Disable auto-guard\n" +
                "  /autoreact status   - Show current status",
        });

        ChatGui.ChatMessage += OnChatMessage;

        // Load persistent stats from separate JSON file
        statsFilePath = Path.Combine(PluginInterface.ConfigDirectory.FullName, "persistent_stats.json");
        LoadStats();

        // Detect PvP zone transitions for Games counter
        ClientState.TerritoryChanged += OnTerritoryChanged;
        currentlyInPvpInstance = pvpTerritoryTypeIds.Contains(ClientState.TerritoryType);

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OnOpenMainUi;
        Framework.Update += OnFrameworkUpdate;

        Log.Info($"AutoReactFFXIV loaded. Spite ID={marksmanSpiteActionId}, Guard ID={guardActionId}, Bravery ID={braveryActionId}");
    }

    public void Dispose()
    {
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenMainUi;
        ChatGui.ChatMessage -= OnChatMessage;
        CommandManager.RemoveHandler(CommandName);

        useActionHook?.Disable();
        useActionHook?.Dispose();

        spiteDetector.OnSpiteDetected -= OnSpiteDetected;
        spiteDetector.OnHostFiredSpite -= OnHostFiredSpite;
        spiteDetector.OnHostUsedBravery -= OnHostUsedBravery;
        spiteDetector.OnAnyPlayerUsedGuard -= OnAnyPlayerUsedGuardHandler;
        spiteDetector.OnSpiteHitTargets -= OnSpiteHitTargetsHandler;
        spiteDetector.Dispose();

        PluginInterface.SavePluginConfig(config);
        Log.Info("AutoReactFFXIV unloaded.");
    }

    // -----------------------------------------------------------------------
    // Startup
    // -----------------------------------------------------------------------

    private void ResolveActionIds()
    {
        var actionSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (actionSheet == null)
        {
            Log.Error("Failed to load Action sheet from Lumina.");
            return;
        }

        // First pass: PvP actions (Spite, Guard, Bravery may all be PvP)
        foreach (var action in actionSheet)
        {
            if (!action.IsPvP)
                continue;

            var name = action.Name.ToString();

            if (name == "Marksman's Spite" && marksmanSpiteActionId == 0)
            {
                marksmanSpiteActionId = action.RowId;
                Log.Info($"Resolved Marksman's Spite PvP: ID={action.RowId}");
            }

            if (name == "Guard" && guardActionId == 0)
            {
                guardActionId = action.RowId;
                Log.Info($"Resolved Guard PvP: ID={action.RowId}");
            }

            if (name == "Bravery" && braveryActionId == 0)
            {
                braveryActionId = action.RowId;
                Log.Info($"Resolved Bravery PvP: ID={action.RowId}");
            }

            if (marksmanSpiteActionId != 0 && guardActionId != 0 && braveryActionId != 0)
                break;
        }

        // Second pass: if Bravery wasn't a PvP action, check all actions
        if (braveryActionId == 0)
        {
            foreach (var action in actionSheet)
            {
                if (action.Name.ToString() == "Bravery")
                {
                    braveryActionId = action.RowId;
                    Log.Info($"Resolved Bravery (non-PvP): ID={action.RowId}");
                    break;
                }
            }
        }

        if (marksmanSpiteActionId == 0) Log.Warning("Could not resolve Marksman's Spite PvP action ID!");
        if (guardActionId == 0) Log.Warning("Could not resolve Guard PvP action ID!");
        if (braveryActionId == 0) Log.Warning("Could not resolve Bravery action ID — will fire Spite without it.");

        // Resolve Guard status ID (the buff applied when Guard is used)
        var statusSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
        if (statusSheet != null)
        {
            foreach (var status in statusSheet)
            {
                if (status.Name.ToString() == "Guard" && guardStatusId == 0)
                {
                    guardStatusId = status.RowId;
                    Log.Info($"Resolved Guard status ID: {status.RowId}");
                }
            }
        }
        if (guardStatusId == 0) Log.Warning("Could not resolve Guard status ID — SIGHT RED detection disabled.");

        // Resolve PvP Limit Break ready status (shown when a player's LB gauge is full)
        // Search for all statuses containing "Limit Break" and log candidates
        if (statusSheet != null)
        {
            foreach (var status in statusSheet)
            {
                var sName = status.Name.ToString();
                if (sName.Contains("Limit Break", System.StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"LB status candidate: '{sName}' ID={status.RowId}");
                    if (limitBreakReadyStatusId == 0)
                        limitBreakReadyStatusId = status.RowId;
                }
            }
        }
        if (limitBreakReadyStatusId == 0) Log.Warning("Could not resolve LB ready status ID — party MCH LB stars disabled.");
        else Log.Info($"Using LB ready status ID={limitBreakReadyStatusId}.");

        // Build set of PvP territory IDs from ContentFinderCondition sheet
        try
        {
            var cfcSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
            if (cfcSheet != null)
            {
                foreach (var cfc in cfcSheet)
                {
                    if (cfc.PvP && cfc.TerritoryType.IsValid && cfc.TerritoryType.RowId != 0)
                        pvpTerritoryTypeIds.Add(cfc.TerritoryType.RowId);
                }
                Log.Info($"Resolved {pvpTerritoryTypeIds.Count} PvP territory type IDs.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not resolve PvP territory IDs: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------

    private void OnSpiteDetected()
    {
        config.TimesDetected++;

        lastDefendEvent = "Marksman's Spite DETECTED!";
        lastDefendEventTime = DateTime.Now;

        if (!config.Enabled)
        {
            Log.Info("Spite detected but plugin is disabled.");
            return;
        }

        ChatGui.Print("[AutoReact] Marksman's Spite incoming!");

        if (!config.AutoGuard)
        {
            Log.Info("Spite detected but auto-guard is disabled.");
            return;
        }

        if (guardExecutor.UseGuard())
        {
            config.TimesGuarded++;
            lastDefendEvent = "Guard ACTIVATED!";
            ChatGui.Print("[AutoReact] Guard activated!");
            Log.Info("Auto-Guard successful.");
        }
        else
        {
            if (config.AlertOnCooldown)
                ChatGui.Print("[AutoReact] Spite incoming but Guard is on cooldown!");
            lastDefendEvent = "Guard ON COOLDOWN!";
            Log.Warning("Spite detected but Guard is on cooldown.");
        }

        PluginInterface.SavePluginConfig(config);
    }

    private void OnHostFiredSpite(uint targetEntityId)
    {
        // Look up target first — needed for victim tracking regardless of auto mode
        IGameObject? target = null;
        foreach (var obj in ObjectTable)
        {
            if (obj != null && obj.EntityId == targetEntityId) { target = obj; break; }
        }

        if (!config.ExecuteAutoMode)
            return;

        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null) return;
        if (localPlayer.ClassJob.RowId != MchClassJobId) return;
        if (targetEntityId == localPlayer.EntityId) return;

        if (target == null)
        {
            Log.Warning($"OnHostFiredSpite: target EntityId {targetEntityId} not found.");
            return;
        }

        if (!executeEngine.IsInRange(localPlayer, target))
        {
            Log.Info("Auto Execute: target out of range, skipping.");
            return;
        }

        if (!executeEngine.IsSpiteReady())
        {
            Log.Info("Auto Execute: Marksman's Spite not available, skipping.");
            return;
        }

        if (IsLbBlockedBySight())
        {
            Log.Info("Auto Execute: LB blocked by SIGHT.");
            return;
        }

        TriggerFire(target, "Auto: ");
    }

    private void OnHostUsedBravery()
    {
        if (!config.SyncBraveryWithHost) return;

        if (executeEngine.UseBraveryIfAvailable())
        {
            SetExecuteMessage("Synced Bravery with host");
            Log.Info("Synced Bravery with host.");
        }
    }

    private void OnTerritoryChanged(ushort newTerritoryTypeId)
    {
        // Commit the "seen this game" window — clear so next game starts fresh
        if (seenThisGame.Count > 0)
            Log.Info($"Zone change: committing {seenThisGame.Count} player game entries, clearing session.");
        seenThisGame.Clear();
        lastSeenHp.Clear();
        spitedRecently.Clear();

        currentlyInPvpInstance = pvpTerritoryTypeIds.Contains(newTerritoryTypeId);
        Log.Info($"Territory changed to {newTerritoryTypeId}, isPvP={currentlyInPvpInstance}");
    }

    private void OnAnyPlayerUsedGuardHandler(uint entityId)
    {
        // Track Guard use time for any player — filtered to enemies at draw time
        guardingEnemies[entityId] = DateTime.Now;
    }

    private void OnSpiteHitTargetsHandler(uint casterEntityId, uint[] targetEntityIds)
    {
        // Only track victims/AVOID when the local player fired the Spite
        if (ObjectTable.LocalPlayer == null || casterEntityId != ObjectTable.LocalPlayer.EntityId)
            return;

        var cutoff = DateTime.Now.AddMilliseconds(-GuardBlackCircleWindowMs);
        foreach (var targetId in targetEntityIds)
        {
            foreach (var obj in ObjectTable)
            {
                if (obj == null || obj.EntityId != targetId || obj is not IPlayerCharacter pc) continue;

                var name = pc.Name.ToString();

                // Skip allies: party members, trusted players, and local player
                bool isAlly = name == (ObjectTable.LocalPlayer?.Name.ToString() ?? string.Empty)
                    || config.TrustedPlayers.Contains(name);
                if (!isAlly)
                {
                    for (int pi = 0; pi < PartyList.Length; pi++)
                    {
                        if (PartyList[pi]?.Name.ToString() == name) { isAlly = true; break; }
                    }
                }
                if (isAlly) continue;

                // Always add as victim and mark spited time for kill tracking
                AddVictim(name);
                spitedRecently[name] = DateTime.Now;

                // If they were guarding in the window, add to AVOID
                if (guardingEnemies.TryGetValue(targetId, out var guardTime) && guardTime >= cutoff)
                    AddAvoid(name);

                break;
            }
        }

        if (config.AutoSync) BroadcastSync();
    }

    // -----------------------------------------------------------------------
    // Execute helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Central fire trigger. Handles AutoBravery logic:
    ///   - Bravery available now → Bravery + Spite
    ///   - Bravery off CD but animation-locked → set pending fire (wait up to 1.2s)
    ///   - Bravery on CD or disabled → Spite only
    /// </summary>
    private void TriggerFire(IGameObject target, string prefix = "")
    {
        if (IsLbBlockedBySight())
        {
            SetExecuteMessage(prefix + "LB blocked by SIGHT (Guard/buff on target)");
            Log.Info("TriggerFire: blocked by SIGHT.");
            return;
        }

        if (config.AutoBravery && braveryActionId != 0)
        {
            if (executeEngine.IsBraveryReady())
            {
                // Bravery available right now — use it then fire Spite
                var msg = executeEngine.Fire(target);
                SetExecuteMessage(prefix + msg);
            }
            else if (executeEngine.IsBraveryOffCooldown())
            {
                // Off cooldown but animation-locked — queue a deferred fire
                SetPendingFire(target.GameObjectId);
                SetExecuteMessage(prefix + "Waiting for Bravery...");
                Log.Info("TriggerFire: deferred — waiting for animation lock to clear.");
            }
            else
            {
                // Bravery on recast cooldown — fire Spite immediately without it
                var ok = executeEngine.FireSpiteOnly(target);
                SetExecuteMessage(prefix + (ok ? "Spite FIRED (Bravery on CD)" : "Spite failed"));
            }
        }
        else
        {
            // AutoBravery disabled — just fire Spite
            var ok = executeEngine.FireSpiteOnly(target);
            SetExecuteMessage(prefix + (ok ? "Spite FIRED!" : "Spite failed"));
        }
    }

    private void SetPendingFire(ulong gameObjectId)
    {
        pendingFireGameObjectId = gameObjectId;
        pendingFireExpiry = DateTime.Now.AddMilliseconds(PendingFireTimeoutMs);
    }

    private void ClearPendingFire()
    {
        pendingFireGameObjectId = 0;
        pendingFireExpiry = DateTime.MinValue;
    }

    private void SetExecuteMessage(string msg)
    {
        executeLastMessage = msg;
        executeLastMessageTime = DateTime.Now;
        Log.Info($"Execute: {msg}");
    }

    private void OnCommand(string command, string args)
    {
        var sub = args.Trim().ToLower();

        switch (sub)
        {
            case "":
                showWindow = !showWindow;
                break;

            case "on":
                config.Enabled = true;
                PluginInterface.SavePluginConfig(config);
                ChatGui.Print("[AutoReact] Enabled.");
                break;

            case "off":
                config.Enabled = false;
                PluginInterface.SavePluginConfig(config);
                ChatGui.Print("[AutoReact] Disabled.");
                break;

            case "status":
                ChatGui.Print($"[AutoReact] {(config.Enabled ? "ENABLED" : "DISABLED")}");
                ChatGui.Print($"[AutoReact] Auto-Guard: {(config.AutoGuard ? "ON" : "OFF")}");
                ChatGui.Print($"[AutoReact] Spite ID: {marksmanSpiteActionId}, Guard ID: {guardActionId}, Bravery ID: {(braveryActionId != 0 ? braveryActionId.ToString() : "NOT FOUND")}");
                ChatGui.Print($"[AutoReact] Times detected: {config.TimesDetected}");
                ChatGui.Print($"[AutoReact] Times guarded: {config.TimesGuarded}");
                if (mchTargetingUs)
                    ChatGui.Print($"[AutoReact] WARNING: MCH '{mchTargetingName}' is targeting you!");
                break;

            default:
                ChatGui.Print("[AutoReact] Unknown command. Try /autoreact status");
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Framework update (100ms poll)
    // -----------------------------------------------------------------------

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.Now;

        // SIGHT collection runs on its own configurable interval
        if (config.SightEnabled
            && (now - lastSightPoll).TotalMilliseconds >= Math.Max(1, config.SightPollIntervalMs))
        {
            lastSightPoll = now;
            UpdateSightEnemies();
        }

        if ((now - lastPoll).TotalMilliseconds < PollIntervalMs)
            return;
        lastPoll = now;

        // --- Guard cooldown for Defend tab ---
        guardReady = guardExecutor.IsGuardReady();
        guardCooldownRemaining = guardExecutor.GetGuardCooldownRemaining();

        var localPlayer = ObjectTable.LocalPlayer;

        // --- MCH early warning for Defend tab ---
        if (config.Enabled && localPlayer != null)
        {
            var wasMchTargeting = mchTargetingUs;
            mchTargetingUs = false;
            mchTargetingName = string.Empty;

            foreach (var obj in ObjectTable)
            {
                if (obj == null) continue;
                if (obj.GameObjectId == localPlayer.GameObjectId) continue;
                if (obj is not IPlayerCharacter pc) continue;
                if (obj.TargetObjectId != localPlayer.GameObjectId) continue;

                if (pc.ClassJob.RowId == MchClassJobId)
                {
                    mchTargetingUs = true;
                    mchTargetingName = pc.Name.ToString();
                    break;
                }
            }

            if (config.WarnOnMchTarget && mchTargetingUs && !wasMchTargeting)
                ChatGui.Print($"[AutoReact] MCH '{mchTargetingName}' is targeting you!");
        }

        // --- Execute tab state ---
        executeIsLocalMch = localPlayer != null && localPlayer.ClassJob.RowId == MchClassJobId;

        if (!string.IsNullOrEmpty(config.HostPlayerName) && localPlayer != null)
        {
            executeHost = null;
            foreach (var obj in ObjectTable)
            {
                if (obj is IPlayerCharacter pc && pc.Name.ToString() == config.HostPlayerName)
                {
                    executeHost = pc;
                    break;
                }
            }

            if (executeHost != null)
            {
                // Keep the host entity ID synced for the auto-mode hook
                spiteDetector.HostEntityId = executeHost.EntityId;

                // Find host's current target
                executeHostTarget = null;
                executeHostTargetName = string.Empty;
                var hostTargetId = executeHost.TargetObjectId;
                if (hostTargetId != 0)
                {
                    foreach (var obj in ObjectTable)
                    {
                        if (obj != null && obj.GameObjectId == hostTargetId)
                        {
                            executeHostTarget = obj;
                            executeHostTargetName = obj.Name.ToString();
                            break;
                        }
                    }
                }
            }
            else
            {
                spiteDetector.HostEntityId = 0;
                executeHostTarget = null;
                executeHostTargetName = string.Empty;
            }
        }
        else
        {
            spiteDetector.HostEntityId = 0;
            executeHost = null;
            executeHostTarget = null;
            executeHostTargetName = string.Empty;
        }

        executeInRange = localPlayer != null && executeHostTarget != null
            && executeEngine.IsInRange(localPlayer, executeHostTarget);
        executeSpiteReady = executeEngine.IsSpiteReady();
        executeBraveryReady = executeEngine.IsBraveryReady();

        // --- Persistent stats: Games + Kill tracking ---
        if (config.SightEnabled && sightEnemies.Count > 0)
        {
            // Once enemies are visible, confirm we're in a PvP instance
            if (!currentlyInPvpInstance && pvpTerritoryTypeIds.Contains(ClientState.TerritoryType))
                currentlyInPvpInstance = true;

            var spiteKillCutoff = now.AddMilliseconds(-SpiteKillWindowMs);

            foreach (var enemy in sightEnemies)
            {
                bool isTracked = config.Victims.Exists(v => v.Name == enemy.Name)
                              || config.AvoidList.Contains(enemy.Name);

                // Games counter: once per game per tracked player
                if (isTracked && currentlyInPvpInstance && !seenThisGame.Contains(enemy.Name))
                {
                    seenThisGame.Add(enemy.Name);
                    var ps = GetOrCreatePlayerStats(enemy.Name);
                    ps.GamesEncountered++;
                    SaveStats();
                    Log.Info($"Stats: {enemy.Name} games={ps.GamesEncountered}");
                }

                // Kill counter: HP just dropped to 0 after we Spite'd them
                if (lastSeenHp.TryGetValue(enemy.Name, out var prevHp)
                    && prevHp > 0 && enemy.CurrentHp == 0)
                {
                    if (spitedRecently.TryGetValue(enemy.Name, out var spiteTime)
                        && spiteTime > spiteKillCutoff)
                    {
                        var victimEntry = config.Victims.Find(v => v.Name == enemy.Name);
                        if (victimEntry != null)
                        {
                            victimEntry.Kills++;
                            PluginInterface.SavePluginConfig(config);
                            var ps = GetOrCreatePlayerStats(enemy.Name);
                            ps.SpiteKills++;
                            SaveStats();
                            Log.Info($"Stats: {enemy.Name} kills={victimEntry.Kills} spiteKills={ps.SpiteKills}");
                            if (config.AutoSync) BroadcastSync();
                        }
                    }
                }

                lastSeenHp[enemy.Name] = enemy.CurrentHp;
            }
        }

        // --- Clean up stale black-circle Guard markers ---
        if (guardingEnemies.Count > 0)
        {
            var cutoff = now.AddMilliseconds(-GuardBlackCircleWindowMs);
            var stale = new List<uint>();
            foreach (var kvp in guardingEnemies)
                if (kvp.Value < cutoff) stale.Add(kvp.Key);
            foreach (var key in stale) guardingEnemies.Remove(key);
        }

        // --- Pending deferred fire (waiting for animation lock to clear) ---
        if (pendingFireGameObjectId != 0)
        {
            // Re-look up the target freshly — the stored reference may have gone stale
            IGameObject? pendingTarget = null;
            foreach (var obj in ObjectTable)
            {
                if (obj != null && obj.GameObjectId == pendingFireGameObjectId)
                { pendingTarget = obj; break; }
            }

            if (pendingTarget == null)
            {
                // Target left the scene — cancel
                Log.Info("PendingFire: target gone, cancelling.");
                ClearPendingFire();
            }
            else if (DateTime.Now >= pendingFireExpiry)
            {
                // Timed out — fire Spite without Bravery
                var ok = executeEngine.FireSpiteOnly(pendingTarget);
                SetExecuteMessage(ok ? "Spite FIRED (Bravery wait timeout)" : "Spite failed (timeout)");
                ClearPendingFire();
            }
            else if (executeEngine.IsBraveryReady())
            {
                // Bravery is now available — fire the full combo
                var msg = executeEngine.Fire(pendingTarget);
                SetExecuteMessage("Deferred: " + msg);
                ClearPendingFire();
            }
            // else: still waiting, check again next poll
        }
    }

    private void OnOpenMainUi()
    {
        showWindow = true;
    }

    // -----------------------------------------------------------------------
    // UI
    // -----------------------------------------------------------------------

    private void DrawUI()
    {
        // SIGHT overlay drawn regardless of window visibility
        if (config.SightEnabled)
            DrawSightOverlay();

        if (!showWindow)
            return;

        ImGui.SetNextWindowSize(new Vector2(420, 520), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Auto React", ref showWindow, ImGuiWindowFlags.None))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Auto React v0.5.7");
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "PvP Defend & Execute");
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.BeginTabBar("MainTabs"))
            {
                if (ImGui.BeginTabItem("Defend"))
                {
                    DrawDefendTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Execute"))
                {
                    DrawExecuteTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("SIGHT"))
                {
                    DrawSightTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Victims"))
                {
                    DrawVictimsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("AVOID"))
                {
                    DrawAvoidTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Share"))
                {
                    DrawShareTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Settings"))
                {
                    DrawSettingsTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        ImGui.End();
    }

    private void DrawDefendTab()
    {
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            PluginInterface.SavePluginConfig(config);
        }

        var autoGuard = config.AutoGuard;
        if (ImGui.Checkbox("Auto-Guard on Spite detection", ref autoGuard))
        {
            config.AutoGuard = autoGuard;
            PluginInterface.SavePluginConfig(config);
        }

        var warnMch = config.WarnOnMchTarget;
        if (ImGui.Checkbox("Warn when MCH targets you", ref warnMch))
        {
            config.WarnOnMchTarget = warnMch;
            PluginInterface.SavePluginConfig(config);
        }

        var alertCd = config.AlertOnCooldown;
        if (ImGui.Checkbox("Alert if Guard on cooldown", ref alertCd))
        {
            config.AlertOnCooldown = alertCd;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Status");
        ImGui.Indent();

        if (config.Enabled)
            ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "ACTIVE");
        else
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "DISABLED");

        if (mchTargetingUs)
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.0f, 1.0f), $"MCH targeting you: {mchTargetingName}");
        else
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No MCH targeting you");

        if (guardReady)
            ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "Guard: AVAILABLE");
        else if (guardCooldownRemaining > 0.1f)
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), $"Guard: on cooldown ({guardCooldownRemaining:F1}s)");
        else
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Guard: NOT AVAILABLE");

        var eventAge = (DateTime.Now - lastDefendEventTime).TotalSeconds;
        if (lastDefendEventTime != DateTime.MinValue && eventAge < 10)
        {
            var col = lastDefendEvent.Contains("ACTIVATED")
                ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f)
                : new Vector4(1.0f, 0.3f, 0.3f, 1.0f);
            ImGui.TextColored(col, $"Last: {lastDefendEvent}");
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Action IDs");
        ImGui.Indent();
        ImGui.BulletText($"Marksman's Spite: {(marksmanSpiteActionId != 0 ? marksmanSpiteActionId.ToString() : "NOT FOUND")}");
        ImGui.BulletText($"Guard: {(guardActionId != 0 ? guardActionId.ToString() : "NOT FOUND")}");
        ImGui.BulletText($"Bravery: {(braveryActionId != 0 ? braveryActionId.ToString() : "NOT FOUND")}");
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Stats");
        ImGui.Indent();
        ImGui.BulletText($"Times Spite detected: {config.TimesDetected}");
        ImGui.BulletText($"Times Guard activated: {config.TimesGuarded}");
        ImGui.Unindent();
    }

    private void DrawExecuteTab()
    {
        // ---- Host Setup ----
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "Host Setup");
        ImGui.Separator();
        ImGui.Spacing();

        // Text input for host name
        var hostBuf = config.HostPlayerName;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("##HostNameInput", ref hostBuf, 64))
        {
            config.HostPlayerName = hostBuf;
            PluginInterface.SavePluginConfig(config);
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear##ClearHost"))
        {
            config.HostPlayerName = string.Empty;
            PluginInterface.SavePluginConfig(config);
        }
        ImGui.SameLine();

        // Party member picker combo
        if (ImGui.BeginCombo("##PartyPick", "Party"))
        {
            var localName = ObjectTable.LocalPlayer?.Name.ToString() ?? string.Empty;
            for (int i = 0; i < PartyList.Length; i++)
            {
                var member = PartyList[i];
                if (member == null) continue;
                var memberName = member.Name.ToString();
                if (memberName == localName) continue; // skip self
                if (ImGui.Selectable(memberName))
                {
                    config.HostPlayerName = memberName;
                    PluginInterface.SavePluginConfig(config);
                }
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();

        // Host status
        if (string.IsNullOrEmpty(config.HostPlayerName))
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Host: not set");
        }
        else if (executeHost != null)
        {
            ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), $"Host: {executeHost.Name}  [FOUND]");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"Host: {config.HostPlayerName}  [NOT IN RANGE]");
        }

        // Host's target
        if (!string.IsNullOrEmpty(executeHostTargetName))
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), $"Host targeting: {executeHostTargetName}");
        else
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Host targeting: None");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ---- FIRE! button ----
        bool canFire = executeIsLocalMch
            && executeHostTarget != null
            && executeInRange
            && executeSpiteReady
            && !IsLbBlockedBySight();

        if (canFire)
        {
            // Pulsing bright red when ready
            float pulse = (float)(Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds * 5.0) * 0.12 + 0.88);
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.85f * pulse, 0.05f, 0.05f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.00f,         0.15f, 0.15f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.60f,         0.00f, 0.00f, 1.0f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.30f, 0.10f, 0.10f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.35f, 0.12f, 0.12f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.25f, 0.08f, 0.08f, 1.0f));
        }

        if (ImGui.Button("  FIRE!  ", new Vector2(-1, 54)))
        {
            if (canFire && executeHostTarget != null)
                TriggerFire(executeHostTarget);
        }

        ImGui.PopStyleColor(3);

        ImGui.Spacing();

        // ---- Readiness status ----
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Readiness");
        ImGui.Indent();

        // Job check
        if (executeIsLocalMch)
            ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "Job: Machinist");
        else
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Job: NOT Machinist (MCH required)");

        // Range
        if (executeHostTarget == null)
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Range: no target");
        else if (executeInRange)
            ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "Range: IN RANGE");
        else
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Range: OUT OF RANGE (>50y)");

        // Marksman's Spite
        if (marksmanSpiteActionId == 0)
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Marksman's Spite: ID not resolved");
        else if (executeSpiteReady)
            ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "Marksman's Spite: AVAILABLE");
        else
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Marksman's Spite: NOT AVAILABLE");

        // Bravery
        if (braveryActionId == 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), "Bravery: NOT FOUND in game data");
        }
        else if (!config.AutoBravery)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Bravery: disabled (Auto Bravery off)");
        }
        else if (executeBraveryReady)
        {
            ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), "Bravery: AVAILABLE");
        }
        else if (executeEngine.IsBraveryOffCooldown())
        {
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.0f, 1.0f), "Bravery: off CD but busy (will wait)");
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), "Bravery: NOT AVAILABLE (will fire Spite anyway)");
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ---- Automation Options ----
        var autoBravery = config.AutoBravery;
        if (ImGui.Checkbox("Auto Bravery##ab", ref autoBravery))
        {
            config.AutoBravery = autoBravery;
            PluginInterface.SavePluginConfig(config);
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "(use Bravery before LB; waits if busy)");

        var syncBravery = config.SyncBraveryWithHost;
        if (ImGui.Checkbox("Sync Bravery with Host##sb", ref syncBravery))
        {
            config.SyncBraveryWithHost = syncBravery;
            PluginInterface.SavePluginConfig(config);
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "(use Bravery when host does)");

        var autoMode = config.ExecuteAutoMode;
        if (ImGui.Checkbox("Auto Mode##am", ref autoMode))
        {
            config.ExecuteAutoMode = autoMode;
            PluginInterface.SavePluginConfig(config);
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "(auto-fires when host uses their LB)");

        // ---- Last action message ----
        if (executeLastMessageTime != DateTime.MinValue)
        {
            var age = (DateTime.Now - executeLastMessageTime).TotalSeconds;
            if (age < 15)
            {
                ImGui.Spacing();
                bool isFired = executeLastMessage.Contains("FIRED") || executeLastMessage.Contains("Deferred") || executeLastMessage.Contains("Synced");
                var msgCol = isFired
                    ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f)
                    : executeLastMessage.Contains("Waiting")
                        ? new Vector4(1.0f, 0.8f, 0.0f, 1.0f)
                        : new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
                ImGui.TextColored(msgCol, executeLastMessage);
            }
        }
    }

    // -----------------------------------------------------------------------
    // SIGHT tab
    // -----------------------------------------------------------------------

    private void DrawSightTab()
    {
        var sightEnabled = config.SightEnabled;
        if (ImGui.Checkbox("Enable SIGHT overlay##sight", ref sightEnabled))
        {
            config.SightEnabled = sightEnabled;
            PluginInterface.SavePluginConfig(config);
        }
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "(circles over visible enemy players)");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "LB Suppression");
        ImGui.Spacing();

        var blockRed = config.SightBlockLbOnRed;
        if (ImGui.Checkbox("Block Marksman's Spite when targeting RED (Guard active)##blockRed", ref blockRed))
        {
            config.SightBlockLbOnRed = blockRed;
            PluginInterface.SavePluginConfig(config);
        }

        var blockYellow = config.SightBlockLbOnYellow;
        if (ImGui.Checkbox("Block Marksman's Spite when targeting YELLOW (damage-reducing buff)##blockYellow", ref blockYellow))
        {
            config.SightBlockLbOnYellow = blockYellow;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "Ally Overlay");
        ImGui.Spacing();

        var showLbStars = config.SightShowLbStars;
        if (ImGui.Checkbox("Show yellow star on party MCH when LB is ready##lbstars", ref showLbStars))
        {
            config.SightShowLbStars = showLbStars;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "Circle Legend");
        ImGui.Indent();
        ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f),  "●/◆  GREEN  — No Guard, no buffs (safe to LB)");
        ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.0f, 1.0f),  "●/◆  YELLOW — No Guard, damage-reducing buff active");
        ImGui.TextColored(new Vector4(1.0f, 0.25f, 0.25f, 1.0f), "●/◆  RED    — Guard active (do not LB)");
        ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.0f, 1.0f),  "★    YELLOW STAR — ally MCH LB ready");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f),  "(◆ = diamond-marked victim; ● = regular circle)");
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "Visible Enemies");
        if (!config.SightEnabled)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "(SIGHT disabled)");
        }
        else if (sightEnemies.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No enemy players detected");
        }
        else
        {
            ImGui.Indent();
            foreach (var enemy in sightEnemies)
            {
                var col = enemy.Color switch
                {
                    SightColor.Red    => new Vector4(1.0f, 0.25f, 0.25f, 1.0f),
                    SightColor.Yellow => new Vector4(1.0f, 0.8f, 0.0f, 1.0f),
                    _                 => new Vector4(0.2f, 1.0f, 0.2f, 1.0f),
                };
                ImGui.TextColored(col, $"● {enemy.Name}");
            }
            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Debug Info");
        ImGui.Indent();
        ImGui.BulletText($"Guard status ID: {(guardStatusId != 0 ? guardStatusId.ToString() : "NOT FOUND")}");
        ImGui.BulletText($"LB ready status ID: {(limitBreakReadyStatusId != 0 ? limitBreakReadyStatusId.ToString() : "NOT FOUND — stars disabled")}");
        ImGui.BulletText($"Damage-reducing buff IDs: {config.SightDamageReducingBuffIds.Count} configured");

        // Live status ID dump for party MCH members — helps identify correct LB ready status
        var localPlayerDbg = ObjectTable.LocalPlayer;
        if (localPlayerDbg != null)
        {
            var partyNamesDbg = new HashSet<string>();
            for (int i = 0; i < PartyList.Length; i++)
            {
                var m = PartyList[i];
                if (m != null) partyNamesDbg.Add(m.Name.ToString());
            }

            foreach (var obj in ObjectTable)
            {
                if (obj == null || obj is not IPlayerCharacter pcDbg) continue;
                if (!partyNamesDbg.Contains(pcDbg.Name.ToString())) continue;
                if (pcDbg.ClassJob.RowId != MchClassJobId) continue;

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"Party MCH: {pcDbg.Name}");
                var statuses = pcDbg.StatusList;
                if (statuses == null || statuses.Length == 0)
                {
                    ImGui.BulletText("  (no statuses)");
                }
                else
                {
                    foreach (var s in statuses)
                        if (s.StatusId != 0)
                            ImGui.BulletText($"  Status {s.StatusId}");
                }
            }
        }
        ImGui.Unindent();
    }

    private void UpdateSightEnemies()
    {
        // Snapshot current positions as lerp "from" before overwriting
        sightPrevPositions.Clear();
        foreach (var e in sightEnemies)
            sightPrevPositions[e.EntityId] = e.Position;
        lastSightPollTime = DateTime.Now;

        sightEnemies.Clear();
        partyMchLbReadyPositions.Clear();

        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null) return;

        var partyNames = new HashSet<string>();
        for (int i = 0; i < PartyList.Length; i++)
        {
            var m = PartyList[i];
            if (m != null) partyNames.Add(m.Name.ToString());
        }
        partyNames.Add(localPlayer.Name.ToString());

        // Collect alliance member entity IDs (Frontlines/Rival Wings: 2 extra alliances of 8)
        var allianceEntityIds = new HashSet<uint>();
        unsafe
        {
            var gm = FFXIVClientStructs.FFXIV.Client.Game.Group.GroupManager.Instance();
            if (gm != null)
            {
                var group = gm->GetGroup();
                if (group != null)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        var member = group->GetAllianceMemberByIndex(i);
                        if (member != null && member->EntityId != 0)
                            allianceEntityIds.Add(member->EntityId);
                    }
                }
            }
        }

        foreach (var obj in ObjectTable)
        {
            if (obj == null) continue;
            if (obj is not IPlayerCharacter pc) continue;

            var pcName = pc.Name.ToString();

            if (partyNames.Contains(pcName))
            {
                if (config.SightShowLbStars && limitBreakReadyStatusId != 0
                    && pc.ClassJob.RowId == MchClassJobId)
                {
                    foreach (var status in pc.StatusList)
                    {
                        if ((uint)status.StatusId == limitBreakReadyStatusId)
                        {
                            partyMchLbReadyPositions.Add(pc.Position);
                            break;
                        }
                    }
                }
            }
            else if (obj.GameObjectId != localPlayer.GameObjectId && pc.IsTargetable
                     && !allianceEntityIds.Contains(pc.EntityId)
                     && IsHostileCharacter(pc))
            {
                sightEnemies.Add(new EnemySightData
                {
                    EntityId   = pc.EntityId,
                    CurrentHp  = pc.CurrentHp,
                    Position   = pc.Position,
                    Color      = GetSightColor(pc),
                    Name       = pcName,
                    ClassJobId = pc.ClassJob.RowId,
                });
            }
        }
    }

    // Draw priority: 0=black(lowest) < 1=red-circle < 2=red-diamond < 3=yellow-circle
    //                < 4=yellow-diamond < 5=green-circle < 6=green-diamond(highest)
    private static int GetShapePriority(bool isAvoid, SightColor color, bool isGuardingNow, bool isDiamond)
    {
        if (isAvoid) return 0;
        int baseP = (isGuardingNow || color == SightColor.Red) ? 1
                  : color == SightColor.Yellow                  ? 3
                  :                                               5; // Green
        return isDiamond ? baseP + 1 : baseP;
    }

    // Circle colours
    private static uint CircleColor(SightColor color, bool isGuardingNow) =>
        ImGui.GetColorU32((isGuardingNow || color == SightColor.Red)
            ? new Vector4(1.00f, 0.25f, 0.25f, 0.9f)   // red
            : color == SightColor.Yellow
            ? new Vector4(1.00f, 0.80f, 0.00f, 0.9f)   // yellow
            : new Vector4(0.20f, 1.00f, 0.20f, 0.9f)); // green

    // Diamond colours — hue-shifted variants
    private static uint DiamondColor(SightColor color, bool isGuardingNow) =>
        ImGui.GetColorU32((isGuardingNow || color == SightColor.Red)
            ? new Vector4(0.95f, 0.10f, 0.50f, 0.9f)   // magenta-red
            : color == SightColor.Yellow
            ? new Vector4(1.00f, 0.55f, 0.00f, 0.9f)   // orange-amber
            : new Vector4(0.05f, 0.85f, 0.65f, 0.9f)); // teal-green

    private void DrawSightOverlay()
    {
        var drawList = ImGui.GetForegroundDrawList();

        uint blackColor = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 0.95f));
        uint glowColor  = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.85f));
        var now = DateTime.Now;
        var guardCutoff = now.AddMilliseconds(-GuardBlackCircleWindowMs);
        float dScale = Math.Clamp(config.SightDiamondScale, 0.3f, 2.0f);
        float cScale = Math.Clamp(config.SightCircleScale,  0.3f, 2.0f);
        float shapeRadius = Math.Max(22f * dScale, 22f * cScale);

        float lerpT = lastSightPollTime == DateTime.MinValue
            ? 1f
            : (float)Math.Min(1.0, (now - lastSightPollTime).TotalMilliseconds
                              / Math.Max(1, config.SightPollIntervalMs));

        var mousePos = ImGui.GetMousePos();

        // Build render list with screen positions and priorities
        var renderList = new System.Collections.Generic.List<(
            EnemySightData Enemy, Vector2 ScreenPos, int Priority,
            bool IsAvoid, bool IsDiamond, uint ShapeColor)>();

        foreach (var enemy in sightEnemies)
        {
            Vector3 worldPos;
            if (lerpT < 1f && sightPrevPositions.TryGetValue(enemy.EntityId, out var prevPos))
                worldPos = Vector3.Lerp(prevPos, enemy.Position, lerpT);
            else
                worldPos = enemy.Position;

            var headPos = new Vector3(worldPos.X, worldPos.Y + 2.3f, worldPos.Z);
            if (!GameGui.WorldToScreen(headPos, out Vector2 screenPos))
                continue;

            bool isAvoid      = config.AvoidList.Contains(enemy.Name);
            bool isGuardingNow = guardingEnemies.TryGetValue(enemy.EntityId, out var guardTime)
                                  && guardTime > guardCutoff;
            bool isDiamond    = !isAvoid && config.Victims.Exists(v => v.Name == enemy.Name && v.DiamondMark);

            // Skip if this shape type is toggled off in Settings
            if (!isAvoid)
            {
                if (isDiamond && !config.SightShowDiamonds) continue;
                if (!isDiamond && !config.SightShowCircles) continue;
            }

            int  priority     = GetShapePriority(isAvoid, enemy.Color, isGuardingNow, isDiamond);
            uint shapeColor   = isAvoid ? blackColor
                              : isDiamond ? DiamondColor(enemy.Color, isGuardingNow)
                              :             CircleColor(enemy.Color, isGuardingNow);

            renderList.Add((enemy, screenPos, priority, isAvoid, isDiamond, shapeColor));
        }

        // Sort ascending: lowest priority drawn first (appears behind), highest on top
        renderList.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        // Find hovered entity: highest priority within click radius
        uint hoverEntityId   = 0;
        int  hoverPriority   = -1;
        Vector2 hoverScreenPos = default;
        foreach (var r in renderList)
        {
            float dist = Vector2.Distance(mousePos, r.ScreenPos);
            if (dist <= shapeRadius && r.Priority > hoverPriority)
            {
                hoverPriority   = r.Priority;
                hoverEntityId   = r.Enemy.EntityId;
                hoverScreenPos  = r.ScreenPos;
            }
        }

        // Transparent fullscreen window — captures input only when hovering a shape
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.SetNextWindowBgAlpha(0.0f);
        var wflags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar |
                     ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                     ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus |
                     ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
                     ImGuiWindowFlags.NoDecoration;
        if (hoverEntityId == 0)
            wflags |= ImGuiWindowFlags.NoInputs;

        ImGui.Begin("##sightClickOverlay", wflags);

        uint clickedEntityId = 0;
        if (hoverEntityId != 0)
        {
            ImGui.SetCursorScreenPos(hoverScreenPos - new Vector2(shapeRadius, shapeRadius));
            if (ImGui.InvisibleButton($"##sc{hoverEntityId}", new Vector2(shapeRadius * 2, shapeRadius * 2)))
                clickedEntityId = hoverEntityId;
        }

        ImGui.End();

        // Draw shapes in priority order (lowest first = appears behind higher-priority shapes)
        foreach (var r in renderList)
        {
            bool isHovered = r.Enemy.EntityId == hoverEntityId;

            if (r.IsAvoid)
            {
                drawList.AddCircleFilled(r.ScreenPos, 22f * cScale, blackColor, 32);
                if (isHovered) drawList.AddCircle(r.ScreenPos, 26f * cScale, glowColor, 32, 2.5f);
                if (config.SightShowJobIcons) DrawJobIcon(drawList, r.ScreenPos, r.Enemy.ClassJobId, 12f * cScale);
                continue;
            }

            if (r.IsDiamond)
            {
                DrawDiamond(drawList, r.ScreenPos, 18f * dScale, r.ShapeColor, 2.5f);
                if (isHovered) drawList.AddCircle(r.ScreenPos, 26f * dScale, glowColor, 32, 2.5f);
                if (config.SightShowJobIcons) DrawJobIcon(drawList, r.ScreenPos, r.Enemy.ClassJobId, 11f * dScale);
            }
            else
            {
                drawList.AddCircleFilled(r.ScreenPos, 20f * cScale, r.ShapeColor, 32);
                if (isHovered) drawList.AddCircle(r.ScreenPos, 24f * cScale, glowColor, 32, 2.5f);
                if (config.SightShowJobIcons) DrawJobIcon(drawList, r.ScreenPos, r.Enemy.ClassJobId, 12f * cScale);
            }
        }

        // Apply click targeting
        if (clickedEntityId != 0)
        {
            foreach (var obj in ObjectTable)
            {
                if (obj == null || obj.EntityId != clickedEntityId) continue;
                TargetManager.Target = obj;
                break;
            }
        }

        // Yellow stars for ally MCH with LB ready
        if (config.SightShowLbStars)
        {
            uint starColor = ImGui.GetColorU32(new Vector4(1.0f, 0.9f, 0.0f, 1.0f));
            foreach (var pos in partyMchLbReadyPositions)
            {
                var headPos = new Vector3(pos.X, pos.Y + 2.3f, pos.Z);
                if (!GameGui.WorldToScreen(headPos, out Vector2 screenPos))
                    continue;
                DrawStar(drawList, screenPos, 16f, 7f, starColor, 2.0f);
            }
        }
    }

    private void DrawVictimsTab()
    {
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "Players hit by Marksman's Spite");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear All##clearVictims"))
        {
            config.Victims.Clear();
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Check a name to show a DIAMOND instead of a circle in SIGHT.");
        ImGui.Separator();
        ImGui.Spacing();

        if (config.Victims.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No victims recorded yet.");
            return;
        }

        var localPos   = ObjectTable.LocalPlayer?.Position;
        var eligible   = config.Victims.Where(v => sightEnemies.Exists(e => e.Name == v.Name)).ToList();
        var unavailable = config.Victims.Where(v => !sightEnemies.Exists(e => e.Name == v.Name)).ToList();
        string? toRemove = null;

        // ---- Eligible group ----
        if (ImGui.CollapsingHeader($"Eligible ({eligible.Count})##eligGroup", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (int i = 0; i < eligible.Count; i++)
            {
                var entry    = eligible[i];
                var sightData = sightEnemies.Find(e => e.Name == entry.Name);
                var col = sightData.Color switch
                {
                    SightColor.Red    => new Vector4(1.0f, 0.25f, 0.25f, 1.0f),
                    SightColor.Yellow => new Vector4(1.0f, 0.8f, 0.0f, 1.0f),
                    _                 => new Vector4(0.2f, 1.0f, 0.2f, 1.0f),
                };

                bool isDiamond = entry.DiamondMark;
                if (ImGui.Checkbox($"##evd{i}", ref isDiamond))
                {
                    entry.DiamondMark = isDiamond;
                    PluginInterface.SavePluginConfig(config);
                }
                ImGui.SameLine();
                ImGui.TextColored(col, $"{(entry.DiamondMark ? "◆" : "●")} {entry.Name}");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.2f, 1.0f), $"x{entry.Kills}");
                if (stats.Players.TryGetValue(entry.Name, out var ps))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), $"[{ps.GamesEncountered}g / {ps.SpiteKills}k]");
                }
                if (localPos.HasValue)
                {
                    float dist = Vector3.Distance(localPos.Value, sightData.Position);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 0.6f, 1.0f), $"{dist:F0}y");
                }
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.6f, 0.1f, 0.1f, 0.6f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 0.9f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                if (ImGui.SmallButton($"X##evr{i}")) toRemove = entry.Name;
                ImGui.PopStyleColor(3);
            }
        }

        ImGui.Spacing();

        // ---- Unavailable group ----
        if (ImGui.CollapsingHeader($"Unavailable ({unavailable.Count})##unavailGroup", ImGuiTreeNodeFlags.DefaultOpen))
        {
            for (int i = 0; i < unavailable.Count; i++)
            {
                var entry = unavailable[i];
                bool isDiamond = entry.DiamondMark;
                if (ImGui.Checkbox($"##uvd{i}", ref isDiamond))
                {
                    entry.DiamondMark = isDiamond;
                    PluginInterface.SavePluginConfig(config);
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), $"  {entry.Name}");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.2f, 1.0f), $"x{entry.Kills}");
                if (stats.Players.TryGetValue(entry.Name, out var ps))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), $"[{ps.GamesEncountered}g / {ps.SpiteKills}k]");
                }
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.6f, 0.1f, 0.1f, 0.6f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.2f, 0.2f, 0.9f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
                if (ImGui.SmallButton($"X##uvr{i}")) toRemove = entry.Name;
                ImGui.PopStyleColor(3);
            }
        }

        if (toRemove != null)
        {
            config.Victims.RemoveAll(v => v.Name == toRemove);
            PluginInterface.SavePluginConfig(config);
        }
    }

    // -----------------------------------------------------------------------
    // SIGHT helpers
    // -----------------------------------------------------------------------

    private static unsafe bool IsHostileCharacter(IPlayerCharacter pc)
    {
        var bc = (FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara*)pc.Address;
        return bc->Character.GetTargetType() == FFXIVClientStructs.FFXIV.Client.Game.Object.TargetType.Enemy;
    }

    private bool HasGuardStatus(IPlayerCharacter pc)
    {
        if (guardStatusId == 0) return false;
        foreach (var status in pc.StatusList)
        {
            if ((uint)status.StatusId == guardStatusId)
                return true;
        }
        return false;
    }

    private bool HasDamageReducingBuff(IPlayerCharacter pc)
    {
        if (config.SightDamageReducingBuffIds.Count == 0) return false;
        foreach (var status in pc.StatusList)
        {
            if (config.SightDamageReducingBuffIds.Contains((uint)status.StatusId))
                return true;
        }
        return false;
    }

    private SightColor GetSightColor(IPlayerCharacter pc)
    {
        if (HasGuardStatus(pc)) return SightColor.Red;
        if (HasDamageReducingBuff(pc)) return SightColor.Yellow;
        return SightColor.Green;
    }

    private unsafe bool UseActionDetour(ActionManager* mgr, ActionType type, uint actionId, long targetId, uint a4, uint a5, uint a6, void* a7)
    {
        if (type == ActionType.Action && actionId == marksmanSpiteActionId && IsLbBlockedBySight())
        {
            Log.Debug("UseAction: Spite blocked by LB suppression.");
            return false;
        }
        return useActionHook!.Original(mgr, type, actionId, targetId, a4, a5, a6, a7);
    }

    private bool IsLbBlockedBySight()
    {
        if (!config.SightEnabled) return false;
        if (!config.SightBlockLbOnRed && !config.SightBlockLbOnYellow) return false;

        var target = TargetManager.Target;
        if (target == null) return false;
        if (target is not IPlayerCharacter pc) return false;

        // Also block if target recently used Guard (hook-detected, same source as red overlay)
        if (config.SightBlockLbOnRed)
        {
            var guardCutoff = DateTime.Now.AddMilliseconds(-GuardBlackCircleWindowMs);
            if (guardingEnemies.TryGetValue(target.EntityId, out var guardTime) && guardTime > guardCutoff)
                return true;
        }

        var color = GetSightColor(pc);
        if (color == SightColor.Red && config.SightBlockLbOnRed) return true;
        if (color == SightColor.Yellow && config.SightBlockLbOnYellow) return true;
        return false;
    }

    private void DrawShareTab()
    {
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "Share Lists");
        ImGui.Spacing();

        // ---- Channel + Sync Now ----
        ImGui.Text("Channel:");
        ImGui.SameLine();
        var channelIdx = (int)config.SyncChatChannel;
        ImGui.SetNextItemWidth(100);
        if (ImGui.BeginCombo("##syncChan", SyncChannelDisplayNames[channelIdx]))
        {
            for (int i = 0; i < SyncChannelDisplayNames.Length; i++)
            {
                if (ImGui.Selectable(SyncChannelDisplayNames[i], i == channelIdx))
                {
                    config.SyncChatChannel = (SyncChannel)i;
                    PluginInterface.SavePluginConfig(config);
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("Sync Now##syncNow"))
            BroadcastSync();
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"({config.Victims.Count}v, {config.AvoidList.Count}a)");

        var autoSync = config.AutoSync;
        if (ImGui.Checkbox("Auto-sync on victim/kill update##autoSync", ref autoSync))
        {
            config.AutoSync = autoSync;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ---- Trusted Players ----
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "Trusted Players");
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Only these players can auto-push their lists to you.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(180);
        ImGui.InputText("##syncAddInput", ref syncAddBuf, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add##syncAdd"))
        {
            var trimmed = syncAddBuf.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && !config.TrustedPlayers.Contains(trimmed))
            {
                config.TrustedPlayers.Add(trimmed);
                PluginInterface.SavePluginConfig(config);
            }
            syncAddBuf = string.Empty;
        }
        ImGui.SameLine();
        if (ImGui.BeginCombo("##trustPartyPick", "Party"))
        {
            var localName = ObjectTable.LocalPlayer?.Name.ToString() ?? string.Empty;
            for (int i = 0; i < PartyList.Length; i++)
            {
                var member = PartyList[i];
                if (member == null) continue;
                var memberName = member.Name.ToString();
                if (memberName == localName) continue;
                if (ImGui.Selectable(memberName))
                {
                    if (!config.TrustedPlayers.Contains(memberName))
                    {
                        config.TrustedPlayers.Add(memberName);
                        PluginInterface.SavePluginConfig(config);
                    }
                }
            }
            ImGui.EndCombo();
        }

        ImGui.Spacing();

        if (config.TrustedPlayers.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No trusted players added yet.");
        }
        else
        {
            int removeIdx = -1;
            for (int i = 0; i < config.TrustedPlayers.Count; i++)
            {
                bool inParty = false;
                for (int j = 0; j < PartyList.Length; j++)
                    if (PartyList[j]?.Name.ToString() == config.TrustedPlayers[i]) { inParty = true; break; }

                var col = inParty ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f) : new Vector4(0.7f, 0.7f, 0.7f, 1.0f);
                ImGui.TextColored(col, config.TrustedPlayers[i]);
                if (inParty) { ImGui.SameLine(); ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.4f, 1.0f), "(in party)"); }
                ImGui.SameLine();
                if (ImGui.SmallButton($"X##tr{i}")) removeIdx = i;
            }
            if (removeIdx >= 0)
            {
                config.TrustedPlayers.RemoveAt(removeIdx);
                PluginInterface.SavePluginConfig(config);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ---- Sync debug ----
        if (!string.IsNullOrEmpty(lastSyncRawSender))
        {
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Last received [AR]:");
            ImGui.Indent();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"Raw: \"{lastSyncRawSender}\"");
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"Parsed: \"{lastSyncNormSender}\"");
            var trustCol = lastSyncWasTrusted
                ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f)
                : new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
            ImGui.TextColored(trustCol, lastSyncWasTrusted ? "Trusted — accepted" : "NOT in trusted list — blocked");
            ImGui.Unindent();
            ImGui.Spacing();
        }

        // ---- Clipboard fallback ----
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Clipboard (manual share):");
        ImGui.Spacing();

        if (ImGui.Button("Export to Clipboard"))
        {
            ImGui.SetClipboardText(EncodeSyncPayload());
            shareStatusMsg = $"Exported {config.Victims.Count} victims, {config.AvoidList.Count} avoids.";
            shareStatusIsError = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Import from Clipboard"))
        {
            var text = ImGui.GetClipboardText();
            if (string.IsNullOrWhiteSpace(text)) { shareStatusMsg = "Clipboard is empty."; shareStatusIsError = true; }
            else HandleImport(text.Trim());
        }

        if (!string.IsNullOrEmpty(shareStatusMsg))
        {
            ImGui.Spacing();
            var col = shareStatusIsError ? new Vector4(1.0f, 0.3f, 0.3f, 1.0f) : new Vector4(0.2f, 1.0f, 0.2f, 1.0f);
            ImGui.TextColored(col, shareStatusMsg);
        }
    }

    // -----------------------------------------------------------------------
    // Share — encode / chat broadcast / receive / clipboard import
    // -----------------------------------------------------------------------

    private string EncodeSyncPayload()
    {
        var payload = new SyncPayload
        {
            Victims = config.Victims.Select(v => v.Name).ToList(),
            Avoids  = new List<string>(config.AvoidList),
        };
        var json  = JsonConvert.SerializeObject(payload);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            gz.Write(bytes, 0, bytes.Length);
        return Convert.ToBase64String(ms.ToArray());
    }

    private string GetSyncChatPrefix() => config.SyncChatChannel switch
    {
        SyncChannel.Party        => "/p",
        SyncChannel.Ls1          => "/ls1",
        SyncChannel.Ls2          => "/ls2",
        SyncChannel.Ls3          => "/ls3",
        SyncChannel.Ls4          => "/ls4",
        SyncChannel.Ls5          => "/ls5",
        SyncChannel.Ls6          => "/ls6",
        SyncChannel.Ls7          => "/ls7",
        SyncChannel.Ls8          => "/ls8",
        SyncChannel.Cwls1        => "/cwl1",
        SyncChannel.Cwls2        => "/cwl2",
        SyncChannel.Cwls3        => "/cwl3",
        SyncChannel.Cwls4        => "/cwl4",
        SyncChannel.Cwls5        => "/cwl5",
        SyncChannel.Cwls6        => "/cwl6",
        SyncChannel.Cwls7        => "/cwl7",
        SyncChannel.Cwls8        => "/cwl8",
        SyncChannel.FreeCompany  => "/fc",
        _                        => "/p",
    };

    private XivChatType GetSyncChatType() => config.SyncChatChannel switch
    {
        SyncChannel.Party        => XivChatType.Party,
        SyncChannel.Ls1          => XivChatType.Ls1,
        SyncChannel.Ls2          => XivChatType.Ls2,
        SyncChannel.Ls3          => XivChatType.Ls3,
        SyncChannel.Ls4          => XivChatType.Ls4,
        SyncChannel.Ls5          => XivChatType.Ls5,
        SyncChannel.Ls6          => XivChatType.Ls6,
        SyncChannel.Ls7          => XivChatType.Ls7,
        SyncChannel.Ls8          => XivChatType.Ls8,
        SyncChannel.Cwls1        => XivChatType.CrossLinkShell1,
        SyncChannel.Cwls2        => XivChatType.CrossLinkShell2,
        SyncChannel.Cwls3        => XivChatType.CrossLinkShell3,
        SyncChannel.Cwls4        => XivChatType.CrossLinkShell4,
        SyncChannel.Cwls5        => XivChatType.CrossLinkShell5,
        SyncChannel.Cwls6        => XivChatType.CrossLinkShell6,
        SyncChannel.Cwls7        => XivChatType.CrossLinkShell7,
        SyncChannel.Cwls8        => XivChatType.CrossLinkShell8,
        SyncChannel.FreeCompany  => XivChatType.FreeCompany,
        _                        => XivChatType.Party,
    };

    private void BroadcastSync()
    {
        if (ObjectTable.LocalPlayer == null)
        {
            ToastGui.ShowError("[AutoReact] Must be logged in to sync.");
            return;
        }

        var b64 = EncodeSyncPayload();
        var prefix = GetSyncChatPrefix();
        int singleMax = MaxChatContent - SyncTag.Length;

        if (b64.Length <= singleMax)
        {
            SendToChannel(prefix, SyncTag + b64);
        }
        else
        {
            const int chunkHeaderMax = 10;
            int chunkMax = MaxChatContent - chunkHeaderMax;
            var chunks = new List<string>();
            for (int i = 0; i < b64.Length; i += chunkMax)
                chunks.Add(b64.Substring(i, Math.Min(chunkMax, b64.Length - i)));
            int n = chunks.Count;
            for (int i = 0; i < n; i++)
                SendToChannel(prefix, $"{SyncTagChunk}{i + 1}/{n}]{chunks[i]}");
        }

        Log.Info($"[AutoReact] Sync sent via {prefix}: {config.Victims.Count} victims, {config.AvoidList.Count} avoids.");
    }

    private unsafe void SendToChannel(string prefix, string text)
    {
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null) { Log.Error("[AutoReact] UIModule null — cannot send"); return; }
            var str = Utf8String.FromString($"{prefix} {text}");
            if (str == null) { Log.Error("[AutoReact] Utf8String null"); return; }
            try   { uiModule->ProcessChatBoxEntry(str); }
            finally { str->Dtor(true); }
        }
        catch (Exception ex) { Log.Error(ex, "[AutoReact] SendToChannel failed"); }
    }

    // Strip rank/leader icons (★ etc.) and @World suffix from raw sender SeString text
    private static string NormalizeSenderName(string raw)
    {
        var atIdx = raw.IndexOf('@');
        var name  = (atIdx >= 0 ? raw.Substring(0, atIdx) : raw).TrimStart();
        // Skip any leading non-letter characters (★, ☆, rank icons, etc.)
        int start = 0;
        while (start < name.Length && !char.IsLetter(name[start])) start++;
        return name.Substring(start).Trim();
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (type != GetSyncChatType()) return;

        var text = message.TextValue;
        if (!text.StartsWith(SyncTag) && !text.StartsWith(SyncTagChunk)) return;

        var rawName    = sender.TextValue;
        var senderName = NormalizeSenderName(rawName);

        // Record for Share tab debug display
        lastSyncRawSender  = rawName;
        lastSyncNormSender = senderName;

        // Ignore our own echoed messages
        var localName = ObjectTable.LocalPlayer?.Name.ToString() ?? string.Empty;
        if (senderName == localName) return;

        lastSyncWasTrusted = config.TrustedPlayers.Contains(senderName);
        if (!lastSyncWasTrusted) return;

        if (text.StartsWith(SyncTag))
        {
            HandleIncomingSync(senderName, text.Substring(SyncTag.Length));
            return;
        }

        // Chunked message
        var closeIdx = text.IndexOf(']', SyncTagChunk.Length);
        if (closeIdx < 0) return;
        var header  = text.Substring(SyncTagChunk.Length, closeIdx - SyncTagChunk.Length);
        var content = text.Substring(closeIdx + 1);
        var slash   = header.IndexOf('/');
        if (slash < 0) return;
        if (!int.TryParse(header.Substring(0, slash), out int partNum)) return;
        if (!int.TryParse(header.Substring(slash + 1), out int total)) return;

        if (!syncChunkBuffer.TryGetValue(senderName, out var buf) || buf.Total != total)
        {
            buf = (total, new Dictionary<int, string>());
            syncChunkBuffer[senderName] = buf;
        }
        buf.Parts[partNum] = content;

        if (buf.Parts.Count == total)
        {
            var fullB64 = string.Concat(Enumerable.Range(1, total).Select(i => buf.Parts[i]));
            syncChunkBuffer.Remove(senderName);
            HandleIncomingSync(senderName, fullB64);
        }
    }

    private void HandleIncomingSync(string senderName, string base64)
    {
        try
        {
            var compressed = Convert.FromBase64String(base64);
            using var ms     = new MemoryStream(compressed);
            using var gz     = new GZipStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, System.Text.Encoding.UTF8);
            var json = reader.ReadToEnd();

            var payload = JsonConvert.DeserializeObject<SyncPayload>(json);
            if (payload == null) return;

            int addedVictims = 0, addedAvoids = 0;

            foreach (var name in payload.Victims)
            {
                if (!config.Victims.Exists(v => v.Name == name))
                {
                    config.Victims.Add(new VictimEntry { Name = name, Kills = 0, DiamondMark = true });
                    addedVictims++;
                }
            }

            foreach (var name in payload.Avoids)
            {
                if (!config.AvoidList.Contains(name))
                {
                    config.AvoidList.Add(name);
                    addedAvoids++;
                }
            }

            PluginInterface.SavePluginConfig(config);
            var msg = $"[AutoReact] Sync from {senderName}: +{addedVictims} victims, +{addedAvoids} avoids";
            ToastGui.ShowNormal(msg);
            Log.Info(msg);
        }
        catch (Exception ex)
        {
            Log.Warning($"[AutoReact] Failed to decode sync from {senderName}: {ex.Message}");
        }
    }

    private void HandleImport(string base64)
    {
        try
        {
            var compressed = Convert.FromBase64String(base64);
            using var ms     = new MemoryStream(compressed);
            using var gz     = new GZipStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, System.Text.Encoding.UTF8);
            var json = reader.ReadToEnd();

            var payload = JsonConvert.DeserializeObject<SyncPayload>(json);
            if (payload == null) { shareStatusMsg = "Import failed: invalid data."; shareStatusIsError = true; return; }

            int addedVictims = 0, addedAvoids = 0;

            foreach (var name in payload.Victims)
                if (!config.Victims.Exists(v => v.Name == name))
                { config.Victims.Add(new VictimEntry { Name = name, Kills = 0, DiamondMark = true }); addedVictims++; }

            foreach (var name in payload.Avoids)
                if (!config.AvoidList.Contains(name))
                { config.AvoidList.Add(name); addedAvoids++; }

            PluginInterface.SavePluginConfig(config);
            shareStatusMsg = $"Imported +{addedVictims} victims, +{addedAvoids} avoids.";
            shareStatusIsError = false;
            if (addedVictims > 0 || addedAvoids > 0)
                ToastGui.ShowNormal($"[AutoReact] +{addedVictims} victims, +{addedAvoids} avoids imported");
            Log.Info($"[AutoReact] Clipboard import: +{addedVictims} victims, +{addedAvoids} avoids");
        }
        catch (Exception ex)
        {
            shareStatusMsg = "Import failed: clipboard is not a valid export.";
            shareStatusIsError = true;
            Log.Warning($"[AutoReact] Import failed: {ex.Message}");
        }
    }

    private void DrawSettingsTab()
    {
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "SIGHT Overlay Timing");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
            "Controls how often enemy positions and colors are refreshed.\nLower = smoother but more CPU. Higher = stuttery but lighter.");
        ImGui.Spacing();

        var intervalMs = config.SightPollIntervalMs;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Update interval (ms)##sightPoll", ref intervalMs, 8, 500))
        {
            config.SightPollIntervalMs = intervalMs;
            PluginInterface.SavePluginConfig(config);
        }

        float approxFps = intervalMs > 0 ? 1000f / intervalMs : 0f;
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), $"≈ {approxFps:F0} updates/sec");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Recommended: 16–50ms  |  Default: 33ms (~30/sec)");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), "SIGHT Shapes");
        ImGui.Separator();
        ImGui.Spacing();

        var diamondScale = config.SightDiamondScale;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("◆ Diamond scale##diamondScale", ref diamondScale, 0.3f, 2.0f, "%.1f"))
        {
            config.SightDiamondScale = diamondScale;
            PluginInterface.SavePluginConfig(config);
        }

        var circleScale = config.SightCircleScale;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("● Circle scale##circleScale", ref circleScale, 0.3f, 2.0f, "%.1f"))
        {
            config.SightCircleScale = circleScale;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.Spacing();

        var showDiamonds = config.SightShowDiamonds;
        if (ImGui.Checkbox("Show diamonds (◆)##showDiamonds", ref showDiamonds))
        {
            config.SightShowDiamonds = showDiamonds;
            PluginInterface.SavePluginConfig(config);
        }
        ImGui.SameLine();
        var showCircles = config.SightShowCircles;
        if (ImGui.Checkbox("Show circles (●)##showCircles", ref showCircles))
        {
            config.SightShowCircles = showCircles;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.Spacing();

        var showJobIcons = config.SightShowJobIcons;
        if (ImGui.Checkbox("Show job icons inside shapes##showJobIcons", ref showJobIcons))
        {
            config.SightShowJobIcons = showJobIcons;
            PluginInterface.SavePluginConfig(config);
        }
    }

    private void DrawAvoidTab()
    {
        ImGui.TextColored(new Vector4(0.1f, 0.1f, 0.1f, 1.0f), "● ");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.9f, 0.3f, 0.3f, 1.0f), "AVOID — Successfully guarded Marksman's Spite");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear All##clearAvoid"))
        {
            config.AvoidList.Clear();
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "These players guarded when hit by Spite. BLACK circle in SIGHT, overrides all.");
        ImGui.Separator();
        ImGui.Spacing();

        if (config.AvoidList.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No avoid players recorded yet.");
        }
        else
        {
            for (int i = config.AvoidList.Count - 1; i >= 0; i--)
            {
                var name = config.AvoidList[i];
                bool visible = sightEnemies.Exists(e => e.Name == name);

                if (visible)
                    ImGui.TextColored(new Vector4(0.2f, 0.2f, 0.2f, 1.0f), $"● {name}");
                else
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), $"  {name}");

                if (stats.Players.TryGetValue(name, out var ps))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 1.0f, 1.0f), $"[{ps.GamesEncountered}g]");
                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##av{i}"))
                {
                    config.AvoidList.RemoveAt(i);
                    PluginInterface.SavePluginConfig(config);
                }
            }
        }
    }

    private void AddAvoid(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!config.AvoidList.Contains(name))
        {
            config.AvoidList.Add(name);
            PluginInterface.SavePluginConfig(config);
            Log.Info($"AVOID: added '{name}'");
        }
        // Ensure a stats entry exists for this player
        GetOrCreatePlayerStats(name);
    }

    // -----------------------------------------------------------------------
    // Persistent stats
    // -----------------------------------------------------------------------

    private void LoadStats()
    {
        try
        {
            if (File.Exists(statsFilePath))
            {
                var json = File.ReadAllText(statsFilePath);
                stats = JsonConvert.DeserializeObject<PersistentStats>(json) ?? new PersistentStats();
                Log.Info($"Loaded persistent stats for {stats.Players.Count} players.");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not load persistent stats: {ex.Message}");
            stats = new PersistentStats();
        }
    }

    private void SaveStats()
    {
        try
        {
            Directory.CreateDirectory(PluginInterface.ConfigDirectory.FullName);
            File.WriteAllText(statsFilePath, JsonConvert.SerializeObject(stats, Formatting.Indented));
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not save persistent stats: {ex.Message}");
        }
    }

    private PlayerStats GetOrCreatePlayerStats(string name)
    {
        if (!stats.Players.TryGetValue(name, out var ps))
        {
            ps = new PlayerStats { Name = name };
            stats.Players[name] = ps;
        }
        return ps;
    }

    private void AddVictim(string name)
    {
        if (string.IsNullOrEmpty(name)) return;

        var existing = config.Victims.Find(v => v.Name == name);
        if (existing != null)
        {
            // Move to top (most recently hit); kills only increment on confirmed death
            config.Victims.Remove(existing);
            config.Victims.Insert(0, existing);
        }
        else
        {
            config.Victims.Insert(0, new VictimEntry { Name = name, Kills = 0, DiamondMark = true });
        }
        PluginInterface.SavePluginConfig(config);
    }

    private void DrawJobIcon(ImDrawListPtr drawList, Vector2 center, uint classJobId, float halfSize)
    {
        if (classJobId == 0) return;
        var tex = TextureProvider.GetFromGameIcon(new GameIconLookup(62100 + classJobId));
        if (!tex.TryGetWrap(out var wrap, out _)) return;
        var h = new Vector2(halfSize, halfSize);
        drawList.AddImage(wrap.Handle, center - h, center + h);
    }

    private static void DrawDiamond(ImDrawListPtr drawList, Vector2 center, float size, uint color, float thickness)
    {
        var top    = new Vector2(center.X,        center.Y - size);
        var right  = new Vector2(center.X + size, center.Y);
        var bottom = new Vector2(center.X,        center.Y + size);
        var left   = new Vector2(center.X - size, center.Y);
        drawList.AddQuadFilled(top, right, bottom, left, color);
    }

    private static void DrawStar(ImDrawListPtr drawList, Vector2 center, float outerR, float innerR, uint color, float thickness)
    {
        var pts = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float angle = (float)(i * Math.PI / 5.0 - Math.PI / 2.0);
            float r = (i % 2 == 0) ? outerR : innerR;
            pts[i] = new Vector2(
                center.X + r * (float)Math.Cos(angle),
                center.Y + r * (float)Math.Sin(angle));
        }
        for (int i = 0; i < 10; i++)
            drawList.AddLine(pts[i], pts[(i + 1) % 10], color, thickness);
    }
}

internal enum SightColor { Green, Yellow, Red }

internal struct EnemySightData
{
    public uint EntityId;
    public uint CurrentHp;
    public Vector3 Position;
    public SightColor Color;
    public string Name;
    public uint ClassJobId;
}
