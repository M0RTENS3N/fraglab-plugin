# FragLab setup guide

This guide takes you from an empty CS2 server to a working FragLab setup: scrims with Discord demo links, retakes, practice with lineups, and the minigames.

It's written for someone who has never run a CS2 server before. Steps marked **(optional)** can be skipped.

Contents:

1. [What you need](#1-what-you-need)
2. [Install the server and the plugins](#2-install-the-server-and-the-plugins)
3. [Install FragLab](#3-install-fraglab)
4. [Make yourself admin](#4-make-yourself-admin)
5. [Pick a mode](#5-pick-a-mode)
6. [Discord: demo posts (optional)](#6-discord-demo-posts-optional)
7. [Cloudflare R2: demo download links (optional)](#7-cloudflare-r2-demo-download-links-optional)
8. [Playing a scrim](#8-playing-a-scrim)
9. [Practice](#9-practice)
10. [Retake, zombies, chaos, deathmatch](#10-retake-zombies-chaos-deathmatch)
11. [Config and troubleshooting](#11-config-and-troubleshooting)

---

## 1. What you need

- A **CS2 dedicated server**. Either rent one (DatHost, or any host that gives you the server files and a file manager/FTP), or run one yourself on a Windows or Linux machine.
- **Metamod:Source** and **CounterStrikeSharp** on the server. These are what let plugins run.
- The **FragLab zip** from the [latest release](https://github.com/M0RTENS3N/fraglab-plugin/releases).
- *(Optional)* A **Discord server** where you can add webhooks.
- *(Optional)* A **Cloudflare account** for demo download links.

## 2. Install the server and the plugins

1. Install the CS2 dedicated server. Your host usually does this for you.
2. Install **Metamod:Source** (the version that matches CS2).
3. Install **CounterStrikeSharp** (the release for CS2, API version 1.0.376 or newer). Follow its own install guide: https://docs.cssharp.dev/
4. Start the server once so the folders get created, then stop it.

After this, your server should have `csgo/addons/counterstrikesharp/` and `csgo/addons/metamod/`.

## 3. Install FragLab

1. Download `FragLabPlugin.zip` from the latest release.
2. Extract it into your server's **`csgo`** folder. The zip already has the right folder structure, so the files end up in:
   - `csgo/addons/counterstrikesharp/plugins/FragLabPlugin/`
   - `csgo/cfg/fraglab/`
3. Start the server (or run `css_plugins reload FragLabPlugin` in the server console).
4. The plugin creates its config file on first load:
   `csgo/addons/counterstrikesharp/configs/plugins/FragLabPlugin/FragLabPlugin.json`

Check that the plugin loaded: in the server console, `css_plugins list` should show **FragLab Plugin**.

> **Config not changing?** The plugin reads its config only when it loads. After editing `FragLabPlugin.json`, run `css_plugins reload FragLabPlugin` or restart the server.

## 4. Make yourself admin

Admin commands are protected by a CounterStrikeSharp permission. By default that's `@css/generic`.

1. Find your SteamID64 (look yourself up at https://steamid.io).
2. Add yourself to `csgo/addons/counterstrikesharp/configs/admins.json` with the `@css/generic` flag, or give yourself the flag the way your host documents it.
3. Reconnect to the server and check that admin commands work (e.g. `!mode` shows the current mode).

The server console always counts as admin.

## 5. Pick a mode

Set the mode that starts with the server in `FragLabPlugin.json`:

```json
"DefaultMode": "scrim"
```

Modes: `scrim`, `retake`, `funretake`, `prac`, `zombies`, `chaos`, `dm` (deathmatch) or `none`.

Change it in game at any time with `!mode <name>` (admin).

## 6. Discord: demo posts (optional)

Scrim demos can be posted to a Discord channel when a match ends.

1. In Discord, open the channel you want to use. Click the gear icon (**Edit channel**) → **Integrations** → **Webhooks** → **New Webhook**.
2. Give it a name, then click **Copy Webhook URL**.
3. In the game, as an admin, type in the **console** (not in chat, so nobody else sees the link):

   ```
   css_setwebhook https://discord.com/api/webhooks/123.../abc...
   ```

   You should get "Discord webhook saved."

   To turn it off: `css_setwebhook off`

The webhook is saved in the config, so it stays after restarts.

**Discord's size limit:** 10 MB per file without a server boost (50 MB with boost level 2). Most full scrim demos are bigger than that. For those, set up Cloudflare R2 below, or the message will say the demo is on the server.

## 7. Cloudflare R2: demo download links (optional)

This makes a download link for every demo, so both teams can get it. The links stop working after 24 hours.

**a) Create the bucket**
1. Create a free Cloudflare account at https://dash.cloudflare.com.
2. Open **R2 Object Storage** and enable it if asked. The free tier includes 10 GB.
3. Click **Create bucket** and name it, e.g. `fraglab-demos`.

**b) Make the bucket public**
1. Open the bucket → **Settings**.
2. Under **Public access**, enable the **R2.dev subdomain**.
3. Copy the address it shows, e.g. `https://pub-xxxxxxxx.r2.dev`.

**c) Delete demos after 1 day**
1. In the bucket's **Settings**, find **Object lifecycle rules**.
2. **Add rule**: apply to all objects, and **delete after 1 day**.

**d) Create an API token**
1. Back on the R2 page, open **Manage API tokens** → **Create API token**.
2. Permission: **Object Read & Write**, limited to your bucket.
3. Copy the **Access Key ID**, the **Secret Access Key** and your **Account ID**. The secret is only shown once.

**e) Put the values in the config**

In `FragLabPlugin.json`, in the `"Scrim"` section:

```json
"R2AccountId": "your-account-id",
"R2Bucket": "fraglab-demos",
"R2AccessKeyId": "your-access-key-id",
"R2SecretAccessKey": "your-secret-access-key",
"R2PublicBaseUrl": "https://pub-xxxxxxxx.r2.dev"
```

Then reload the plugin. Don't share this file: the secret gives write access to your bucket.

**f) Test it:** run a scrim (or any demo) and type `/uploaddemo` as admin. The chat should show `Demo ready (link works for 24 hours): https://...`. Open the link in a browser to check that it downloads.

## 8. Playing a scrim

Scrim is the match mode. It runs in stages: warmup → knife round → side choice → live match.

**Set up teams**
- `!teamnames <CT name> <T name>` (admin) names the teams. The names appear in ready-up messages and the demo file name.

**Warmup and ready-up**
- Players join a team and type `!ready`. Each team is ready once one of its players has typed it.
- When both teams are ready, the match starts. Admins can start without waiting with `!forcestart`.
- The status line shows who is ready, e.g. `Sovs (CT): READY   Team B (T): waiting`.
- `!warm` (admin) opens the warmup menu, where you can turn on a **deathmatch warmup** or a **retake warmup** while players wait. Only one of them is on at a time, and the choice is saved for the next scrim.

**During the match**
- `!tac` calls a tactical timeout (no time limit). Both teams type `!unpause` to continue.
- `!pause` pauses at the next freeze time. `!tech` is a technical pause. Both teams must `!unpause`.
- The knife round decides sides: the winners type `!stay` or `!switch`.
- Demos are recorded automatically and uploaded when the match ends (see sections 6 and 7).

**Admin tools**
- `!restore <round>`: restore a round backup.
- `!kniferound off` / `!kniferound on`: skip the knife round or bring it back.
- `!coach ct` / `!coach t`: lets a player coach a team.
- `!scrimcheck`: after going live, lists any server setting that doesn't match the scrim config.

## 9. Practice

Practice is for lineups, grenade testing and bot-based drills. Switch to it with `!mode prac`.

**Basics**
- `!noclip`, `!god`, `!savepos` / `!loadpos`, `!spawn` (competitive spawns, numbered).
- `!bot` places a standing bot where you're looking. `!kickbots` removes them.
- `!t`, `!ct`, `!spec` change your side.

**Lineups**
1. Hold the grenade, stand where you throw from, and aim at the spot you throw at.
2. Type `!savenade <name>`.
3. Throw the grenade the way you want it saved. The plugin records the throw type (standing, jump, run, …) and the click (full, medium, short).
4. Open `!nades` to see them. Pick a lineup to go there, replay its ghost, or (admin) delete it.
5. Look at a lineup marker and press **E** to go to it. A green circle shows where to aim.
6. `!markers` shows or hides the lineup markers.

**Nade finder**
1. `!nf` opens the finder menu.
2. Set the throw spot (`!nfthrow`) and the landing spot (`!nfland`).
3. Start the search (`!nffind`). It finds throws that land on the spot, and prefers aim points on edges.
4. `!nfsave <name>` saves the result as a lineup.

**Dry run**
- `!dryrun` runs competitive round settings (freeze time, buy time, full money) to practice executes.
- `!dryrun` again restarts it. `!pracc` or `!mode prac` goes back to normal practice.

**Spawn squares**
- `!spawnmarks` shows the spawn squares. Admins look at one and press **E** to switch that spawn off or on.
- `!spawnmarks off` / `!spawnmarks on` switches all of them at once.
- `!spawndump` (admin) saves all of the map's spawns to `spawn-dumps/<map>.json`, handy for sharing spawn positions.

**Damage**
- Hits are shown in your chat as you make them, e.g. `Rifle: -27 to Krikey (73 hp)`.

## 10. Retake, zombies, chaos, deathmatch

- **Retake** (`!mode retake`): bomb sites, teams, pistol and force-buy rounds. `funretake` adds random perks. Admin rules: `!deagleonly`, `!awponly`, `!hsonly`, `!normalweapons`.
- **Zombies** (`!mode zombies`, beta): waves of zombies, a shop (`!shop`), perks, doors you buy open (`!zedit` to place them), and bosses. Still in beta.
- **Chaos gun game** (`!mode chaos`, beta): every kill moves you one weapon up the ladder, with random chaos events every 40 seconds.
- **Deathmatch** (`!mode dm`): respawn with a rifle and pistol. Also available as a warmup in scrim (`!warm`).

The full command list is in the [README](../README.md).

## 11. Config and troubleshooting

**Where things are**
- Plugin config: `csgo/addons/counterstrikesharp/configs/plugins/FragLabPlugin/FragLabPlugin.json`
- Server settings per mode: `csgo/cfg/fraglab/*.cfg`
- Demos: `csgo/demos/`
- Lineups: `addons/counterstrikesharp/plugins/FragLabPlugin/lineups/`
- Your skins: saved per player under the plugin's `playerdata/` folder

**Common problems**
- *"Config not changing"*: reload the plugin (`css_plugins reload FragLabPlugin`) or restart the server.
- *Admin commands say "no permission"*: check `admins.json` and the `AdminFlag` setting.
- *Demo not uploaded*: check the R2 values, then run `/uploaddemo`. Errors appear in chat and in the server log.
- *Team names not showing*: set them with `!teamnames`.
- *Something went wrong after a server update*: CS2 and CounterStrikeSharp updates can break plugins. Check the CounterStrikeSharp version, then reinstall the FragLab release that matches it.

**Found a bug?** Open an issue on the [GitHub repository](https://github.com/M0RTENS3N/fraglab-plugin/issues) with what you did, what you expected, and the relevant part of the server log.
