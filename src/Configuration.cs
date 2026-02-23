using System;
using Dalamud.Configuration;

namespace AutoReactFFXIV;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public bool AutoGuard { get; set; } = true;
    public bool AlertOnCooldown { get; set; } = true;
    public bool WarnOnMchTarget { get; set; } = true;

    public int TimesDetected { get; set; } = 0;
    public int TimesGuarded { get; set; } = 0;

    // Execute tab
    public string HostPlayerName { get; set; } = string.Empty;
    public bool ExecuteAutoMode { get; set; } = false;
    public bool AutoBravery { get; set; } = true;
    public bool SyncBraveryWithHost { get; set; } = false;
}
