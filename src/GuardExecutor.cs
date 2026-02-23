using System;

using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoReactFFXIV;

public sealed class GuardExecutor
{
    private readonly IPluginLog log;

    public uint GuardActionId { get; set; } = 0;

    public GuardExecutor(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>
    /// Returns true only if Guard can actually be used right now.
    /// GetActionStatus == 0 means all checks pass: zone, job, cooldown, alive, etc.
    /// IsActionOffCooldown is intentionally kept only inside UseGuard for speed.
    /// </summary>
    public unsafe bool IsGuardReady()
    {
        if (GuardActionId == 0)
            return false;

        var am = ActionManager.Instance();
        if (am == null)
            return false;

        return am->GetActionStatus(ActionType.Action, GuardActionId) == 0;
    }

    public unsafe float GetGuardCooldownRemaining()
    {
        if (GuardActionId == 0)
            return 0;

        var am = ActionManager.Instance();
        if (am == null)
            return 0;

        var total = am->GetRecastTime(ActionType.Action, GuardActionId);
        var elapsed = am->GetRecastTimeElapsed(ActionType.Action, GuardActionId);

        if (total <= 0)
            return 0;

        return Math.Max(0, total - elapsed);
    }

    public unsafe bool UseGuard()
    {
        if (GuardActionId == 0)
        {
            log.Warning("GuardExecutor: Guard action ID not set.");
            return false;
        }

        var am = ActionManager.Instance();
        if (am == null)
        {
            log.Warning("GuardExecutor: ActionManager is null.");
            return false;
        }

        if (!am->IsActionOffCooldown(ActionType.Action, GuardActionId))
        {
            var remaining = GetGuardCooldownRemaining();
            log.Warning($"GuardExecutor: Guard is on cooldown ({remaining:F1}s remaining).");
            return false;
        }

        // 0xE0000000 = target self
        var result = am->UseAction(ActionType.Action, GuardActionId, 0xE0000000);
        log.Info($"GuardExecutor: UseAction(Guard) returned {result}");
        return result;
    }
}
