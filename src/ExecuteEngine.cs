using System;
using System.Numerics;

using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoReactFFXIV;

/// <summary>
/// Handles the Execute tab logic: range checking, readiness, and firing
/// Marksman's Spite (optionally preceded by Bravery) at a designated target.
/// </summary>
public sealed class ExecuteEngine
{
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;

    /// <summary>Marksman's Spite PvP action ID.</summary>
    public uint MarksmanSpiteActionId { get; set; } = 0;

    /// <summary>Bravery action ID (0 if not found in game data).</summary>
    public uint BraveryActionId { get; set; } = 0;

    /// <summary>Marksman's Spite has a 50 yalm range.</summary>
    private const float SpiteRangeYalms = 50f;

    public ExecuteEngine(IPluginLog log, IObjectTable objectTable, ITargetManager targetManager)
    {
        this.log = log;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
    }

    /// <summary>Returns true if the target is within Marksman's Spite range.</summary>
    public bool IsInRange(IGameObject localPlayer, IGameObject target)
    {
        var dist = Vector3.Distance(localPlayer.Position, target.Position);
        return dist <= SpiteRangeYalms;
    }

    /// <summary>
    /// Returns true if Marksman's Spite can actually be used right now.
    /// GetActionStatus returns 0 only when the action passes all checks:
    /// correct zone, correct job, off cooldown, not dead, etc.
    /// </summary>
    public unsafe bool IsSpiteReady()
    {
        if (MarksmanSpiteActionId == 0) return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->GetActionStatus(ActionType.Action, MarksmanSpiteActionId) == 0;
    }

    /// <summary>
    /// Returns true if Bravery can actually be used right now.
    /// Same full-context check as IsSpiteReady.
    /// </summary>
    public unsafe bool IsBraveryReady()
    {
        if (BraveryActionId == 0) return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->GetActionStatus(ActionType.Action, BraveryActionId) == 0;
    }

    /// <summary>
    /// Returns true if Bravery's cooldown timer is at zero (i.e. not on recast cooldown).
    /// This returns true even if we're animation-locked and can't use it immediately.
    /// Use this to distinguish "busy" (off CD but locked) from "on cooldown" (CD running).
    /// </summary>
    public unsafe bool IsBraveryOffCooldown()
    {
        if (BraveryActionId == 0) return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->IsActionOffCooldown(ActionType.Action, BraveryActionId);
    }

    /// <summary>
    /// Uses Bravery on self if it is currently available (full context check).
    /// Returns true if the UseAction call succeeded.
    /// </summary>
    public unsafe bool UseBraveryIfAvailable()
    {
        if (BraveryActionId == 0) return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        if (am->GetActionStatus(ActionType.Action, BraveryActionId) != 0) return false;
        return am->UseAction(ActionType.Action, BraveryActionId, 0xE0000000);
    }

    /// <summary>
    /// Fires Marksman's Spite on the target with no Bravery attempt.
    /// Used when Bravery is on cooldown or disabled.
    /// </summary>
    public unsafe bool FireSpiteOnly(IGameObject target)
    {
        if (MarksmanSpiteActionId == 0) return false;
        var am = ActionManager.Instance();
        if (am == null) return false;
        targetManager.Target = target;
        var result = am->UseAction(ActionType.Action, MarksmanSpiteActionId, target.GameObjectId);
        log.Info($"ExecuteEngine: FireSpiteOnly on {target.Name} returned {result}");
        return result;
    }

    /// <summary>
    /// Targets the object, optionally uses Bravery (self), then fires Marksman's Spite.
    /// Returns a status message describing what happened.
    /// </summary>
    public unsafe string Fire(IGameObject target)
    {
        if (MarksmanSpiteActionId == 0)
            return "Marksman's Spite ID not resolved!";

        var am = ActionManager.Instance();
        if (am == null)
            return "ActionManager unavailable";

        // Set hard target so the player can see who we fired at
        targetManager.Target = target;

        string braveryPrefix;
        if (BraveryActionId == 0)
        {
            braveryPrefix = "[Bravery not found] ";
            log.Warning("ExecuteEngine: Bravery action not found in game data — firing without it.");
        }
        else if (am->GetActionStatus(ActionType.Action, BraveryActionId) == 0)
        {
            am->UseAction(ActionType.Action, BraveryActionId, 0xE0000000);
            braveryPrefix = "Bravery + ";
            log.Info("ExecuteEngine: Used Bravery.");
        }
        else
        {
            braveryPrefix = "[Bravery on CD] ";
            log.Info("ExecuteEngine: Bravery is on cooldown — firing Spite anyway.");
        }

        var result = am->UseAction(ActionType.Action, MarksmanSpiteActionId, target.GameObjectId);
        log.Info($"ExecuteEngine: UseAction(Marksman's Spite) on {target.Name} returned {result}");

        return result
            ? $"{braveryPrefix}Marksman's Spite FIRED on {target.Name}!"
            : $"{braveryPrefix}Marksman's Spite failed (action rejected)";
    }
}
