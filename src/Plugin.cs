using System;
using System.Numerics;

using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

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

    // ClassJob ID for Machinist
    private const uint MchClassJobId = 31;

    // -----------------------------------------------------------------------
    // Defend tab state (updated each frame poll)
    // -----------------------------------------------------------------------
    private bool mchTargetingUs = false;
    private string mchTargetingName = string.Empty;
    private DateTime lastPoll = DateTime.MinValue;
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

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ResolveActionIds();

        guardExecutor = new GuardExecutor(Log);
        guardExecutor.GuardActionId = guardActionId;

        executeEngine = new ExecuteEngine(Log, ObjectTable, TargetManager);
        executeEngine.MarksmanSpiteActionId = marksmanSpiteActionId;
        executeEngine.BraveryActionId = braveryActionId;

        spiteDetector = new SpiteDetector(GameInteropProvider, Log, ObjectTable);
        spiteDetector.MarksmanSpiteActionId = marksmanSpiteActionId;
        spiteDetector.BraveryActionId = braveryActionId;
        spiteDetector.OnSpiteDetected += OnSpiteDetected;
        spiteDetector.OnHostFiredSpite += OnHostFiredSpite;
        spiteDetector.OnHostUsedBravery += OnHostUsedBravery;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage =
                "Auto React commands:\n" +
                "  /autoreact          - Toggle the window\n" +
                "  /autoreact on       - Enable auto-guard\n" +
                "  /autoreact off      - Disable auto-guard\n" +
                "  /autoreact status   - Show current status",
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
        Framework.Update += OnFrameworkUpdate;

        Log.Info($"AutoReactFFXIV loaded. Spite ID={marksmanSpiteActionId}, Guard ID={guardActionId}, Bravery ID={braveryActionId}");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;
        CommandManager.RemoveHandler(CommandName);

        spiteDetector.OnSpiteDetected -= OnSpiteDetected;
        spiteDetector.OnHostFiredSpite -= OnHostFiredSpite;
        spiteDetector.OnHostUsedBravery -= OnHostUsedBravery;
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
        if (!config.ExecuteAutoMode)
            return;

        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null) return;
        if (localPlayer.ClassJob.RowId != MchClassJobId) return;
        if (targetEntityId == localPlayer.EntityId) return;

        IGameObject? target = null;
        foreach (var obj in ObjectTable)
        {
            if (obj != null && obj.EntityId == targetEntityId) { target = obj; break; }
        }

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
        if (!showWindow)
            return;

        ImGui.SetNextWindowSize(new Vector2(420, 520), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Auto React", ref showWindow, ImGuiWindowFlags.None))
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Auto React v0.3.0");
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
            && executeSpiteReady;

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
}
