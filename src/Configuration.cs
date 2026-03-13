using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace AutoReactFFXIV;

[Serializable]
public enum SyncChannel
{
    Party = 0,
    Ls1, Ls2, Ls3, Ls4, Ls5, Ls6, Ls7, Ls8,
    Cwls1, Cwls2, Cwls3, Cwls4, Cwls5, Cwls6, Cwls7, Cwls8,
    FreeCompany,
}

[Serializable]
public sealed class VictimEntry
{
    public string Name { get; set; } = string.Empty;
    public int Kills { get; set; } = 0;
    public bool DiamondMark { get; set; } = false;
}

[Serializable]
public sealed class SyncPayload
{
    public List<string> Victims { get; set; } = new List<string>();
    public List<string> Avoids  { get; set; } = new List<string>();
}

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

    // Monk Execute tab
    public List<string> MonkHostList { get; set; } = new List<string>();
    public bool MonkInstantMeteodrive { get; set; } = false;

    // SIGHT tab
    public bool SightEnabled { get; set; } = false;
    public bool SightBlockLbOnRed { get; set; } = true;
    public bool SightBlockLbOnYellow { get; set; } = false;
    public List<uint> SightDamageReducingBuffIds { get; set; } = new List<uint>();
    public bool SightShowLbStars { get; set; } = true;

    // Settings tab
    public int SightPollIntervalMs { get; set; } = 33;  // ~30fps default
    public float SightDiamondScale { get; set; } = 1.0f;
    public float SightCircleScale { get; set; } = 1.0f;
    public bool SightShowDiamonds { get; set; } = true;
    public bool SightShowCircles { get; set; } = true;
    public bool SightShowJobIcons { get; set; } = false;

    // Victims tab
    public List<VictimEntry> Victims { get; set; } = new List<VictimEntry>();

    // AVOID tab
    public List<string> AvoidList { get; set; } = new List<string>();

    // Share tab
    public SyncChannel SyncChatChannel { get; set; } = SyncChannel.Party;
    public List<string> TrustedPlayers { get; set; } = new List<string>();
    public bool AutoSync { get; set; } = false;
}
