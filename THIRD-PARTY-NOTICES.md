# Third-party notices

## contractors-data (zaymax)

The item database (`data/items_database.json`: ids, English and Russian names,
categories) is generated from the accepted database baseline of
[contractors-data](https://github.com/zaymax/contractors-data), tables extracted
from the game files of Contractors Showdown: ExfilZone. The source commit and
baseline id are recorded in the JSON under `source`.

## exfil-zone-assistant (zaymax fork of zelengeo/exfil-zone-assistant)

Item icons (`data/icons/`) are taken from the item images of
[exfil-zone-assistant](https://github.com/zaymax/exfil-zone-assistant), a
community companion app distributed under the MIT license, downscaled and
converted to PNG by `tools/import_contractors_data.py`. Item images depict
in-game assets of Contractors Showdown: ExfilZone (Caveman Studio).

## OpenVR SDK (Valve Corporation)

This repository vendors two files from the [OpenVR SDK](https://github.com/ValveSoftware/openvr),
pinned at tag `v2.5.1`:

- `src/XiloOVR/OpenVR/openvr_api.cs` — official C# binding
- `src/XiloOVR/OpenVR/openvr_api.dll` — native runtime library (win64)

Both are Copyright (c) 2015 Valve Corporation and distributed under the
BSD-3-Clause license. The full license text is included verbatim at
`src/XiloOVR/OpenVR/LICENSE-OpenVR.txt`.
