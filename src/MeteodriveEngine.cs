using System.Numerics;

using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoReactFFXIV;

/// <summary>
/// Handles firing the Monk PvP Limit Break: Meteodrive.
/// </summary>
public sealed class MeteodriveEngine
{
    private readonly IPluginLog log;
    private readonly ITargetManager targetManager;

    /// <summary>Meteodrive PvP LB action ID (resolved at startup from Lumina).</summary>
    public uint MeteodriveActionId { get; set; } = 0;

    /// <summary>Meteodrive has roughly 20y range (melee PvP LB).</summary>
    private const float MeteodriveRangeYalms = 20f;

    public MeteodriveEngine(IPluginLog log, ITargetManager targetManager)
    {
        this.log = log;
        this.targetManager = targetManager;
    }

    /// <summary>Returns true if the target is within Meteodrive range.</summary>
    public bool IsInRange(IGameObject localPlayer, IGameObject target)
    {
        var dist = Vector3.Distance(localPlayer.Position, target.Position);
        return dist <= MeteodriveRangeYalms;
    }

    /// <summary>Returns true if Meteodrive can be used right now (off cooldown, correct job, etc.).</summary>
    public unsafe bool IsMeteodriveReady()
    {
        if (MeteodriveActionId == 0) return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->GetActionStatus(ActionType.Action, MeteodriveActionId) == 0;
    }

    /// <summary>Targets the object and fires Meteodrive. Returns true if UseAction succeeded.</summary>
    public unsafe bool Fire(IGameObject target)
    {
        if (MeteodriveActionId == 0) return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        // Set hard target, then pass the raw entity ID (uint cast to ulong).
        // GameObjectId is Dalamud's composite ulong and may differ from the game's entity ID.
        targetManager.Target = target;
        var result = am->UseAction(ActionType.Action, MeteodriveActionId, (ulong)target.EntityId);
        log.Info($"MeteodriveEngine: Fire on {target.Name} EntityId={target.EntityId} = {result}");
        return result;
    }
}
