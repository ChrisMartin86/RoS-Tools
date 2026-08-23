#!/usr/bin/env python3
"""Decide whether a freshly exported GuildData.lua is worth publishing.

The exporter stamps a new ``generated_at`` on every run, so a plain file
diff always reports a change. This compares only the item level table, and
refuses to publish an export that looks like an API failure rather than a
real roster change.

Usage
-----
    python compare_guild_data.py --new NEW.lua [--old OLD.lua]

Exit codes
----------
    0   the item levels changed -- publish
    1   nothing changed -- skip the commit
    2   the new export looks wrong -- do not publish, fail loudly

Exit code 2 is the important one. A revoked API key, a renamed guild, or a
Blizzard outage can all produce a syntactically valid export with most of
the roster missing. Publishing that would wipe everyone's data.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# A roster this much smaller than the last one is treated as an API problem,
# not as members leaving. Guilds do shrink, but not by a third overnight.
SHRINK_TOLERANCE = 0.66

# Below this, percentage guards are meaningless -- a tiny guild can legitimately
# lose several members at once.
SMALL_ROSTER = 20

ENTRY_PATTERN = re.compile(r'\["([^"]+)"\]\s*=\s*(\d+)')
META_PATTERN = re.compile(r'(\w+)\s*=\s*"([^"]*)"')


def parse_entries(path: Path) -> dict[str, int]:
    """Pull the Name-realm -> ilvl mapping out of a generated Lua file."""
    text = path.read_text(encoding="utf-8")
    return {name: int(ilvl) for name, ilvl in ENTRY_PATTERN.findall(text)}


def parse_meta(path: Path) -> dict[str, str]:
    text = path.read_text(encoding="utf-8")
    start = text.find("meta")
    end = text.find("ilvls")
    if start == -1 or end == -1 or end < start:
        return {}
    return dict(META_PATTERN.findall(text[start:end]))


def describe_changes(old: dict[str, int], new: dict[str, int]) -> list[str]:
    added = sorted(set(new) - set(old))
    removed = sorted(set(old) - set(new))
    changed = sorted(k for k in set(old) & set(new) if old[k] != new[k])

    lines: list[str] = []

    if changed:
        gained = [k for k in changed if new[k] > old[k]]
        lines.append(f"{len(changed)} item level change(s), {len(gained)} upward")
        for key in changed[:10]:
            direction = "+" if new[key] > old[key] else ""
            delta = new[key] - old[key]
            lines.append(f"    {key}: {old[key]} -> {new[key]} ({direction}{delta})")
        if len(changed) > 10:
            lines.append(f"    ... and {len(changed) - 10} more")

    if added:
        lines.append(f"{len(added)} character(s) added")
        for key in added[:10]:
            lines.append(f"    + {key} ({new[key]})")
        if len(added) > 10:
            lines.append(f"    ... and {len(added) - 10} more")

    if removed:
        lines.append(f"{len(removed)} character(s) removed")
        for key in removed[:10]:
            lines.append(f"    - {key} ({old[key]})")
        if len(removed) > 10:
            lines.append(f"    ... and {len(removed) - 10} more")

    return lines


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--new", type=Path, required=True,
                        help="Freshly generated file")
    parser.add_argument("--old", type=Path,
                        help="Currently published file. Absent means first run.")
    parser.add_argument("--summary", type=Path,
                        help="Write a one-line commit message here")
    args = parser.parse_args()

    if not args.new.exists():
        print(f"error: {args.new} does not exist", file=sys.stderr)
        return 2

    new_entries = parse_entries(args.new)

    if not new_entries:
        print("error: new export contains no characters at all", file=sys.stderr)
        return 2

    meta = parse_meta(args.new)
    guild = meta.get("guild", "?")
    realm = meta.get("realm", "?")
    print(f"New export: {len(new_entries)} characters for {guild} on {realm}")

    if args.old is None or not args.old.exists():
        print("No published file to compare against -- treating as first publish.")
        if args.summary:
            args.summary.write_text(
                f"Publish guild data ({len(new_entries)} characters)\n",
                encoding="utf-8",
            )
        return 0

    old_entries = parse_entries(args.old)
    print(f"Published:  {len(old_entries)} characters")

    if not old_entries:
        print("Published file has no entries; publishing the new one.")
        if args.summary:
            args.summary.write_text(
                f"Publish guild data ({len(new_entries)} characters)\n",
                encoding="utf-8",
            )
        return 0

    # Guard: a sharp drop is far more likely an API problem than reality.
    if len(old_entries) >= SMALL_ROSTER:
        floor = int(len(old_entries) * SHRINK_TOLERANCE)
        if len(new_entries) < floor:
            print(
                f"error: roster dropped from {len(old_entries)} to "
                f"{len(new_entries)} characters (floor is {floor}). "
                "This usually means an API failure or a guild/realm typo, "
                "not real departures. Refusing to publish.",
                file=sys.stderr,
            )
            return 2

    if new_entries == old_entries:
        print("No item level changes. Nothing to publish.")
        return 1

    for line in describe_changes(old_entries, new_entries):
        print(line)

    if args.summary:
        added = len(set(new_entries) - set(old_entries))
        removed = len(set(old_entries) - set(new_entries))
        changed = len([k for k in set(old_entries) & set(new_entries)
                       if old_entries[k] != new_entries[k]])

        parts = []
        if changed:
            parts.append(f"{changed} ilvl change{'s' if changed != 1 else ''}")
        if added:
            parts.append(f"{added} added")
        if removed:
            parts.append(f"{removed} removed")

        args.summary.write_text(
            f"Update guild data ({', '.join(parts)})\n", encoding="utf-8"
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
