#!/usr/bin/env python3
"""Regenerates data/items_database.json and data/icons/ from exfil-zone-assistant.

Source: https://github.com/zelengeo/exfil-zone-assistant (MIT) - community item
data and images for Contractors Showdown: ExfilZone. Run after game patches to
pick up new items, then commit the changed files.

Requires: python3, curl; icon conversion uses macOS `sips` (webp -> png).
Usage: python3 tools/import_assistant_data.py
"""
import concurrent.futures
import json
import pathlib
import subprocess
import sys
import urllib.request

RAW = "https://raw.githubusercontent.com/zelengeo/exfil-zone-assistant/master/public"
CATEGORY_FILES = [
    "ammunition", "armor", "attachments", "backpacks", "face-shields",
    "grenades", "helmets", "holsters", "keys", "magazines", "medical",
    "misc", "provisions", "task-items", "weapons",
]
ICON_SIZE = "96"

ROOT = pathlib.Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
ICONS = DATA / "icons"


def fetch_json(name: str):
    with urllib.request.urlopen(f"{RAW}/data/{name}.json", timeout=30) as response:
        return json.load(response)


def icon_target(source_path: str) -> str:
    # "/images/items/task/x.webp" -> "icons/task/x.png"
    parts = source_path.removeprefix("/images/items/").lstrip("/")
    return "icons/" + str(pathlib.PurePosixPath(parts).with_suffix(".png"))


def convert_icon(source_path: str) -> bool:
    target = DATA / icon_target(source_path)
    if target.exists():
        return True
    target.parent.mkdir(parents=True, exist_ok=True)
    webp = target.with_suffix(".webp.tmp")
    try:
        with urllib.request.urlopen(f"{RAW}{source_path}", timeout=30) as response:
            webp.write_bytes(response.read())
        result = subprocess.run(
            ["sips", "-Z", ICON_SIZE, "-s", "format", "png", str(webp), "--out", str(target)],
            capture_output=True,
        )
        return result.returncode == 0 and target.exists()
    except Exception as ex:
        print(f"  icon failed: {source_path}: {ex}", file=sys.stderr)
        return False
    finally:
        webp.unlink(missing_ok=True)


def main() -> None:
    items, icons, seen = [], {}, set()
    for name in CATEGORY_FILES:
        for raw in fetch_json(name):
            item_id = raw.get("id", "").strip()
            if not item_id or item_id in seen:
                if item_id:
                    print(f"  duplicate id skipped: {item_id}", file=sys.stderr)
                continue
            seen.add(item_id)
            icon_src = (raw.get("images") or {}).get("icon")
            entry = {
                "id": item_id,
                "name": raw.get("name", item_id),
                "category": raw.get("category", name),
                "icon": icon_target(icon_src) if icon_src else None,
                "note": raw.get("subcategory") or None,
            }
            items.append(entry)
            if icon_src:
                icons[icon_src] = True
        print(f"{name}: total {len(items)} items so far")

    print(f"Downloading + converting {len(icons)} icons to {ICONS} ...")
    ok = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        for success in pool.map(convert_icon, icons):
            ok += 1 if success else 0
    print(f"Icons ready: {ok}/{len(icons)}")

    database = {
        "schemaVersion": 1,
        "game": "Contractors Showdown: ExfilZone",
        "credit": "Item data and images from https://github.com/zelengeo/exfil-zone-assistant (MIT)",
        "items": items,
    }
    out = DATA / "items_database.json"
    out.write_text(json.dumps(database, indent=2, ensure_ascii=False) + "\n")
    print(f"Wrote {out} with {len(items)} items")


if __name__ == "__main__":
    main()
