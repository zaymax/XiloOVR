# Third-party notices

## exfil-zone-assistant (zelengeo)

The item database (`data/items_database.json`) and item icons (`data/icons/`)
are generated from [exfil-zone-assistant](https://github.com/zelengeo/exfil-zone-assistant),
a community companion app for Contractors Showdown: ExfilZone, distributed
under the MIT license. Icons are downscaled and converted to PNG by
`tools/import_assistant_data.py`.

## OpenVR SDK (Valve Corporation)

This repository vendors two files from the [OpenVR SDK](https://github.com/ValveSoftware/openvr),
pinned at tag `v2.5.1`:

- `src/ExfilZoneTracker/OpenVR/openvr_api.cs` — official C# binding
- `src/ExfilZoneTracker/OpenVR/openvr_api.dll` — native runtime library (win64)

Both are Copyright (c) 2015 Valve Corporation and distributed under the
BSD-3-Clause license. The full license text is included verbatim at
`src/ExfilZoneTracker/OpenVR/LICENSE-OpenVR.txt`.
