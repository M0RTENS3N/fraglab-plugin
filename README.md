# FragLab Plugin

The FragLab all-in-one [CounterStrikeSharp](https://docs.cssharp.dev/) plugin for CS2 servers. It switches between game modes:

| Mode     | What it does |
|----------|--------------|
| `scrim`  | Ready-up warmup, knife round (!stay / !switch), pauses, tactical timeouts, demo recording, round backups |
| `retake` | Random bombsite, bomb already planted, managed T/CT teams with queue, rotation and scramble. 5 pistol rounds, 5 force buys, then full buys. Free buy menu limited by round type (last buy is remembered), instant defuse unless a molotov is burning |
| `funretake` | Retakes exactly like `retake`, plus a random perk for every player each round (`!perk` shows yours) |
| `prac` | Practice: noclip, rethrow, grenade lineups, airtime, bots, comp spawns. `!dryrun` runs real rounds (freeze/buy time, full money), `!prac` goes back |

Plus, in every mode: **weapon skins, knives and gloves** with an in-game menu (`!skins`).

New modes (minigames) are added by subclassing `GameMode` and registering them in `FragLabPlugin.Load`.

## Requirements

- CS2 dedicated server with [Metamod:Source](https://www.sourcemm.net/downloads.php?branch=dev) and CounterStrikeSharp (built against API 1.0.376)
- .NET 10 SDK to build

## Build and install

```powershell
powershell -File scripts/package.ps1
```

This builds the plugin and creates `dist/FragLabPlugin.zip`.

1. Upload the zip to `game/csgo` (e.g. DatHost file manager) and extract it there; it contains `addons/...` and `cfg/fraglab/...`
2. Without the zip: copy `build/FragLabPlugin.dll` and `data/skins.json` to `addons/counterstrikesharp/plugins/FragLabPlugin/` (json in a `data` subfolder) and `cfg/fraglab` to `game/csgo/cfg/fraglab`
3. Start the server. The config is generated at `addons/counterstrikesharp/configs/plugins/FragLabPlugin/FragLabPlugin.json`
4. For demo recording, add `tv_enable 1` to `server.cfg` (it must be set before the map loads)

### Upgrading from the old UltimatePlugin

The plugin was renamed from UltimatePlugin to FragLab Plugin (`FragLabPlugin`). On the first start it automatically takes over the old config and copies saved spawns, lineups, skin choices, saved buys and nade finder measurements (existing FragLab files are never overwritten). After that, delete `addons/counterstrikesharp/plugins/UltimatePlugin`, `addons/counterstrikesharp/configs/plugins/UltimatePlugin` and `cfg/ultimate` so the old plugin doesn't run next to the new one.

It's named `FragLabPlugin` (not `FragLab`) so it never clashes with the FragLab app's own server plugin (`FragLab.dll`).

## Commands

All commands work in chat as `!command` or `/command`, and in the console as `css_command`. MatchZy-style dot commands work too (`.rethrow`, `.bot`). For key binds use the console name: `bind k css_bot` (`bind k !bot` does not work).

### General
| Command | Description |
|---------|-------------|
| `!mode [scrim\|retake\|funretake\|prac\|none]` | Show the mode, or switch it (admin). `pracc`/`practice` and `fun` also work. While a scrim runs, the switch must be confirmed by typing it again within 15 seconds |
| `!status` | Current mode state |
| `!map <name\|workshop id>` (admin) | Change map; `!map mirage` and `!map de_mirage` both work. Workshop maps by id |
| `!ancient` `!anubis` `!dust2` `!inferno` `!mirage` `!nuke` `!overpass` `!train` `!vertigo` `!office` `!italy` (admin) | Map shortcuts. The current game mode stays active on the new map |
| `!help` | Menu with every command available in the current mode (plus general and skin commands; admin commands only for admins). Select one to run it. The full list is also printed to your console |

### Scrim
| Command | Description |
|---------|-------------|
| `!ready` / `!r`, `!unready` / `!ur` | Ready up during warmup |
| `!stay`, `!switch` | Knife round winners pick a side |
| `!pause` / `!p`, `!unpause` / `!up` | Pause at the next freeze time; both teams must unpause |
| `!tech` | Technical pause at the next freeze time; both teams must unpause |
| `!tac` / `!timeout` | Tactical timeout with no time limit; only your team's `!unpause` ends it (3 per team per match, `TacticalTimeoutsPerTeam`) |
| `!coach <ct\|t>`, `!uncoach` | Coach a team: you're on the team but sit out every knife/live round (spectate and talk to your team). One coach per team, coaches don't need to ready up |
| `!forcestart` (admin) | Start without waiting for ready |
| `!skipknife` (admin) | Skip the knife round |
| `!kniferound <on\|off>` (admin) | Turn the knife round on/off (saved to the config) |
| `!forceunpause` (admin) | Unpause immediately |
| `!warm` (admin) | Warmup menu during scrim warmup: play **Retake**, **Deathmatch** (respawn, rifle and pistol, kills in chat) or go back to the scrim warmup. Works from Deathmatch too |
| `/setwebhook <url>` (admin) | Set the Discord webhook for demos and save it (use `/` or the console so the link isn't shown in chat; `off` turns it off) |
| `!restore <round>` (admin) | Go back to the start of a round and pause (round 1 = pistol round, numbered like the scoreboard) |
| `!teamnames <CT> <T>` (admin) | Set team names |
| `!resetmatch` (admin) | Back to warmup |

**Demos to Discord:** set `Scrim.DiscordWebhookUrl` to a Discord webhook and the demo is posted when a scrim ends, with the map, teams and final score. It's zipped and attached when it fits Discord's limit (`DiscordMaxUploadMb`, 10 MB without boosts). Bigger demos get a message with a download link if you set `DemoDownloadUrl` (e.g. `https://example.com/demos/{file}`). `!uploaddemo` (admin) sends the last demo again. Scrim limits grenades to 2 flashes, 4 in total and 1 of every other type.
### Retake
| Command | Description |
|---------|-------------|
| `!retakeedit` (admin) | Enter/exit spawn edit mode (opens the spawn editor menu) |
| `!sm` (admin) | Open/close the spawn editor menu (you can walk with it open) |
| `!addspawn <t\|ct> <a\|b> [planter]` (admin) | Add a spawn at your position, facing where you look |
| `!delspawn` (admin) | Delete the nearest spawn (within 128 units) |
| `!spawns` (admin) | Spawn counts per site |
| `!savespawns` (admin) | Save spawns to `plugins/FragLabPlugin/spawns/<map>.json` |
| `!deagleonly` (admin) | Toggle Deagle only rounds (no buying) |
| `!awponly` (admin) | Toggle AWP only rounds (no buying) |
| `!hsonly` (admin) | Toggle headshots only: body shots do no damage |
| `!normalweapons` (admin) | Turn all of the above off |

The weapon rules work in retake and fun retake, start from the next round and stay on until turned off or the mode changes. In fun retake, Ninja, Gunslinger and Super Zeus still get their own weapons.

### Fun retake (`!mode funretake`)

Same rounds, spawns, buying and editor commands as Retake (settings in the `Retake` config section). Every round each player rolls a perk: first a rarity, then a perk of that rarity, revealed with a roulette during freeze time. `!perk` shows yours again, and at round end chat lists everyone's perk.

| Rarity | Chance | Perks |
|--------|--------|-------|
| Common (green) | 40% | Vampire (+35 HP per kill, max 150), Regeneration (4 HP/s up to 100), Low Gravity, Fireman (no fire damage + a molotov/incendiary), Gunslinger (Revolver only), Pyromaniac (a molotov/incendiary + x2 fire damage dealt), Sunglasses (flashbangs can't blind you + a flashbang) |
| Rare (blue) | 30% | Speed (1.3x-2x), Jetpack (hold jump in the air), M.C. Hammer (you take damage when standing still after freeze time), Bulletproof (half bullet damage), Bhop (auto bunny hop, no fall damage), Glass Cannon (2x damage dealt and taken) |
| Epic (purple) | 20% | Tank (200 HP), Ghost (nearly invisible), Ninja (CT only: invisible, knife + smoke + kit, default gloves), Enderman (your decoy teleports you where it lands, never into a wall), Totem of Undying (once per round a deadly hit leaves you at 1 HP, then heals you to 30), Creeper (you explode when you die), Clone (T only: press E to place a clone of yourself; when an enemy shoots it you see the enemies through walls for 5 s) |
| Legendary (yellow) | 10% | Wallhack (enemies glow through walls, only you see it), HS King (only headshots hurt you), Radar (enemies on the radar), Golden Shot (x10 damage on every bullet), Phoenix (respawn once per round), Super Zeus (Zeus only, unlimited range, instantly recharged on a kill) |

**Double perk:** rarely (`DoublePerkChance`, 5% per player) you get two perks at once, shown as DOUBLE PERK.

Ninja, Gunslinger and Super Zeus can't buy. A server can't move your crosshair, so Golden Shot simply multiplies damage.

**Map events:** in about 1 round in 5 (`EventChance`) one random event runs for that round only: Thick fog, Moon gravity, Slippery ice, Turbo or Quad damage (all damage x2). Chat and the round-start screen announce it with a description, `!event` shows it again, and everything is restored at round end.

All perk effects are undone before the next round and when switching mode. Chances, numbers and the perk list (`Perks`) are in the `FunRetake` config section.
### Practice (`!mode prac`)

Everything that affects other players or the server is admin-only; personal tools (noclip, god, positions, lineup menu, nade finder search) are for everyone.

| Command | Description |
|---------|-------------|
| `!noclip` / `!nc` | Toggle noclip |
| `!god` | Toggle god mode (on by default in practice) |
| `!noflash` | Toggle flashbangs not blinding you |
| `!rethrow` (admin) | Throw the last grenade again |
| `!last` | Teleport to where you threw your last grenade |
| `!savepos`, `!loadpos` | Save / return to a position |
| `!spawn [n]` | Cycle through (or pick) your team's competitive spawns |
| `!savenade <name>` (admin) | Save a lineup: hold the grenade, aim, and type it. Shared by everyone, saved per map |
| `!nades` | Lineup menu: teleports you there with the grenade in hand |
| `!delnade <name>` (admin) | Delete a lineup |
| `!markers` | Show or hide the lineup markers (smoke, flash, HE, molotov, incendiary, decoy) on the map. Look at a marker and press **E** to go to that lineup; a green circle shows where to aim |
| `!t` / `!ct` / `!spec` | Change your side (practice) |
| `!bot` (admin) | Place a standing bot (enemy team) at your position. You can walk through it, and it respawns there when killed |
| `!kickbots` / `!botkick` (admin) | Remove all bots |
| `!clear` (admin) | Remove smokes, molotovs and grenades in the air |
| `!nf` | Nade finder menu (see below) |
| `!dryrun` / `!dry` (admin) | Real round settings: 8s freeze time, buy time, full $16000 every round, no cheats. Bots are kicked |
| `!prac` / `!exitdry` (admin) | End the dry run, back to practice |
| `!fas` (admin) | Move everyone else to spectator (you stay on your team) |

Everyone spawns with every grenade (smoke, flash, HE, molotov/incendiary, decoy; `Practice.GiveAllGrenades`). Practice uses infinite money and ammo, grenade trajectory previews, impact markers and respawning, and rounds don't end. Chat shows grenade airtime when a nade lands, how long each flash blinded someone, and the damage you deal (weapon or utility, hits and headshots).

#### Lineups: technique and filter

After `!savenade <name>`, the next throw of that grenade is recorded into the lineup: the technique (`normal`, `stepthrow`, `walkthrow`, `runthrow`, `duckthrow`, `jumpthrow`, `wjumpthrow`, `runjumpthrow`, `walkjumpthrow`, `duckjumpthrow`, from speed, ducking, walk key and whether you're in the air) and the click (`full` = left, `medium` = both, `short` = right). The lineup menu shows it, for example `[Smoke] CT Smoke (runjumpthrow, full click)`.

Picking a lineup opens its menu: **Go there**, **Replay ghost** and, for admins, **Delete lineup**. Replay ghost puts you a little behind the spot and shows a ghost walking the path that was recorded before the throw (about 2.5 s), then throws. The grenade's predicted flight is drawn with beams for 6 seconds, and the landing point is printed in chat. Only lineups saved and thrown after this update have a recording; re-save old ones to get ghosts.

`!nades <filter>` filters the menu: a grenade (`smoke`, `flash`, `he`, `molotov`, `decoy`), a technique word (`jump`, `run`, `duck`, `walk`, `step`) or a click (`full`, `medium`, `short`). `!nades` alone shows everything. Lineups saved at the same spot are listed as a stack when you go to one of them.

#### Nade finder (experimental)

1. `!nf` opens the menu (you can walk with it open: R next, Shift+R previous, E select). Pick the grenade type.
2. Stand where you want to throw from: *Throw from here* (`!nfthrow`).
3. Stand where it should land: *Land here* (`!nfland`).
4. Optional: *Accuracy* Normal (within `NadeFinderTolerance`, 25 units), Precise (10), Exact (5) or Loose (50). Lineups always land within 5-50 units; a last very fine search step (0.03 degrees) makes the tight ones possible.
5. *Find lineup* (`!nffind`) simulates thousands of standing left, middle (left + right) and right-click throws, jump-throws and jump middle-throws through the map (a few ms per tick, so no lag). It prefers lineups that still land with slightly-off aim, and puts you on the spot with the aim and grenade ready. If nothing lands close enough it says it's not possible from that spot.
6. `!nfgo` goes back to the found lineup, `!nfsave <name>` adds it to `!nades`.

After a lineup is found, your throw from it is checked: if the technique, click or aim differs from what the finder asked for, chat tells you. When the grenade lands, chat shows how far it was from your spot; if it missed and was thrown right, the finder searches the aim again with the real offset corrected and puts you on the corrected lineup, so each throw lands closer.

The simulation copies CS's throw and bounce physics; in testing on Mirage, real throws landed within 0-9 units of the prediction. `!nfcheck` compares its prediction with your real left-click standing throws, so you can see how accurate it is on a map. Running throws aren't searched. Jump-throws are measured from real jump-throws: after your first clean jump-throw in practice, chat shows how much the jump adds and the finder uses those numbers (saved in `data/jump-throw.txt`).

### Zombies (`!mode zombies`, beta)

COD-style zombie waves. Humans are CT, the zombies are knife-only T bots. Every wave has more zombies with more HP, and they get a bit faster. Hits and kills give points (shown as your money): 10 per hit, 60 per kill, 100 for a headshot kill, 130 for a knife kill. Players who go down come back at the start of the next wave. When everyone is down it's game over and the game restarts at wave 1.

| Command | Description |
|---|---|
| `!shop` | Shop menu: weapons, ammo, armor, mystery box, perks |
| `!ammo` / `!armor` / `!box` | Quick buy: ammo refill, kevlar + helmet, mystery box (random weapon) |
| `!pap` | Pack-a-Punch the gun in your hands, up to 3 times: x2, x3, x4 damage, full ammo, gold color (5000 each) |
| `!open` / **E** | Buy open the door you stand next to |
| `!zdoor a` / `!zdoor b <price> <name>` (admin) | Make a door: stand on one side of a doorway (`a`), then on the other side (`b`) |
| `!zdoor del` / `!zdoor list` (admin) | Delete the nearest door / list the doors on this map |
| `!zedit` (admin) | Edit mode: no waves, no damage, all doors shown and not blocking. A menu (walk with it open) sets corners, makes a door at a price, deletes the nearest door and toggles noclip. `!zedit` again starts a game |
| `!zem` (admin) | Open the edit menu again while in edit mode |
| `!wave` | Current wave and zombies left |
| `!zrestart` (admin) | Restart from wave 1 |

Perks are lost when you go down: **Juggernog** (250 HP), **Double Tap** (x1.5 bullet damage), **Stamin-Up** (run faster), **Quick Revive** (survive one deadly hit). Zombies sometimes drop a power-up (a glowing chicken, walk into it): **Max Ammo** (everyone refilled), **Insta-Kill** and **Double Points** (20 s), **Nuke** (kills every zombie, +400 points each). Every 5th wave has a **boss**: a big dark-red zombie with 10x HP, worth 500 points. Zombies spawn at spots players have walked, 700-1800 units from the nearest player, instead of only at the T spawn.

**Doors** work like COD: red laser boxes block the way until someone buys them (E or `!open`, the price shows on screen when you're close). Zombies ignore doors. Doors are saved per map in `zombie-doors/<map>.json` in the plugin folder, and every new game closes them again. Health **regenerates** after 4 s without being hit (20 HP/s). Zombies that wander far from everyone are moved closer, and zombies stuck on an obstacle jump towards you. The nuke only hurts the boss (25% of its HP). Stamin-Up also cancels the weight of heavy guns.

Prices, wave sizes, zombie HP/speed and points are in the `Zombies` section of the config. Server settings are in `cfg/fraglab/zombies.cfg`.
### Chaos Gun Game (`!mode chaos` / `!mode gg`, beta)

Free for all. Every kill gives you the next weapon on the ladder (20 guns, Glock up to Negev), and the last level is the knife: a knife kill there wins the game. A knife kill on anyone steals a level from them (**HUMILIATION**). Infinite reserve ammo, random deathmatch spawns, 2 s spawn protection (ends when you shoot). Bots fill the server to 6 players (`Chaos.FillBots`). Late joiners start at the lowest level anyone has.

Every 40 seconds a **chaos event** changes the rules for 20 seconds: Low gravity, Speed rush, Headshots only, One shot, Vampire (damage heals you), Ghosts (almost invisible), Bunny hop, Juggernauts (300 HP), Explosive (everyone explodes on death) or Tiny (half size). The **leader glows gold** through walls. Kill streaks: 3 kills heal you and give armor, 5 kills give a speed boost (**RAMPAGE**), 10 is **UNSTOPPABLE**. The center of the screen shows your level, the next weapon, the leader and the chaos timer.

| Command | Description |
|---|---|
| `!level` | Your level, the next weapons and your stats |
| `!ladder` | The whole weapon ladder |
| `!top` | Top 3 players |
| `!chaos` | Current chaos event / time until the next one |
| `!chaosnow [name]` (admin) | Start a chaos event now (random or by name, e.g. `!chaosnow OneShot`) |
| `!ggrestart` (admin) | Restart the game |

The ladder, chaos events, timings and streak rewards are in the `Chaos` section of the config. Server settings are in `cfg/fraglab/chaos.cfg`.

### Skins (all modes)
| Command | Description |
|---------|-------------|
| *Everyone can change skins. In **scrim** mode only admins can change them; skins already saved still apply.* | |
| `!skins` / `!ws` | Open the skins menu |
| `!knife`, `!gloves` | Jump straight to knives or gloves |
| `!skin <name>` | Search skins for the weapon in your hand, e.g. `!skin asiimov` |
| `!wear <0-1\|fn\|mw\|ft\|ww\|bs>` | Wear of the weapon in your hand |
| `!seed <0-1000>` | Pattern seed of the weapon in your hand |

Menu controls: **W/S** scroll (hold to scroll fast), **E** or **D** select, **A** back, **R** close. Players are frozen while the menu is open (`FreezeWhileInMenu`).

Selections are saved per player in `plugins/FragLabPlugin/playerdata/skins/<steamid64>.json`. A new skin shows right away: the weapon is swapped for a fresh copy, keeping its ammo.

Players without access get ready-made **Green / Red / Blue loadouts** in `!skins` instead (`Skins.Presets`, knife + main weapons). Hand-picking single skins is **admin-only** by default (`Skins.RequiredFlag`, default `@css/generic`). Set it to e.g. `"@css/vip"` for VIPs, or `""` for everyone. Disable skins with `Skins.Enabled: false`.

> **Heads-up:** Valve's server guidelines don't allow servers to give players items they don't own. Some servers running skin plugins have had their GSLT (server login token) banned. You use this at your own risk.

> **After CS2 updates:** skins rely on a memory signature (`Skins.SignatureLinux` / `SignatureWindows` in the config). If skins stop showing and the console logs "signature not found", copy the new signature from the [cs2-WeaponPaints gamedata](https://github.com/Nereziel/cs2-WeaponPaints/blob/main/gamedata/weaponpaints.json) into the config.
>
> To add newly released skins: `powershell -File scripts/generate-skins.ps1`, then rebuild.

## Setting up retake spawns

Built-in spawns are included for **ancient, anubis, cache, dust2, inferno, mirage, nuke, overpass, train and vertigo**, so retakes work right away on those maps. They come from [cs2-retakes by B3none](https://github.com/B3none/cs2-retakes) (GPL-3.0) via `scripts/import-retake-spawns.ps1`.

Spawns you make or change with the editor are saved separately (`plugins/FragLabPlugin/spawns/<map>.json`) and always take priority over the built-in ones. Plugin updates never overwrite them. *Reset to default spawns* in the editor menu deletes your custom file again.

To make spawns for another map, or to adjust the built-in ones:


1. `!mode retake`, then `!retakeedit`. The spawn editor menu opens.
2. The menu stays open while you walk: **R** next item, **Shift+R** previous, **E** select. Walk to a spot and select *Add T/CT spawn*.
3. For each site, add several T spawns (at least one **planter** spawn where the bomb should be) and enough CT spawns for your max players.
4. Choose **Save spawns**. You get a ✔ confirmation with the saved counts.
5. Choose **Exit edit mode** to start playing. If anything is unsaved, it asks you to save or discard first.

### Retake rounds and buying

- After retakes start or the map changes there's a **15s warmup** to pick teams (`WarmupSeconds`).
- Doors are opened and locked open every round (not broken), and windows, vents and other breakables are broken (`OpenDoors`, `BreakWindows`). CTs don't spawn closer than 1000 units to the bomb when enough spawns allow it (`CtMinDistanceFromBomb`).
- When no living CT can still finish a defuse before the bomb explodes, the round ends right away as a T win (`EndRoundWhenDefuseImpossible`).
- Scrims (live) and retakes print a **damage report** (to/from each enemy) at round end. Scrims play all 24 rounds (`mp_match_can_clinch 0`).
- Rounds 1-5 are pistol rounds, 6-10 force buys, then full buys (`PistolRounds` / `ForceBuyRounds`). The count restarts when the mode or map changes.
- Everyone gets a default loadout. During **freeze time** (7s) players can buy for free from the normal buy menu (`BuyOnlyInFreezeTime`):
  - The AWP, auto-snipers and shotguns are disabled (`DisabledWeapons`). Remove `weapon_awp` from that list to bring the AWP back
  - Pistol rounds: pistols only. Armor only with the default pistol (Glock/USP-S/P2000); an upgraded pistol means no armor (`PistolRoundArmorOnlyWithDefaultPistol`)
  - Force buys: pistols, SMGs and shotguns (no rifles, snipers or MGs)
  - Full buys: everything, max 1 AWP per team
- **AWP** (disabled by default): buying an AWP once signs you up for it. Each full-buy round, one player per team who wants it gets the AWP. If nobody on a team wants it, a random player gets it 50% of the time (`AwpChance`).
- What you buy is saved per round type and side, and you get it automatically next time.
- Armor, kits and grenades are handed out automatically: **1 utility per player** (`UtilityPerPlayer`), random from the team's list. Only CTs get smokes.
- **Instant defuse** happens when all Ts are dead, the defuse would finish before the bomb explodes, and no molotov/incendiary is burning near the bomb or in the air. It's checked continuously during the defuse, so it kicks in as soon as the molotov burns out or the last T dies.

## Admin permission

Admin commands need the flag set in `AdminFlag` (default `@css/generic`). Give it in `addons/counterstrikesharp/configs/admins.json`.

## Roadmap ideas

- Draw retake spawns in the world while editing
- Skins: StatTrak, name tags, stickers/keychains, agents, music kits
- Weapon preferences per player (!guns: M4A4 vs M4A1-S, AWP opt-in)
- Scrim: per-player stats, Discord webhook with results, coach slots
- More modes: practice (noclip, grenade trajectories, rethrow), 1v1 arenas, aim/deathmatch
