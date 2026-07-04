# XiloOVR

A standalone SteamVR **wrist overlay** for tracking loot and quest items in
*Contractors Showdown: ExfilZone* — a small panel attached to your controller,
LIV/OVR-Toolkit style, so you can check what you still need to find without
opening the in-game menu.

The app is a pure `IVROverlay` client on top of the SteamVR compositor:

- it does **not** read game memory, inject DLLs, or hook game functions;
- it does **not** touch network traffic;
- it knows nothing about the running game — it just draws a panel in VR space,
  next to any VR title.

## Features

- [x] **Overlay initialization** — connects to the SteamVR runtime, readable
  error dialog (not a crash) when SteamVR is unavailable
- [x] **Wrist attachment** — panel glued to the left/right controller with a
  configurable position/rotation offset; survives controller reconnects and
  scene-app switches (self-healing re-assert)
- [x] **Show/hide toggle** — click a controller button (X on Touch by
  default); rebindable in SteamVR's controller-bindings UI; short fade in/out
- [x] **Icon-grid checklist** — item icons with `collected/needed` counters
  and a **visible laser beam** from the free hand: **trigger +1**, **grip −1**
  (custom hit-testing via `ComputeOverlayIntersection`); completed items get
  dimmed with a green check; long lists scroll with ▲▼ footer arrows; state
  persists to `checklist.json`
- [x] **In-VR item picker** — the **+** cell opens a database search on the
  VR keyboard: trigger adds an item (or raises its needed count ×2, ×3…),
  grip lowers it and removes the item at zero
- [x] **Real item database** — 600+ items with icons, imported from the
  community project
  [exfil-zone-assistant](https://github.com/zelengeo/exfil-zone-assistant) (MIT)
- [x] **Live editing** — `config.json`, `checklist.json` and
  `data/items_database.json` are watched and hot-reloaded; edit them on the
  desktop and the wrist panel updates without restarting
- [x] **Twitch chat feed** — last messages of your channel's chat at the
  bottom of the panel (read-only, anonymous IRC — no OAuth, no tokens);
  usernames keep their Twitch colors, each line carries a source badge so
  YouTube can merge into the same feed later
- [x] **Dashboard settings** — a XiloOVR tab in the SteamVR dashboard: hand,
  panel offsets/size with live preview, Twitch channel via the VR keyboard,
  chat feed length and connection status; changes apply instantly and persist
  to `config.json`
- [x] **Background app** — no console window, no desktop windows: an **XO**
  tray icon (open data folder / log / quit), errors as dialogs, all
  diagnostics in `xiloovr.log` next to the exe
- [x] **Theming** — one accent color across panel, settings, laser and tray
  (`AccentColorHex` in config or the dashboard tab), green by default
- [ ] Next: Twitch login + sending chat replies from VR (v0.6), follow/sub
  alerts on the panel (v0.6), YouTube chat merged into the same feed (v0.7),
  autostart with SteamVR

Out of scope by design: memory reading, DLL injection, traffic parsing, OCR.

## Tech stack

C# / .NET 8, talking to OpenVR directly through Valve's official C# binding
(`openvr_api.cs` + `openvr_api.dll`, vendored from the
[OpenVR SDK](https://github.com/ValveSoftware/openvr) v2.5.1 — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)). No Unity, no game engine:
the panel is rendered with GDI+ into a pixel buffer and submitted via
`IVROverlay::SetOverlayRaw`, which keeps the app a single lightweight
executable.

## Requirements

- Windows 10/11 x64, Steam + SteamVR, any SteamVR-compatible headset
- To build from source: the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
  (`winget install Microsoft.DotNet.SDK.8`). Prebuilt releases need no .NET at all.

The code cross-compiles from macOS/Linux, but only runs on Windows.

## Get it on the Windows VR machine

**Easiest: download a prebuilt build.** Grab `XiloOVR-win-x64.zip`
from [Releases](https://github.com/zaymax/XiloOVR/releases) (every
push to `main` also produces a downloadable artifact under
[Actions](https://github.com/zaymax/XiloOVR/actions)), unzip it
anywhere and run `XiloOVR.exe`. Self-contained — no .NET required. The app
runs in the background: no window, just the **XO** icon in the system tray
(right-click for data folder, log and quit).

**Or build from source** (needs the .NET 8 SDK):

```powershell
git clone https://github.com/zaymax/XiloOVR.git
cd XiloOVR
dotnet run --project src/XiloOVR -c Release
```

Standalone single-file exe:

```powershell
dotnet publish src/XiloOVR -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Command line: `--hand left` / `--hand right` overrides the configured hand for
one run.

## Controls

The panel lives on the **watch hand** (`Hand` in config, default **left**);
the other, **free hand** is the pointer.

| Action | Default binding | Notes |
| --- | --- | --- |
| Show / hide panel | **X click** (Touch, left hand), **B** (Index), **menu** (Vive) | set `ToggleHoldMs` > 0 to require a long-press |
| +1 collected | **trigger** on the free hand while pointing at an item cell | the laser beam shows where you point |
| −1 collected | **grip** (Touch/Vive) or **A** (Index) on the free hand | |
| Add items | trigger the **+** cell → type a search on the VR keyboard | in the picker: trigger adds / raises needed, grip lowers / removes; `←` returns |
| Scroll | trigger the **▲ ▼** buttons in the footer | appear when the list overflows |

Rebind anytime in **SteamVR → Settings → Controllers → Manage Controller
Bindings → XiloOVR** (the app registers itself with SteamVR on
launch; binding load success/failure is logged to `xiloovr.log`). There is no
visible laser beam yet — aim with the free controller and watch for the cell
highlight.

## Settings in VR

Open the SteamVR dashboard (menu button) and pick the **XiloOVR** tab: hand,
panel position/rotation/width with ± buttons (the wrist panel moves live as
you click), show-on-start, Twitch channel via SteamVR's VR keyboard, chat
feed length and connection status. Every change applies immediately and is
written to `config.json`, so the file below stays the single source of truth.
The tab also lists features planned for the next versions.

## Configuration

`config.json` is created next to the executable on first run and hot-reloads
on save — tune the offsets live while wearing the headset:

```json
{
  "Hand": "left",
  "WidthMeters": 0.22,
  "PositionMeters": { "X": 0, "Y": 0.02, "Z": 0.13 },
  "RotationDegrees": { "X": -90, "Y": 0, "Z": 0 },
  "PanelPixelWidth": 600,
  "PanelPixelHeight": 520,
  "StartVisible": true,
  "ToggleHoldMs": 0,
  "MaxLaserDistanceMeters": 2,
  "TwitchChannel": "",
  "ChatMessagesShown": 6,
  "AccentColorHex": "#34D399"
}
```

| Key | Meaning |
| --- | --- |
| `Hand` | `"left"` or `"right"` — which controller carries the panel |
| `WidthMeters` | physical panel width; height follows the pixel aspect ratio |
| `PositionMeters` | offset from the controller origin, meters, controller-local axes |
| `RotationDegrees` | `X` = pitch, `Y` = yaw, `Z` = roll; applied yaw → pitch → roll |
| `PanelPixelWidth/Height` | texture resolution (also defines how many icon cells fit) |
| `StartVisible` | show the panel right after launch |
| `ToggleHoldMs` | 0 = toggle on click; > 0 = button must be held that long |
| `MaxLaserDistanceMeters` | laser clicks farther than this are ignored |
| `TwitchChannel` | your channel name (e.g. `"zaymax"`); empty = no chat section |
| `ChatMessagesShown` | chat lines at the bottom of the panel (1–20) |
| `AccentColorHex` | accent color of the panel, settings, laser and tray (HTML hex) |

Controller-local axes (OpenVR convention): **+X** to the right, **+Y** up out
of the button face, **−Z** along the pointing direction. So `Z: 0.13` moves the
panel 13 cm back toward your wrist, and pitch −90° lays it flat, facing up like
a watch face.

Tuning tips — controller origins differ between Index knuckles, Touch and Vive
wands, so expect to adjust (live, thanks to hot-reload):

- panel too far / too close along the arm → change `Z` (typical range 0.10–0.18);
- text runs across the arm instead of along it → roll `Z: 90` or `-90`;
- you see the back side of the panel → flip pitch to `+90` or add yaw `180`;
- panel blocks your aim → shrink `WidthMeters`, raise `Y` slightly.

## Data files

**`data/items_database.json` + `data/icons/`** — the reference database:
600+ items (id, name, category, icon) generated from
[exfil-zone-assistant](https://github.com/zelengeo/exfil-zone-assistant)
(MIT) by [tools/import_assistant_data.py](tools/import_assistant_data.py);
icons are downscaled to 96 px PNGs. Re-run the script after game patches to
refresh, or edit the JSON by hand — it hot-reloads.

**`checklist.json`** (created next to the exe on first run) — your active
hunt: database item ids plus how many you need and how many you've collected.
This file is also how you add/remove items for now:

```json
{
  "entries": [
    { "itemId": "taskitem_ark_floppydisk", "needed": 1, "collected": 0 },
    { "itemId": "taskitem_electricdrill_blue", "needed": 3, "collected": 1 }
  ]
}
```

Look up ids in `data/items_database.json` (search by item name). The old
v0.1 format (`"found": true/false`) migrates automatically. Cells that don't
fit the panel show up as “+N more”; raise `PanelPixelHeight` to fit more rows
of the grid.

## Stream chat

Set `"TwitchChannel": "yourchannel"` in `config.json` (hot-reloads, so you can
do it mid-session) and the bottom of the panel becomes a live chat feed:
newest messages at the bottom, usernames in their Twitch colors, a purple `T`
badge per message (the badge marks the platform — YouTube will join the same
feed later). Reading is anonymous over Twitch IRC: no login, no OAuth token,
nothing to configure besides the channel name.

The chat section takes `ChatMessagesShown × 22 + 12` pixels from the grid
area; with the default 520-pixel panel and 6 chat lines about two icon rows
remain. Raise `PanelPixelHeight` to ~660–700 if you want three icon rows plus
chat. The client reconnects automatically with backoff if the connection
drops, and switches channels on the fly when you edit the config.

## What you should see (verification checklist)

1. Start SteamVR, turn both controllers on, run the tracker.
2. The XO icon appears in the tray; `xiloovr.log` (tray → Open log) records
   `Connected to SteamVR` → `SteamVR Input ready` → `Panel attached`.
3. In the headset: a panel with a grid of item icons and counters on your left
   forearm, following the controller like a watch.
4. A thin beam shoots from the free controller; the cell under it highlights.
   Trigger bumps the counter (`1/3`), grip drops it; a full counter dims the
   icon under a green check; `checklist.json` changes on disk.
5. Trigger the **+** cell — the VR keyboard opens; type e.g. `key`, confirm —
   the grid shows matching items; trigger adds one (×1 badge appears), `←`
   returns to the checklist; overflowing lists scroll with the ▲▼ arrows.
6. Click X (left Touch controller) — the panel fades out; click again — back.
7. Edit `config.json` / `checklist.json` / the item database on the desktop —
   the panel updates within a second, no restart.
8. Turn the watch-hand controller off → panel hides; on → reappears. Restart
   the tracker → counters are preserved.
9. Set `TwitchChannel` in config while the app runs — the log records
   `Twitch chat: joined #yourchannel`, the feed appears at the panel bottom,
   and messages typed in your chat show up within a second.
10. Open the SteamVR dashboard → **XiloOVR** tab: hand/offset/width buttons
    move the wrist panel live; `Edit` opens the VR keyboard for the channel
    and the accent color.
11. Quit SteamVR → the tracker prints `SteamVR is shutting down` and exits.

## Project layout

```
src/XiloOVR/
  Program.cs           entry point, main loop, config hot-reload, clean shutdown
  OverlayManager.cs    OpenVR init, overlay lifecycle, SteamVR event pump
  WristAttachment.cs   controller discovery + offset matrix (wrist attach)
  ChecklistUI.cs       visibility/fade, laser hover + clicks, item picker, scrolling
  PanelRenderer.cs     GDI+ icon-grid rendering + pixel layout for hit-testing
  LaserBeam.cs         visible beam overlays attached to the pointer controller
  Theme.cs             accent color parsing (AccentColorHex)
  IconCache.cs         item icon bitmaps, reloaded when data changes
  InputManager.cs      SteamVR Input: app/action manifests, toggle/+1/−1
  TwitchChatClient.cs  anonymous read-only Twitch IRC client (background thread)
  SettingsUI.cs        SteamVR dashboard settings tab (laser buttons + VR keyboard)
  ChecklistData.cs     active checklist model, persistence, file watchers
  ItemDatabase.cs      game-item reference loading
  AppConfig.cs         config model + load/create
  app.vrmanifest       SteamVR application manifest (app key, action manifest)
  input/               action manifest + default bindings (Index/Touch/Vive)
  OpenVR/              vendored Valve C# binding + win64 openvr_api.dll
data/items_database.json   generated game-item reference (hot-reloaded)
data/icons/                item icons, 96 px PNG (generated)
tools/import_assistant_data.py   regenerates the database + icons
```

## Troubleshooting

- **Panel shows in SteamVR Home but disappears in every game** — the game is
  almost certainly running through a different OpenXR runtime (Oculus, VDXR)
  and bypassing SteamVR's compositor entirely; SteamVR overlays can only
  exist inside SteamVR. Fixes, in order:
  1. If the game's Steam launch dialog offers a mode choice (ExfilZone does),
     pick **SteamVR**, not OpenXR.
  2. In **SteamVR → Settings → General** enable **“Set SteamVR as OpenXR
     runtime”**, then restart the game.
  3. Virtual Desktop users: launch the game from Steam/SteamVR Home, not from
     VD's Games tab (which prefers VDXR), or set VD's OpenXR runtime to
     SteamVR.
  Quick check: if you cannot open the SteamVR dashboard inside the game, the
  game is not running under SteamVR.
- **“SteamVR is not running…”** — start SteamVR first, then the tracker.
- **Panel not visible** — is the watch-hand controller on and tracked? The
  panel hides while it is missing; check `xiloovr.log`. Also rotate your wrist
  as if checking a watch, and check `StartVisible` in config.
- **Buttons do nothing** — the log should say `SteamVR Input ready` and
  `SteamVR loaded controller bindings`. If it reports a binding load failure,
  bind the three actions manually in SteamVR's binding UI.
- **Cell shows an item name instead of an icon** — the `itemId` is not in the
  database (typo?) or its icon file is missing from `data/icons/`.
- **Clicks land on the wrong cell** — please open an issue: the UV→pixel
  mapping in `ChecklistUI.ComputeHoveredRow` likely needs its vertical flip
  inverted for your setup.
- **Panel colors look swapped (blue/orange tint)** — open an issue; the
  channel order fix is a one-liner in `PanelRenderer.ToRgba`.
- **Panel too big or in the way** — lower `WidthMeters`, increase `Z`.

## License

[MIT](LICENSE). Vendored OpenVR files are © Valve Corporation (BSD-3-Clause);
item data and icons come from
[exfil-zone-assistant](https://github.com/zelengeo/exfil-zone-assistant) (MIT)
— see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
