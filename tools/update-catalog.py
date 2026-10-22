#!/usr/bin/env python3
"""Keep src/Content/acr-content.json equal to the community content catalog.

The app embeds the catalog's data.json unmodified (CC BY 4.0, see the README):
https://github.com/dminotte/assetto-corsa-rally-catalog

    python tools/update-catalog.py          copy the published data.json over ours
    python tools/update-catalog.py --check  only compare (exit 1 when they differ)

Either way the names that change are listed: `name` is what the app shows.
Exit codes: 0 same or updated, 1 differs (--check), 2 cannot fetch or read it.
Standard library only.
"""

import argparse
import json
import sys
import urllib.request
from pathlib import Path

URL = "https://raw.githubusercontent.com/dminotte/assetto-corsa-rally-catalog/main/data.json"
SCHEMA = 3   # the schema ContentCatalog.cs reads
LOCAL = Path(__file__).resolve().parent.parent / "src" / "Content" / "acr-content.json"


def fetch():
    with urllib.request.urlopen(URL, timeout=30) as response:
        return response.read()


def changes(old, new):
    """Lines describing what the app would show differently: names, and ids
    added or removed, per list of the catalog."""
    lines = []
    for key, items in new.items():
        if not isinstance(items, list):
            if old.get(key) != items:
                lines.append(f"{key}: {old.get(key)!r} -> {items!r}")
            continue
        before = {i.get("id"): i for i in old.get(key, []) if isinstance(i, dict)}
        after = {i.get("id"): i for i in items if isinstance(i, dict)}
        for id_ in after.keys() - before.keys():
            lines.append(f"{key}/{id_}: added ({after[id_].get('name')!r})")
        for id_ in before.keys() - after.keys():
            lines.append(f"{key}/{id_}: removed")
        for id_ in after.keys() & before.keys():
            if after[id_].get("name") != before[id_].get("name"):
                lines.append(f"{key}/{id_}: {before[id_].get('name')!r} -> {after[id_].get('name')!r}")
    for key in old.keys() - new.keys():
        lines.append(f"{key}: removed")
    return sorted(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="compare only, write nothing")
    args = parser.parse_args()

    try:
        published = fetch().replace(b"\r\n", b"\n")   # the repo keeps LF
        catalog = json.loads(published.decode("utf-8"))
    except (OSError, ValueError) as error:
        print(f"Cannot read {URL}: {error}", file=sys.stderr)
        return 2
    if catalog.get("schemaVersion") != SCHEMA:
        print(f"The published catalog is schema {catalog.get('schemaVersion')!r}; the app reads schema {SCHEMA}. "
              "Update ContentCatalog.cs before the catalog.", file=sys.stderr)
        return 2

    ours = LOCAL.read_bytes()
    if ours == published:
        print("The content catalog is up to date.")
        return 0

    try:
        old = json.loads(ours.decode("utf-8"))
    except ValueError:
        old = {}
    diff = changes(old, catalog)
    print("The published content catalog differs from src/Content/acr-content.json.")
    print("\n".join(f"  {line}" for line in diff) if diff else "  (no name changes: labels, aliases or other fields only)")
    if args.check:
        print("Run `python tools/update-catalog.py` to update it.")
        return 1

    LOCAL.write_bytes(published)
    print(f"Updated {LOCAL.relative_to(LOCAL.parent.parent.parent)}. Run the tests: some check names.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
