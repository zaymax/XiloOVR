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

**Status: early prototype (milestones 1–2 of the MVP).** The app connects to
SteamVR, shows a placeholder panel on your wrist, survives controller
reconnects, and exits cleanly when SteamVR closes. The interactive checklist
is the next milestone.

## Roadmap (MVP)

- [x] **1. Overlay initialization** — connect to the SteamVR runtime, create the
  overlay, readable error (not a crash) when SteamVR is unavailable
- [x] **2. Wrist attachment** — panel glued to the left/right controller with a
  configurable position/rotation offset (`config.json`)
- [ ] **3. Show/hide toggle** — controller button binding via SteamVR Input
- [ ] **4. Checklist UI** — items with checkboxes, laser-pointer interaction
  (`ComputeOverlayIntersection`), state persisted to JSON
- [ ] **5. Item picker** — search the bundled game-item database
  ([data/items_database.json](data/items_database.json)) and add items to the
  active checklist

Out of scope by design: memory reading, DLL injection, traffic parsing, OCR.

## Tech stack

C# / .NET 8, talking to OpenVR directly through Valve's official C# binding
(`openvr_api.cs` + `openvr_api.dll`, vendored from the
[OpenVR SDK](https://github.com/ValveSoftware/openvr) v2.5.1 — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)). No Unity, no game engine:
the panel is rendered to a pixel buffer with GDI+ and submitted via
`IVROverlay::SetOverlayRaw`, which keeps the app a single lightweight
executable.

## Requirements

- Windows 10/11 x64, Steam + SteamVR, any SteamVR-compatible headset
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
  (published releases will not need it)

The code cross-compiles from macOS/Linux, but only runs on Windows.

## Build & run (on the Windows VR machine)

```powershell
dotnet build src/ExfilZoneTracker -c Release
dotnet run --project src/ExfilZoneTracker -c Release
```

Standalone single-file exe (no .NET runtime needed on the target machine):

```powershell
dotnet publish src/ExfilZoneTracker -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
.\publish\ExfilZoneTracker.exe
```

For a much smaller build that requires the .NET 8 runtime to be installed,
replace `--self-contained` with `--self-contained false`.

Command line: `--hand left` / `--hand right` overrides the configured hand for
one run without editing the config.

## What you should see (verification checklist)

1. Start SteamVR, turn both controllers on, run the tracker.
2. Console prints `Connected to SteamVR`, then `Panel attached to the right
   controller (device #N)`.
3. In the headset: a dark panel with **“Hello wrist overlay”** near your right
   wrist/forearm. It follows the controller like a watch.
4. Turn the controller off — the panel hides; turn it back on — it reappears.
5. Quit SteamVR — the tracker prints `SteamVR is shutting down, exiting.` and
   terminates on its own.
6. Launch the tracker when SteamVR is installed but not running — SteamVR may
   auto-start (standard behavior for overlay-type apps). If the runtime is not
   installed or the headset is missing, you get a plain-language error and exit
   code 1 instead of a crash.

## Configuration

`config.json` is created next to the executable on first run:

```json
{
  "Hand": "right",
  "WidthMeters": 0.22,
  "PositionMeters": { "X": 0, "Y": 0.02, "Z": 0.13 },
  "RotationDegrees": { "X": -90, "Y": 0, "Z": 0 },
  "PanelPixelWidth": 600,
  "PanelPixelHeight": 400
}
```

| Key | Meaning |
| --- | --- |
| `Hand` | `"left"` or `"right"` — which controller carries the panel |
| `WidthMeters` | physical panel width; height follows the pixel aspect ratio |
| `PositionMeters` | offset from the controller origin, in meters, controller-local axes |
| `RotationDegrees` | `X` = pitch, `Y` = yaw, `Z` = roll; applied yaw → pitch → roll |
| `PanelPixelWidth/Height` | texture resolution |

Controller-local axes (OpenVR convention): **+X** to the right, **+Y** up out
of the button face, **−Z** along the pointing direction. So `Z: 0.13` moves the
panel 13 cm back toward your wrist, and pitch −90° lays it flat, facing up like
a watch face. The `▲ TOP` marker on the panel shows where its top edge points,
which makes tuning much easier.

Tuning tips — controller origins differ between Index knuckles, Touch and Vive
wands, so expect to adjust:

- panel too far / too close along the arm → change `Z` (typical range 0.10–0.18);
- text runs across the arm instead of along it → roll `Z: 90` or `-90`;
- you see the back side of the panel → flip pitch to `+90` or add yaw `180`;
- panel blocks your aim → shrink `WidthMeters`, raise `Y` slightly.

## Items database

[data/items_database.json](data/items_database.json) is the static reference
of game items (id, name, category, optional icon/note). It ships with the app
and is meant to be edited with a plain text editor after game patches — no
rebuild required. The current entries are **samples**: fill in real items from
the game's wiki and community sheets. The active checklist (next milestone)
will reference these items by `id` and live in a separate file.

## Project layout

```
src/ExfilZoneTracker/
  Program.cs           entry point, main loop, clean shutdown
  OverlayManager.cs    OpenVR init, overlay lifecycle, SteamVR event pump
  WristAttachment.cs   controller discovery + offset matrix (wrist attach)
  PanelRenderer.cs     GDI+ → RGBA buffer for SetOverlayRaw
  AppConfig.cs         config model + load/create logic
  OpenVR/              vendored Valve C# binding + win64 openvr_api.dll
data/items_database.json   editable game-item reference (future milestone)
```

## Troubleshooting

- **“SteamVR is not running…”** — start SteamVR first, then the tracker.
- **Panel not visible** — is the right (configured) controller on and tracked?
  The panel hides while the controller is missing; watch the console. Also
  rotate your wrist as if checking a watch — with default offsets the panel
  faces up from your forearm.
- **Panel colors look swapped (blue/orange tint)** — open an issue; the channel
  order fix is a one-liner in `PanelRenderer.ToRgba`.
- **Panel too big or in the way** — lower `WidthMeters`, increase `Z`.

## License

[MIT](LICENSE). Vendored OpenVR files are © Valve Corporation, BSD-3-Clause —
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
