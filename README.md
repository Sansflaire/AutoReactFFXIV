# AutoReactFFXIV

A Dalamud plugin for FFXIV that provides PvP (Crystal Conflict / Frontlines) combat tools focused on Marksman's Spite (Machinist Limit Break) — detecting incoming shots, coordinating outgoing shots with a teammate, and tracking enemies over time.

---

## Commands

| Command | Description |
|---------|-------------|
| `/autoreact` | Toggle the plugin window |
| `/autoreact on` | Enable auto-guard |
| `/autoreact off` | Disable auto-guard |
| `/autoreact status` | Print current status to chat |

---

## Tabs

### Defend

Protects you from Marksman's Spite automatically.

- **Auto-Guard on Spite detection** — When the plugin detects that a Machinist's Spite is targeting you, it instantly uses your Guard ability. This bypasses human reaction time entirely.
- **Warn when MCH targets you** — Prints a chat warning when any Machinist on the enemy team hard-targets you, giving early warning before they fire.
- **Alert if Guard on cooldown** — Warns you via chat if Spite lands but Guard isn't available.
- **Status display** — Shows plugin active/disabled state, whether a MCH is currently targeting you, Guard cooldown, and the last detected event.
- **Stats** — Lifetime count of times Spite was detected and times Guard was activated.

Detection method: hooks the game's `ReceiveActionEffect` network packet. When the packet arrives indicating Marksman's Spite targeted your entity ID, Guard is fired in the same frame.

---

### Execute

Coordinates Marksman's Spite with a teammate (the "host") so both fire simultaneously for maximum burst.

**Host Setup**
- Type a player's name manually, or pick from a party dropdown.
- Once set, the plugin tracks the host's current target in real time.

**FIRE! Button**
- A large pulsing red button that activates when all conditions are met: you're a Machinist, the host has a target, the target is within 50 yalms, and Marksman's Spite is available.
- Clicking it fires your Spite (with optional Bravery sequencing).

**Readiness Panel**
- Live color-coded display of: job check, range, Spite availability, and Bravery status.

**Automation Options**
- **Auto Bravery** — Automatically uses Bravery before Spite for the damage buff. If Bravery is off cooldown but animation-locked, it waits up to 1.2 seconds for the lock to clear before firing.
- **Sync Bravery with Host** — Uses your Bravery at the same moment the host uses theirs (detected via hook), independently of Auto Bravery.
- **Auto Mode** — When the host fires their Spite, your Spite fires automatically (with full Bravery sequencing).

---

### SIGHT

Draws a real-time overlay on the game world, placing colored markers above every visible enemy player's head. Also draws ally markers for Machinist players with LB ready.

**Enemy Circle Colors**

| Color | Meaning |
|-------|---------|
| **GREEN** | No Guard, no damage-reducing buffs — safe to Spite |
| **YELLOW** | No Guard, but a damage-reducing buff is active (buff IDs configurable) |
| **RED** | Guard is currently active — do not Spite |
| **BLACK** | Player used Guard during or around a Spite cast — likely trying to Guard; if Spite landed on them they move to AVOID permanently |

**Marker Shapes**
- Circle (●) — standard enemy
- Diamond (◆) — enemy marked as "victim" in the Victims tab

**Ally Overlay**
- **Yellow Star (★)** — a party member who is a Machinist and has their Limit Break ready (requires LB ready status ID to be resolved at startup; check debug info if stars don't appear)

**LB Suppression**
- Optionally block Marksman's Spite from firing (disables the FIRE! button and Auto Mode) when your current hard target has Guard active (RED) or a damage-reducing buff (YELLOW).
- Two separate toggles: one for RED, one for YELLOW.

**Debug Info** (bottom of tab)
- Shows resolved Guard status ID, LB ready status ID, and number of configured damage-reducing buff IDs. Useful for verifying correct in-game detection.

---

### Victims

Tracks every player who has been targeted by Marksman's Spite during this session and across sessions.

- Each entry shows the player's name, how many times they've been **hit** by Spite (`x{N}`), and their persistent stats (`[Xg / Yk]` = games encountered / Spite kills).
- **Checkbox** — Check to replace the circle with a **diamond (◆)** in the SIGHT overlay. Diamonds follow the same RED/YELLOW/GREEN color rules.
- **Most recently hit player appears at the top** — if the same player gets hit again, they bump to position #1.
- **Clear All** — removes all victim entries (persistent stats are retained in the separate stats file).

---

### AVOID

Permanently lists players who **successfully guarded against Marksman's Spite** — i.e., they used Guard during the window when Spite was in flight and were hit.

- These players receive a **black circle** in the SIGHT overlay, which overrides all other colors and shapes.
- Shows games encountered per player: `[Xg]`.
- Individual **Remove** buttons to clear specific entries.
- **Clear All** button.

**How AVOID detection works:**
1. Whenever any enemy player uses Guard, they are internally marked with a timestamp (black circle appears for 5 seconds).
2. When Spite's effect fires and a marked player is among its targets, they are permanently added to AVOID.

---

## Overlay Priority

```
AVOID (black circle)
  overrides
Victim diamond-marked (diamond ◆ in SIGHT color)
  overrides
Regular SIGHT circle (● in SIGHT color)
```

---

## Persistent Stats

Stats are saved to a **separate JSON file** that survives plugin reinstalls and config resets:

```
%APPDATA%\XIVLauncher\pluginConfigs\AutoReactFFXIV\persistent_stats.json
```

| Stat | Description |
|------|-------------|
| **Games** (g) | Number of PvP instances (Crystal Conflict / Frontlines) where you encountered this player. Increments once per instance, resets on zone change. |
| **Kills** (k) | Number of times this player's HP reached zero within 5 seconds of you firing Marksman's Spite at them. |

Stats are displayed in both the Victims tab (`[Xg / Yk]`) and the Avoid tab (`[Xg]`).

---

## Technical Notes

### Hook

The plugin hooks `ActionEffectHandler.Receive` (the game's network packet handler for ability effects). This fires synchronously when an action effect is processed client-side, with no perceptible delay. The hook handles:

- Detecting Spite targeting the local player → triggers Auto-Guard
- Detecting the host player firing Spite → triggers Auto Execute
- Detecting any player using Guard → updates black-circle state
- Detecting Spite landing on targets → checks for AVOID condition

### Detection Accuracy

- **Auto-Guard**: Fires in the same frame the Spite packet arrives. Essentially instant.
- **Black Circle**: Appears as soon as the Guard packet is observed (instant). Lasts 5 seconds.
- **AVOID**: Committed when Spite's effect packet shows a target who recently used Guard.
- **Kills**: Based on HP polling at 100ms intervals — there is a small window where kills near the boundary may be missed or misattributed.
- **PvP detection**: Determined at startup by scanning `ContentFinderCondition` for zones with `PvP = true`. If a zone is not recognized, Games tracking may not activate (check `dalamud.log` for resolved count).

### LB Status IDs

Guard and LB-ready statuses are resolved at startup from the Lumina data sheets. If they fail to resolve, the relevant features are disabled. Check `dalamud.log` for:
```
Resolved Guard status ID: XXXX
Using LB ready status ID=XXXX
```
If the LB ready ID is wrong, stars may appear incorrectly. Contact the developer with the correct status ID.

### Buff Configuration

The YELLOW circle condition (damage-reducing buffs) uses an empty list by default. To add buff IDs, modify `config.SightDamageReducingBuffIds` in the saved config at:
```
%APPDATA%\XIVLauncher\pluginConfigs\AutoReactFFXIV.json
```

---

## Requirements

- Dalamud API Level 14
- FFXIV with PvP content (Crystal Conflict, Frontlines)
- Artisan plugin (only required for Execute tab — IPC calls)

---

## Configuration File Location

| File | Purpose |
|------|---------|
| `%APPDATA%\XIVLauncher\pluginConfigs\AutoReactFFXIV.json` | Plugin settings (enables, lists, IDs) |
| `%APPDATA%\XIVLauncher\pluginConfigs\AutoReactFFXIV\persistent_stats.json` | Lifetime player stats (Games, Kills) |
