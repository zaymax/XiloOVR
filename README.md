# ExfilZone Wrist Tracker

A standalone SteamVR **wrist overlay** for tracking loot and quest items in
*Contractors Showdown: ExfilZone* — a small panel attached to your controller,
LIV/OVR-Toolkit style, so you can check what you still need to find without
opening the in-game menu.

The app is a pure `IVROverlay` client on top of the SteamVR compositor:

- it does **not** read game memory, inject DLLs, or hook game functions;
- it does **not** touch network traffic;
- it knows nothing about the running game — it just draws a panel in VR space,
  next to any VR title.

**Status: MVP complete, pending in-headset verification.** Checklist on the
wrist, laser-pointer checking with the free hand, button toggle, JSON
persistence, live hot-reload of every data file.

## Features (MVP)

- [x] **Overlay initialization** — connects to the SteamVR runtime, readable
  error (not a crash) when SteamVR is unavailable
- [x] **Wrist attachment** — panel glued to the left/right controller with a
  configurable position/rotation offset; survives controller reconnects
- [x] **Show/hide toggle** — hold a controller button (~0.6 s); actions are
  rebindable in SteamVR's controller-bindings UI, with a short fade in/out
- [x] **Checklist UI** — items with checkboxes; point at the panel with the
  free hand and pull the trigger to check/uncheck (custom laser hit-testing via
  `ComputeOverlayIntersection`); state saved to `checklist.json` and restored
  on launch
- [x] **Live editing** — `config.json`, `checklist.json` and
  `data/items_database.json` are watched and hot-reloaded; edit them on the
  desktop and the wrist panel updates without restarting
- [ ] Next: in-VR item picker with search over the database, laser beam
  visual, autostart with SteamVR, scrolling for long lists

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

**Easiest: download a prebuilt build.** Grab `ExfilZoneTracker-win-x64.zip`
from [Releases](https://github.com/zaymax/VROverlayTracker/releases) (every
push to `main` also produces a downloadable artifact under
[Actions](https://github.com/zaymax/VROverlayTracker/actions)), unzip it
anywhere and run `ExfilZoneTracker.exe`. Self-contained — no .NET required.

**Or build from source** (needs the .NET 8 SDK):

```powershell
git clone https://github.com/zaymax/VROverlayTracker.git
cd VROverlayTracker
dotnet run --project src/ExfilZoneTracker -c Release
```

Standalone single-file exe (no .NET runtime needed on the target machine):

```powershell
dotnet publish src/ExfilZoneTracker -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
.\publish\ExfilZoneTracker.exe
```

For a much smaller build that requires the .NET 8 runtime on the machine,
replace `--self-contained` with `--self-contained false`.

Command line: `--hand left` / `--hand right` overrides the configured hand for
one run.

## Controls

The panel lives on the **watch hand** (`Hand` in config, default right); the
other, **free hand** is the pointer.

| Action | Default binding | Notes |
| --- | --- | --- |
| Show / hide panel | hold **B** (Index), **Y/B** (Touch), **menu** (Vive) ~0.6 s | either hand; hold time = `ToggleHoldMs` |
| Check / uncheck item | **trigger** on the free hand while pointing at the panel | hovered row is highlighted |

Rebind anytime in **SteamVR → Settings → Controllers → Manage Controller
Bindings → ExfilZone Wrist Tracker** (the app registers itself with SteamVR on
launch). If a default binding does not load on your controller type, bind the
two actions there manually once.

There is no visible laser beam yet — aim with the free controller and watch
for the row highlight, then pull the trigger.

## Configuration

`config.json` is created next to the executable on first run and hot-reloads
on save — tune the offsets live while wearing the headset:

```json
{
  "Hand": "right",
  "WidthMeters": 0.22,
  "PositionMeters": { "X": 0, "Y": 0.02, "Z": 0.13 },
  "RotationDegrees": { "X": -90, "Y": 0, "Z": 0 },
  "PanelPixelWidth": 600,
  "PanelPixelHeight": 480,
  "StartVisible": true,
  "ToggleHoldMs": 600,
  "MaxLaserDistanceMeters": 2
}
```

| Key | Meaning |
| --- | --- |
| `Hand` | `"left"` or `"right"` — which controller carries the panel |
| `WidthMeters` | physical panel width; height follows the pixel aspect ratio |
| `PositionMeters` | offset from the controller origin, meters, controller-local axes |
| `RotationDegrees` | `X` = pitch, `Y` = yaw, `Z` = roll; applied yaw → pitch → roll |
| `PanelPixelWidth/Height` | texture resolution (also defines how many rows fit) |
| `StartVisible` | show the panel right after launch |
| `ToggleHoldMs` | how long the toggle button must be held |
| `MaxLaserDistanceMeters` | laser clicks farther than this are ignored |

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

Two separate concerns, both plain JSON, both hot-reloaded:

**`data/items_database.json`** — the static reference of game items (id, name,
category, optional icon/note). Ships with the app; edit it with a text editor
after game patches, no rebuild needed. The current entries are **samples** —
fill in real items from the game's wiki and community sheets.

**`checklist.json`** (created next to the exe on first run) — your active
hunt: references database items by id plus a found flag. This is also how you
add/remove items in the MVP:

```json
{
  "entries": [
    { "itemId": "meds-first-aid-kit", "found": false },
    { "itemId": "elec-graphics-card", "found": true }
  ]
}
```

Rows that don't fit the panel show up as “+N more”; raise `PanelPixelHeight`
to fit more rows (44 px per row).

## What you should see (verification checklist)

1. Start SteamVR, turn both controllers on, run the tracker.
2. Console: `Connected to SteamVR` → `SteamVR Input ready` → `Panel attached`.
3. In the headset: a dark checklist panel with sample items on the back of
   your right forearm, following the controller like a watch.
4. Point at it with the left controller — rows highlight; pull the trigger —
   the checkbox ticks, the name gets struck through, the progress counter in
   the header updates, and `checklist.json` on disk changes.
5. Hold the toggle button ~0.6 s — panel fades out; hold again — fades back.
6. Edit `config.json` / `checklist.json` / the item database on the desktop —
   the panel updates within a second, no restart.
7. Turn the watch-hand controller off → panel hides; on → reappears. Restart
   the tracker → checked items are still checked.
8. Quit SteamVR → the tracker prints `SteamVR is shutting down` and exits.

## Project layout

```
src/ExfilZoneTracker/
  Program.cs           entry point, main loop, config hot-reload, clean shutdown
  OverlayManager.cs    OpenVR init, overlay lifecycle, SteamVR event pump
  WristAttachment.cs   controller discovery + offset matrix (wrist attach)
  ChecklistUI.cs       visibility/fade, laser hover + click, texture updates
  PanelRenderer.cs     GDI+ checklist rendering + pixel layout for hit-testing
  InputManager.cs      SteamVR Input: app/action manifests, toggle + interact
  ChecklistData.cs     active checklist model, persistence, file watchers
  ItemDatabase.cs      game-item reference loading
  AppConfig.cs         config model + load/create
  app.vrmanifest       SteamVR application manifest (app key, action manifest)
  input/               action manifest + default bindings (Index/Touch/Vive)
  OpenVR/              vendored Valve C# binding + win64 openvr_api.dll
data/items_database.json   editable game-item reference
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
  panel hides while it is missing; watch the console. Also rotate your wrist
  as if checking a watch, and check `StartVisible` in config.
- **Buttons do nothing** — console should say `SteamVR Input ready`. Open
  SteamVR's controller-bindings UI and make sure the two tracker actions are
  bound for your controller type.
- **Clicks land on the wrong row** — please open an issue: the UV→pixel
  mapping in `ChecklistUI.ComputeHoveredRow` likely needs its vertical flip
  inverted for your setup.
- **Panel colors look swapped (blue/orange tint)** — open an issue; the
  channel order fix is a one-liner in `PanelRenderer.ToRgba`.
- **Panel too big or in the way** — lower `WidthMeters`, increase `Z`.

## License

[MIT](LICENSE). Vendored OpenVR files are © Valve Corporation, BSD-3-Clause —
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
