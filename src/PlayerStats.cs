using System;
using System.Collections.Generic;

namespace AutoReactFFXIV;

[Serializable]
public class PlayerStats
{
    public string Name { get; set; } = string.Empty;

    // Number of PvP games (Frontline/CC instances) where we've encountered this player
    public int GamesEncountered { get; set; } = 0;

    // Number of times we killed this player with Marksman's Spite
    public int SpiteKills { get; set; } = 0;
}

[Serializable]
public class PersistentStats
{
    public Dictionary<string, PlayerStats> Players { get; set; } = new Dictionary<string, PlayerStats>();
}
