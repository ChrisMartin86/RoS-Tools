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
    2   the new export looks wrong, OR this script crashed -- do not publish

Exit code 2 is the important one. A revoked API key, a renamed guild, a
wrong --region, or a Blizzard outage can all produce a syntactically valid
export that is either mostly empty or a different guild entirely. Publishing
that would wipe everyone's data.

Exit 1 means one specific thing -- nothing changed -- and nothing else. An
unexpected exception is exit 2, because the caller maps 1 to a green run.
"""

from __future__ import annotations

import argparse
import re
import sys
import traceback
from pathlib import Path

# A roster this much smaller than the last one is treated as an API problem,
# not as members leaving. Guilds do shrink, but not by a third overnight.
SHRINK_TOLERANCE = 0.66

# At least this share of the previously published roster must still be present.
# A count-only guard cannot tell a real roster from a DIFFERENT one of the same
# size: a workflow_dispatch run with the wrong region, or a guild typo that
# resolves to a real guild elsewhere, produces ~200 valid but entirely foreign
# characters and passes every count check. 0.5 is deliberately looser than
# SHRINK_TOLERANCE, because a guild merge can legitimately churn a lot of names
# at once while a wrong-guild export overlaps by essentially nothing.
OVERLAP_FLOOR = 0.5

# Below this, percentage guards are meaningless -- a tiny guild can legitimately
# lose several members at once.
SMALL_ROSTER = 20

# The export is only the same roster if it describes the same roster. These come
# straight from the meta block the exporter writes.
IDENTITY_FIELDS = ("region", "realm", "guild")

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


def identity_mismatches(old_meta: dict[str, str],
                        new_meta: dict[str, str]) -> list[tuple[str, str, str]]:
    """Which of region/realm/guild describe a different roster than before.

    A field missing from the PUBLISHED side is not a mismatch: an export
    predating a field would otherwise block every publish, which is the only
    reason this tolerates anything at all.

    A field missing from the NEW side is a different story and used to be waved
    through by the same ``was and now`` test. The current exporter always writes
    all three and refuses an empty value, so a new export that has lost one is
    truncated or corrupt -- exactly the "syntactically valid but wrong" input
    this script exists to catch, and the one direction where the guard silently
    disabled itself.
    """
    out = []
    for field in IDENTITY_FIELDS:
        was = old_meta.get(field)
        now = new_meta.get(field)
        if not was:
            continue
        if not now:
            out.append((field, was, "(absent)"))
        elif was != now:
            out.append((field, was, now))
    return out


def overlap_ratio(old: dict[str, int], new: dict[str, int]) -> float:
    """How much of the published roster is still present in the new export."""
    if not old:
        return 1.0
    return len(set(old) & set(new)) / len(old)


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

    # Guard: the export must describe the SAME guild. The meta block used to be
    # parsed only so it could be printed, which left a wrong --region or a guild
    # typo that resolves to a real guild elsewhere entirely unguarded: it yields
    # a valid export of the right size and publishes a foreign roster to
    # CurseForge, which is precisely what the module docstring claims to stop.
    old_meta = parse_meta(args.old)
    mismatches = identity_mismatches(old_meta, meta)
    if mismatches:
        print(
            "error: the new export describes a different roster than the published one:",
            file=sys.stderr,
        )
        for field, was, now in mismatches:
            print(f"    {field}: published '{was}' -> new '{now}'", file=sys.stderr)
        print(
            "A changed region/realm/guild means a wrong --region, a guild typo that "
            "resolved to a real guild elsewhere, or a deliberate move. Refusing to "
            "publish. If the move is deliberate, delete the published file so this "
            "runs as a first publish.",
            file=sys.stderr,
        )
        return 2

    # Guard: a wholesale replacement, at ANY roster size. SMALL_ROSTER exempts
    # the percentage guards below because a ten-person guild can legitimately
    # lose several members at once -- but it cannot legitimately lose ALL of
    # them and gain ten strangers in the same run. That is a wrong region, realm
    # or guild whose meta happens to agree (a re-used workflow input, or an
    # export crossed with another guild's), and the small-roster exemption let
    # it publish: ten published characters replaced by ten entirely different
    # ones exited 0. Zero survivors is not a membership change at any size.
    if not (set(old_entries) & set(new_entries)):
        print(
            f"error: not one of the {len(old_entries)} published characters appears "
            f"in the new export's {len(new_entries)}. A roster does not turn over "
            "completely in one run: this is a different guild, realm or region, or "
            "an export that was built from the wrong input. Refusing to publish. "
            "If the guild really did re-form, delete the published file so this "
            "runs as a first publish.",
            file=sys.stderr,
        )
        return 2

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

        # Guard: same size, different people. The count guards cannot see this.
        overlap = overlap_ratio(old_entries, new_entries)
        if overlap < OVERLAP_FLOOR:
            kept = len(set(old_entries) & set(new_entries))
            print(
                f"error: only {kept} of {len(old_entries)} published characters "
                f"({overlap:.0%}) appear in the new export, below the {OVERLAP_FLOOR:.0%} "
                "floor. The roster is a similar size but largely different people, "
                "which means a different guild, not a real membership change. "
                "Refusing to publish.",
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


def run() -> int:
    """main() with a backstop that keeps a crash out of the exit-1 lane.

    Exit 1 means "nothing changed", which guild-data.yml maps to publish=false
    and a green run. An uncaught exception used to exit 1 too, so a
    UnicodeDecodeError on a corrupted old.lua printed "No item level changes;
    nothing published" and the data quietly stopped updating. Anything
    unexpected is exit 2 -- do not publish, fail loudly.
    """
    try:
        return main()
    except SystemExit:
        # argparse already uses SystemExit(2) for a usage error, which is the
        # lane we want anyway.
        raise
    except Exception as exc:  # noqa: BLE001 -- deliberate backstop
        print(f"error: {type(exc).__name__}: {exc}", file=sys.stderr)
        traceback.print_exc()
        print("This is a bug in compare_guild_data.py or a corrupt input file, "
              "not a decision about the roster. Refusing to publish.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(run())
