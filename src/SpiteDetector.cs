using System;
using System.Numerics;

using Dalamud.Hooking;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AutoReactFFXIV;

public sealed class SpiteDetector : IDisposable
{
    private readonly IPluginLog log;
    private readonly IObjectTable objectTable;

    private unsafe delegate void ReceiveActionEffectDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    private Hook<ReceiveActionEffectDelegate>? hook;

    public uint MarksmanSpiteActionId { get; set; } = 0;

    // Set to host's EntityId to enable auto-execute detection
    public uint HostEntityId { get; set; } = 0;

    // Set to the Bravery action ID to enable Bravery-sync detection
    public uint BraveryActionId { get; set; } = 0;

    public event Action? OnSpiteDetected;

    // Fired when the host player uses Marksman's Spite; arg is the first target's EntityId
    public event Action<uint>? OnHostFiredSpite;

    // Fired when the host player uses Bravery
    public event Action? OnHostUsedBravery;

    public SpiteDetector(IGameInteropProvider gameInterop, IPluginLog log, IObjectTable objectTable)
    {
        this.log = log;
        this.objectTable = objectTable;

        unsafe
        {
            var addr = ActionEffectHandler.Addresses.Receive.Value;
            hook = gameInterop.HookFromAddress<ReceiveActionEffectDelegate>((nint)addr, OnReceiveActionEffect);
            hook.Enable();
        }

        log.Info("SpiteDetector: ReceiveActionEffect hook enabled.");
    }

    private unsafe void OnReceiveActionEffect(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        try
        {
            if (MarksmanSpiteActionId != 0 && header->ActionId == MarksmanSpiteActionId)
            {
                var localPlayer = objectTable.LocalPlayer;
                if (localPlayer != null)
                {
                    var myEntityId = localPlayer.EntityId;

                    // Defend: check if we are targeted
                    for (int i = 0; i < header->NumTargets; i++)
                    {
                        if (targetEntityIds[i].ObjectId == myEntityId)
                        {
                            log.Info($"SpiteDetector: Marksman's Spite targeting us! Caster={casterEntityId}");
                            OnSpiteDetected?.Invoke();
                            break;
                        }
                    }

                    // Execute auto-mode: check if the host fired Spite at anyone
                    if (HostEntityId != 0 && casterEntityId == HostEntityId && header->NumTargets > 0)
                    {
                        var firstTarget = targetEntityIds[0].ObjectId;
                        log.Info($"SpiteDetector: Host fired Marksman's Spite! Target EntityId={firstTarget}");
                        OnHostFiredSpite?.Invoke(firstTarget);
                    }
                }
            }

            // Bravery sync: detect when host uses Bravery
            if (BraveryActionId != 0 && HostEntityId != 0
                && header->ActionId == BraveryActionId
                && casterEntityId == HostEntityId)
            {
                log.Info("SpiteDetector: Host used Bravery!");
                OnHostUsedBravery?.Invoke();
            }
        }
        catch (Exception ex)
        {
            log.Error($"SpiteDetector error: {ex}");
        }

        hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
    }

    public void Dispose()
    {
        hook?.Dispose();
        log.Info("SpiteDetector: hook disposed.");
    }
}
