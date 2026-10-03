#!/usr/bin/env python3
"""Regenerates data/items_database.json and data/icons/ from two local checkouts.

Item data (ids, names, categories, Russian names) comes from the accepted database
baseline of https://github.com/zaymax/contractors-data - tables extracted from the
game files, under pipeline/references/database-baseline/snapshot/. That repository
ships no images, so icons come from the item pages of
https://github.com/zaymax/exfil-zone-assistant (public/images/items/*.webp, MIT),
matched to the game data by game id, then by name, then by the icon asset name.

Usage:
  python3 tools/import_contractors_data.py \
      --data ../contractors-data --images ../exfil-zone-assistant

Requires python3 and one of: ImageMagick `convert`/`magick`, macOS `sips`, or `ffmpeg`
(webp -> 96 px png). Run after the data repo accepts a new baseline, then commit
the changed files; the app hot-reloads them.
"""
import argparse
import csv
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
DATA = ROOT / "data"
DATABASE_JSON = DATA / "items_database.json"
ICONS = DATA / "icons"
ICON_SIZE = 96

# Order of the category browser lives in the app (ItemDatabase.CategoryPriority);
# these are the category keys the app and this script agree on.
LOOT_CATEGORY = {
    "key": "keys",
    "quest_item": "task-items",
    "valuable": "valuables",
    "resource": "resources",
    "medicine": "medicine",
    "food": "provisions",
}

# Which exfil-zone-assistant files may supply an icon for a category (name matches
# outside the right family produce wrong pictures, e.g. a magazine for a gun).
FORK_FILES = {
    "keys": ["keys.json"],
    "task-items": ["task-items.json", "misc.json"],
    "valuables": ["misc.json", "task-items.json", "paints.json"],
    "resources": ["misc.json", "task-items.json"],
    "medicine": ["medical.json"],
    "provisions": ["provisions.json"],
    "weapons": ["weapons.json"],
    "attachments": ["attachments.json", "gunsmith-parts.json"],
    "magazines": ["magazines.json", "gunsmith-parts.json"],
    "gun-parts": ["gunsmith-parts.json", "attachments.json", "magazines.json"],
    "ammo": ["ammunition.json"],
    "armor": ["armor.json"],
    "helmets": ["helmets.json"],
    "face-shields": ["face-shields.json"],
    "backpacks": ["backpacks.json"],
    "holsters": ["holsters.json"],
    "containers": ["containers.json"],
    "grenades": ["grenades.json"],
}


def read_csv(path: pathlib.Path):
    with path.open(encoding="utf-8-sig", newline="") as handle:
        return list(csv.DictReader(handle))


def norm(text) -> str:
    return (text or "").strip().lower()


def safe_name(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "_", text.lower()).strip("_") or "item"


def git_commit(repo: pathlib.Path) -> str:
    try:
        return subprocess.run(["git", "-C", str(repo), "rev-parse", "HEAD"],
                              capture_output=True, text=True, check=True).stdout.strip()
    except Exception:
        return "unknown"


# ---- icon source: exfil-zone-assistant -------------------------------------------------

class ForkIndex:
    """Images of exfil-zone-assistant, reachable by game id, by name and by file stem."""

    def __init__(self, fork: pathlib.Path):
        self.public = fork / "public"
        self.by_game_id = {}
        self.by_name = {}      # (file, name) -> entry
        self.by_entry_id = {}  # entry id -> entry (the old XiloOVR ids used this scheme)
        self.by_stem = {}
        data_dir = self.public / "data"
        for file in sorted(data_dir.glob("*.json")):
            try:
                entries = json.loads(file.read_text(encoding="utf-8"))
            except ValueError:
                continue
            if not isinstance(entries, list):
                continue
            for entry in entries:
                if not isinstance(entry, dict) or "images" not in entry:
                    continue
                icon = (entry.get("images") or {}).get("icon")
                if not icon or not (self.public / icon.lstrip("/")).exists():
                    continue
                entry["_file"] = file.name
                entry["_path"] = self.public / icon.lstrip("/")
                if entry.get("gameId"):
                    self.by_game_id.setdefault(norm(entry["gameId"]), entry)
                self.by_name.setdefault((file.name, norm(entry.get("name"))), entry)
                self.by_entry_id.setdefault(norm(entry.get("id")), entry)
        for path in (self.public / "images" / "items").rglob("*"):
            if path.is_file():
                self.by_stem.setdefault(path.stem.lower(), path)

    def find(self, item_id: str, name: str, category: str, icon_names):
        """Returns (image path, fork entry or None, how)."""
        entry = self.by_game_id.get(norm(item_id))
        if entry:
            return entry["_path"], entry, "game-id"
        for file in FORK_FILES.get(category, []):
            entry = self.by_name.get((file, norm(name)))
            if entry:
                return entry["_path"], entry, "name"
        for stem in list(icon_names) + derived_stems(item_id):
            path = self.by_stem.get(norm(stem))
            if path:
                return path, None, "icon-name"
        # Grenades are named differently in the two sources ("F-1 Hand grenade" vs
        # "F1 Fragmentation Grenade"); the model token is unique per file there.
        if category == "grenades":
            token = re.sub(r"[^a-z0-9]", "", norm(name).split(" ")[0])
            for file in FORK_FILES[category]:
                hits = [e for (f, n), e in self.by_name.items()
                        if f == file and token and re.sub(r"[^a-z0-9]", "", n.split(" ")[0]) == token]
                if len(hits) == 1:
                    return hits[0]["_path"], hits[0], "name-token"
        return None, None, None


def derived_stems(item_id: str):
    """Image stems the fork tends to use for a game id: 'valuable.task.x' -> 'valuable_task_x'."""
    flat = re.sub(r"[^a-z0-9]+", "_", item_id.lower()).strip("_")
    last = flat.rsplit("_", 1)[-1]
    return [flat, "icon_" + flat, "icon_valuable_" + last, "icon_valuableitem_" + last]


# ---- item data: contractors-data ------------------------------------------------------

def build_items(snapshot: pathlib.Path):
    items = []
    seen = set()

    def add(item_id, name, category, name_ru="", note=None, icon_names=()):
        item_id = (item_id or "").strip()
        name = (name or "").strip()
        if not item_id or not name or norm(item_id) in seen:
            return
        seen.add(norm(item_id))
        items.append({
            "id": item_id,
            "name": name,
            "nameRu": (name_ru or "").strip() or None,
            "category": category,
            "note": (note or "").strip() or None,
            "_icon_names": [n for n in icon_names if n],
        })

    # Icon asset names per item id, from every table that records them.
    icon_names = {}

    def remember_icons(item_id, *names):
        if item_id:
            icon_names.setdefault(norm(item_id), []).extend(n for n in names if n)

    for row in read_csv(snapshot / "items/catalog-icon-links.csv"):
        for item_id in row["item_ids"].split(";"):
            remember_icons(item_id, row["icon_object"])
    for table in ("inventory/valuables.csv", "inventory/keys.csv"):
        for row in read_csv(snapshot / table):
            remember_icons(row.get("sell_id"), row.get("icon_name"), row.get("icon_texture_name"), row.get("asset_stem"))
    for row in read_csv(snapshot / "items/catalog-item-links.csv"):
        remember_icons(row.get("item_id"), row.get("catalog_id"))

    # 1. Loot: keys, quest items, valuables, resources, medicine, food (with Russian names).
    for row in read_csv(snapshot / "items/items.csv"):
        category = LOOT_CATEGORY.get(row["category"], "valuables")
        hideout = row.get("hideout_required_quantity") or "0"
        note = f"hideout needs {hideout}" if hideout not in ("", "0") else None
        add(row["item_id"], row["name_en"], category, row["name_ru"], note,
            icon_names.get(norm(row["item_id"]), []))

    # 2. Weapons (gun presets).
    for row in read_csv(snapshot / "weapons/guns.csv"):
        add(row["sell_id"] or row["asset_stem"], row["display_name"], "weapons",
            note=row.get("display_caliber") or None,
            icon_names=(row.get("icon_texture_name"), row.get("icon_name"), row.get("asset_stem")))

    # 3. Attachments (sights, muzzles, grips, rails).
    for row in read_csv(snapshot / "weapons/attachments.csv"):
        if row.get("is_base") == "true":
            continue
        add(row["sell_id"] or row["asset_stem"], row["display_name"], "attachments",
            note=row.get("subcategory") or row.get("category"),
            icon_names=(row.get("icon_name"), row.get("asset_stem")))

    # 4. Magazines: names live in the string table.
    names = {}
    for row in read_csv(snapshot / "reference/source/item_names.csv"):
        if row["field"] == "name":
            names.setdefault(norm(row["sell_id"]), row["text"])
    for row in read_csv(snapshot / "weapons/magazines.csv"):
        name = names.get(norm(row["sell_id"]))
        caliber = row.get("caliber", "").replace("EBulletCaliber_", "")
        add(row["sell_id"], name, "magazines", note=caliber or None,
            icon_names=(pathlib.PurePosixPath(row.get("source_asset", "")).stem,))

    # 5. Gun parts (barrels, stocks, handguards, receivers...).
    for row in read_csv(snapshot / "weapons/gun_parts.csv"):
        part = (row.get("part_types") or "").split(";")[0].replace("EGunSmithPartType_", "").strip()
        add(row["sell_id"], row["display_name"], "gun-parts", note=part or None,
            icon_names=(row.get("icon_name"), row.get("asset_stem")))

    # 6. Ammunition.
    for row in read_csv(snapshot / "ammunition/ammunition_identity.csv"):
        key = row.get("name_key") or row["asset_stem"]
        item_id = key[:-5] if key.endswith("_name") else key
        add(item_id, row["display_name"], "ammo", note=row.get("caliber_token") or None,
            icon_names=(row.get("icon_name"), row.get("asset_stem")))

    # 7. Protection and carried gear.
    for table, category in (
        ("protection/armor.csv", "armor"),
        ("protection/helmets.csv", "helmets"),
        ("protection/face_shields.csv", "face-shields"),
        ("inventory/backpacks.csv", "backpacks"),
        ("inventory/holsters.csv", "holsters"),
        ("inventory/containers.csv", "containers"),
        ("consumables/throwables.csv", "grenades"),
    ):
        for row in read_csv(snapshot / table):
            if row.get("is_base") == "true":
                continue
            add(row.get("sell_id") or row["asset_stem"], row["display_name"], category,
                note=row.get("subcategory") or row.get("category") or None,
                icon_names=(row.get("icon_name"), row.get("icon_texture_name"), row.get("asset_stem")))

    return items


# ---- icons --------------------------------------------------------------------------

def find_converter():
    for tool in ("magick", "convert"):
        if shutil.which(tool):
            return tool
    for tool in ("sips", "ffmpeg"):
        if shutil.which(tool):
            return tool
    return None


def convert_icon(tool: str, source: pathlib.Path, target: pathlib.Path) -> bool:
    target.parent.mkdir(parents=True, exist_ok=True)
    if source.suffix.lower() == ".png" and source.stat().st_size < 60_000:
        shutil.copyfile(source, target)  # already one of ours
        return True
    size = f"{ICON_SIZE}x{ICON_SIZE}>"
    if tool in ("magick", "convert"):
        command = [tool, str(source), "-resize", size, str(target)]
    elif tool == "sips":
        command = ["sips", "-Z", str(ICON_SIZE), "-s", "format", "png", str(source), "--out", str(target)]
    else:
        command = ["ffmpeg", "-loglevel", "error", "-y", "-i", str(source),
                   "-vf", f"scale='min({ICON_SIZE},iw)':'min({ICON_SIZE},ih)':force_original_aspect_ratio=decrease",
                   str(target)]
    result = subprocess.run(command, capture_output=True)
    return result.returncode == 0 and target.exists()


# ---- main ---------------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", default=str(ROOT.parent / "contractors-data"), help="contractors-data checkout")
    parser.add_argument("--images", default=str(ROOT.parent / "exfil-zone-assistant"), help="exfil-zone-assistant checkout")
    args = parser.parse_args()

    data_repo = pathlib.Path(args.data).expanduser().resolve()
    fork_repo = pathlib.Path(args.images).expanduser().resolve()
    snapshot = data_repo / "pipeline/references/database-baseline/snapshot"
    if not (snapshot / "items/items.csv").exists():
        print(f"error: {snapshot} does not look like the contractors-data baseline", file=sys.stderr)
        return 2
    if not (fork_repo / "public/data").exists():
        print(f"error: {fork_repo} does not look like an exfil-zone-assistant checkout", file=sys.stderr)
        return 2
    tool = find_converter()
    if not tool:
        print("error: need ImageMagick (convert/magick), sips or ffmpeg to convert icons", file=sys.stderr)
        return 2

    metadata = json.loads((data_repo / "pipeline/references/database-baseline/metadata.json").read_text(encoding="utf-8"))
    fork = ForkIndex(fork_repo)
    items = build_items(snapshot)

    # Previous database: keeps old checklist ids resolvable (legacyIds) and old icons usable.
    old_items = []
    old_icons = {}
    if DATABASE_JSON.exists():
        try:
            old_items = json.loads(DATABASE_JSON.read_text(encoding="utf-8")).get("items", [])
        except ValueError:
            pass
    if ICONS.exists():
        for path in ICONS.rglob("*.png"):
            old_icons.setdefault(path.stem.lower(), path)

    # Resolve icons into a fresh directory, then swap it in.
    staging = pathlib.Path(tempfile.mkdtemp(prefix="xiloovr-icons-", dir=str(DATA)))
    how_counts = {}
    missing = []
    fork_entry_for = {}
    for item in items:
        source, entry, how = fork.find(item["id"], item["name"], item["category"], item["_icon_names"])
        if source is None:
            for icon_name in item["_icon_names"]:
                if norm(icon_name) in old_icons:
                    source, how = old_icons[norm(icon_name)], "previous-icon"
                    break
        item["icon"] = None
        item["_source_stem"] = source.stem.lower() if source is not None else None
        if source is not None:
            relative = f"icons/{item['category']}/{safe_name(item['id'])}.png"
            if convert_icon(tool, source, staging / relative[len("icons/"):]):
                item["icon"] = relative
                how_counts[how] = how_counts.get(how, 0) + 1
            else:
                print(f"  icon conversion failed: {source}", file=sys.stderr)
        if item["icon"] is None:
            missing.append(f"{item['category']}: {item['name']} ({item['id']})")
        if entry is not None:
            fork_entry_for[item["id"]] = entry
            if item["note"] is None and entry.get("subcategory"):
                item["note"] = str(entry["subcategory"])

    # Legacy ids: old XiloOVR ids were exfil-zone-assistant ids, so match through the
    # fork entry that supplied the icon first, then by exact name.
    by_fork_entry = {norm(entry.get("id")): item for item, entry in
                     ((i, fork_entry_for.get(i["id"])) for i in items) if entry}
    by_name = {}
    by_source_stem = {}
    for item in items:
        by_name.setdefault(norm(item["name"]), item)
        if item["_source_stem"]:
            by_source_stem.setdefault(item["_source_stem"], item)
    # The old database reused one picture for several items, so an old icon stem only
    # identifies an item when exactly one old item used it.
    old_stem_users = {}
    for old in old_items:
        stem = pathlib.PurePosixPath(old.get("icon") or "").stem.lower()
        if stem:
            old_stem_users.setdefault(stem, []).append(old)
    legacy_mapped = 0
    new_ids = {norm(i["id"]) for i in items}
    for old in old_items:
        old_id = norm(old.get("id"))
        if not old_id or old_id in new_ids:
            continue
        old_stem = pathlib.PurePosixPath(old.get("icon") or "").stem.lower()
        target = by_fork_entry.get(old_id)
        if target is None and old_stem and len(old_stem_users.get(old_stem, [])) == 1:
            target = by_source_stem.get(old_stem)
        if target is None:
            target = by_name.get(norm(old.get("name")))
        if target is None:
            continue
        legacy = target.setdefault("legacyIds", [])
        if old["id"] not in legacy:
            legacy.append(old["id"])
            legacy_mapped += 1

    if ICONS.exists():
        shutil.rmtree(ICONS)
    staging.rename(ICONS)

    for item in items:
        item.pop("_icon_names", None)
        item.pop("_source_stem", None)
    items.sort(key=lambda i: (i["category"], i["name"].lower()))

    database = {
        "schemaVersion": 2,
        "game": "Contractors Showdown: ExfilZone",
        "credit": "Item data from https://github.com/zaymax/contractors-data (extracted from the game files); "
                  "icons from https://github.com/zaymax/exfil-zone-assistant (MIT)",
        "source": {
            "data": {
                "repository": "https://github.com/zaymax/contractors-data",
                "commit": git_commit(data_repo),
                "baselineId": metadata.get("baseline_id"),
                "build": metadata.get("build"),
                "snapshot": metadata.get("snapshot"),
            },
            "images": {
                "repository": "https://github.com/zaymax/exfil-zone-assistant",
                "commit": git_commit(fork_repo),
            },
        },
        "items": items,
    }
    DATABASE_JSON.write_text(json.dumps(database, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    categories = {}
    for item in items:
        counts = categories.setdefault(item["category"], [0, 0])
        counts[0] += 1
        counts[1] += 1 if item["icon"] else 0
    print(f"Wrote {len(items)} items to {DATABASE_JSON}")
    for category, (total, with_icon) in sorted(categories.items()):
        print(f"  {category:14s} {total:4d} items, {with_icon:4d} with icon")
    print(f"Icons by source: {how_counts}")
    print(f"Legacy ids mapped: {legacy_mapped} of {len(old_items)} previous items")
    if missing:
        print(f"{len(missing)} items without an icon (shown as text tiles):")
        for line in missing[:40]:
            print(f"  {line}")
        if len(missing) > 40:
            print(f"  ... and {len(missing) - 40} more")
    return 0


if __name__ == "__main__":
    sys.exit(main())
