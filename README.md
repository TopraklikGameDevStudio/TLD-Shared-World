# TLD Shared World (Ortak Dünya)

**A co-op mod for The Long Dark: play with friends in one truly shared world.**

[Türkçe](README.tr.md) · [Download](../../releases) · [Known issues](#known-issues) · [Feedback](#feedback) · [Support the mod](#support-the-mod)

> **Beta.** The mod works and has been played through many test rounds, but it is still young.
> Back up your saves before you try it, and please [tell us](#feedback) what breaks.

Most co-op mods give every player their own copy of the map. Here the world is shared: if your friend
picks up the rifle in the farmhouse, it is gone for you too. You see each other, hunt the same deer,
run from the same wolf and sleep through the same night.

## What it does

- **One world.** Items, containers, fires, carcasses and animals are the same for everyone.
  The host's save is the world; a joining friend gets a copy of it and plays in it.
- **See your friend.** Your friend is drawn as a survivor who walks, crouches, sleeps, lies down and
  holds a torch or flare. The figure shows what they really wear (hat, scarf, gloves and clothing colours).
- **Hunting and wildlife.** Wolves and bears go after the nearest player, and everyone sees it.
  A deer one of you shoots can be harvested by the other.
- **Downed, not dead.** At zero condition you go down instead of dying. A friend has 10 minutes to
  revive you (hold F3). If nobody comes, you wake up somewhere safe and your gear stays where you fell.
- **Sleep together.** Time only speeds up when everyone sleeps or passes time.
- **Long jobs take real time.** Harvesting, crafting or reading does not skip the shared clock; you stay
  at it until the time has passed (F8 carries on without waiting).
- **Proximity voice chat.** Push to talk (T). Closer is louder, and the voice comes from where your friend is.
- **Hand things over.** Face your friend and press V to give what is in your hands, or pick an item in
  the inventory and press V.
- **Whistle.** Press B; your friend hears it and is told roughly where it came from.
- **Shared charcoal map.** What one of you maps shows up on the other's map.
- **Friendly fire** is on: rifle, revolver, flare gun and arrows hurt your friend.
- **Menus in the game's own style.** Everything is in **CO-OP** in the main menu and the Esc menu.
  The mod speaks English, or Turkish when the game is set to Turkish.

## Requirements

- The Long Dark (Steam or Epic), **survival mode**. Every player needs the **same game version** and the
  **same mod version**.
- [MelonLoader](https://github.com/LavaGang/MelonLoader.Installer/releases) **0.7.3**.
- Windows. A microphone if you want to talk.
- Up to 4 players (you included).

## Installing

1. Install MelonLoader 0.7.3 for The Long Dark with the MelonLoader installer, then start the game once
   and close it (the first start can take a few minutes).
2. Download `TLD-Shared-World-<version>.zip` from [Releases](../../releases) and unzip it.
3. Close the game and double-click `install.bat` (it finds Steam and Epic installs and asks first).
   By hand: copy `Mods\TLDCoop.dll` into the game's `Mods` folder and the three DLLs in `UserLibs`
   into the game's `UserLibs` folder.
4. Start the game. The MelonLoader console should list **TLD Shared World (Ortak Dunya)**.

## Playing

**Host (the world's owner)**
1. Load your own survival save.
2. Esc → **CO-OP** → **HOST**.
3. The same menu shows your address: **INTERNET: …** for a friend in another home, **HOME NETWORK: …**
   for a friend on the same router. Choosing it copies it; send it to your friend.

**Joining friend**
1. Copy the address your friend sent you.
2. Main menu → **CO-OP** → **JOIN (COPIED): …** (or **TYPE ADDRESS**).
3. Wait for **SHARED WORLD READY**, then open Survival → Load. The save called **ORTAK DUNYA** opens by itself.

**Over the internet:** when you host, the mod asks your router to open UDP port 27500 (UPnP). If the
menu says your router did not open it, turn on UPnP in the router or forward UDP 27500 to your PC.
If that is not possible (some internet providers do not allow it), install the same VPN on both PCs
(Radmin VPN, Tailscale or Hamachi) and use the address it gives you.

## Keys

| Key | What it does |
|---|---|
| Esc → CO-OP | host, join, copy your address, leave, feedback |
| F9 / F10 / F11 | shortcuts: host / join / leave |
| F6 | status, your address and the key list |
| F3 | hold 5 s next to a downed friend to revive them; hold 3 s while down to give up |
| F8 | get up while waiting for the others to sleep; finish a long job without waiting |
| T | push to talk |
| B | whistle |
| V | give the item in your hands (or the one selected in the inventory) |

If B or V is already used by the game, the mod picks a free key and tells you which.

## Settings

Settings are in `<game folder>\UserData\MelonPreferences.cfg`, under `[TLDCoop]` (each line has an
English and a Turkish description). Some useful ones:

| Setting | Default | |
|---|---|---|
| `Dil` | `auto` | language: `auto`, `en` or `tr` |
| `OyuncuAdi` | your Windows user name | the name your friends see |
| `DostAtesi` | `true` | friendly fire |
| `IsSuresi` | `true` | long jobs take real shared time |
| `SesliKonusma` / `SesMenzili` | `true` / `40` | voice chat and its range in metres |
| `KonusTusu` / `IslikTusu` / `VerTusu` | `T` / `B` / `V` | talk, whistle and give keys |
| `YereYigilma` / `YerdeKalmaDakika` | `true` / `10` | downed state and how long you can be revived |
| `KiyafetGorunumu` | `true` | your friend's figure shows their real clothes |
| `Port` / `OtomatikPortAc` | `27500` / `true` | server port and automatic port opening (UPnP) |
| `MaksOyuncu` | `4` | most players on a server |

## Known issues

This is a beta made by a small studio. These are the things we know about:

- **Mostly tested on one PC.** Most testing ran two copies of the game side by side. A full session
  between two homes over the internet has not been done yet, so connection problems are possible.
- **2 players tested.** The server takes up to 4, and sleep, downed state and animals are written for
  more than two, but 3 and 4 player games have not been tried yet.
- **No password.** While your server is open, anyone with your address and the same mod can join
  (up to the player limit). Share the address only with friends and stop the server when you are done.
- **Internet needs an open port.** Without UPnP or port forwarding (or a VPN) a friend in another home cannot connect.
- **Same versions only.** Different game or mod versions are refused. A game update can break the mod
  until a new version is out.
- **Survival mode only.** The Wintermute story mode is not supported; challenges and Tales are not tested.
- **The world lives in the host's save.** Without the host there is no shared world to play in.
- **Long jobs wait in real time.** A 2-hour harvest keeps you busy for about 10 real minutes
  (F8 skips the wait, but then the job's time is not added to the world). This was tested with the
  game's time acceleration, not yet with every kind of job by hand.
- **The friend's figure is the mod's own survivor model**, not the game's characters. Clothes are shown
  by kind and colour, and the animations are simple.
- **Voice chat** uses Windows' default microphone and speakers, with no echo or noise filtering, and the
  device cannot be chosen in the game.
- **Pasting an address with Ctrl+V** into the address box has not been tested; *JOIN (COPIED)* in the
  menu does the same job and is tested.
- **No in-game settings screen** yet: settings are changed in `MelonPreferences.cfg`.
- The mod is in **English and Turkish** only.

## Feedback

Found a bug or have an idea? Write to **destek@topraklikgamedevstudio.com** (in the game:
CO-OP → FEEDBACK copies the address) or open an [issue](../../issues).

Please include:
- the mod version and the game version, and Steam or Epic;
- whether you were the host or the joining player, and what happened just before the problem;
- the log files `<game folder>\MelonLoader\Latest.log` and `<game folder>\UserData\TLDCoop-*.log`
  (the newest one).

## Support the mod

The mod is free and stays free for everyone. If you enjoy it and want to say thanks, you can support
Topraklık Game Dev Studio through the **Sponsor** button on this page. Donations are optional and do
not unlock anything; everyone gets the same mod.

## Building from source

1. Install the .NET 6 SDK (or newer).
2. Install MelonLoader 0.7.3 into the game and start it once. Copy these from the game folder into a
   `refs` folder at the root of this repository: `MelonLoader\net6\MelonLoader.dll`,
   `MelonLoader\net6\0Harmony.dll`, `MelonLoader\net6\Il2CppInterop.Runtime.dll`,
   `MelonLoader\net6\Il2CppInterop.Common.dll` and everything the project references from
   `MelonLoader\Il2CppAssemblies` (see `src\TLDCoop.csproj`). They belong to the game and to
   MelonLoader, so they are not in this repository.
3. `dotnet build src -c Release` writes `TLDCoop.dll`.

The friend's body (`src/survivor.bin`) is made by `tools/model/gen_survivor.py`.
`src/TestHarness.cs` is the test driver used during development; it only acts on a command file that
players never have.

## Credits and licences

- Made by **Topraklık Game Dev Studio**. Licensed under the [GNU GPL v3](LICENSE): you may use, change
  and share it, and what you share must stay under the same licence with its source.
- [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT) for networking and
  [NAudio](https://github.com/naudio/NAudio) (MIT, © Mark Heath) for voice; their licences are in
  `package/licenses` and in the download.
- Runs on [MelonLoader](https://github.com/LavaGang/MelonLoader) and Harmony.

This is an unofficial fan-made mod. It is not made, endorsed or supported by Hinterland Studio.
The Long Dark is a trademark of Hinterland Studio Inc. You need your own copy of the game.
