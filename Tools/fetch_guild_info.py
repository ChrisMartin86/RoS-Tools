#!/usr/bin/env python3
"""Regenerate Data/GuildData.lua from the Blizzard Community API.

Usage
-----
    python fetch_guild_info.py --realm khadgar --guild "Riddle of Steel"

Credentials come from the environment (preferred) or --client-id /
--client-secret:

    BLIZZARD_CLIENT_ID
    BLIZZARD_CLIENT_SECRET

Create an application at https://develop.battle.net/access/clients to get
them. The client credentials flow needs no redirect URI and no user
consent -- it only reads public profile data.
"""

from __future__ import annotations

import argparse
import concurrent.futures as futures
import math
import os
import random
import re
import sys
import time
import unicodedata
from dataclasses import dataclass
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from pathlib import Path

try:
    import requests
except ImportError:  # pragma: no cover
    sys.exit("This script needs 'requests'. Install it with: pip install requests")


API_HOST = "https://{region}.api.blizzard.com"
OAUTH_HOST = "https://oauth.battle.net/token"
DEFAULT_WORKERS = 8
RETRY_STATUS = {429, 500, 502, 503, 504}

# Retry budget for a single request. The old schedule was 2 ** attempt * 0.25,
# which totalled 3.75 s across all five attempts -- less than one Blizzard
# throttle window, so a rate-limited run quietly dropped a fifth of the roster
# and still exited 0. Retry-After wins when the server sends one.
RETRY_ATTEMPTS = 5
BASE_BACKOFF = 1.0
MAX_BACKOFF = 60.0

# A run is aborted when HARD failures -- retries exhausted, or a network error --
# exceed this share of the roster. A 404 is NOT a hard failure: "no profile" is
# normal for a character that has never logged in or has a private profile, and
# has always been tolerated. The floor keeps the percentage from being
# meaningless on a small guild.
HARD_FAILURE_RATE = 0.02
HARD_FAILURE_FLOOR = 3

# Neither the Lua short string in Data/GuildData.lua nor the `H:` sync header in
# Core/Data.lua can carry these. Core/Sync.lua captures the header's fields as
# [^:;]*, so a ':' or ';' in the meta does not produce a broken file -- it
# produces a roster that every peer silently rejects.
FORBIDDEN_META_CHARS = ':;|"\\'


class RetryExhausted(Exception):
    """Every retry for one request was used up.

    Deliberately distinct from a 404. Collapsing the two is what let a
    throttled run publish a partial roster and exit 0.
    """


# ----------------------------------------------------------------------
# Slug helpers -- must stay in sync with Core/Util.lua RealmToSlug()
# ----------------------------------------------------------------------
def slugify(value: str) -> str:
    value = value.replace("\u2019", "").replace("'", "").replace("`", "")
    value = re.sub(r"\s+", "-", value.strip())
    value = re.sub(r"-+", "-", value)
    return value.lower()


def check_meta_value(field: str, value: str) -> None:
    """Reject a meta value the generated file cannot represent.

    slugify() drops apostrophes and collapses whitespace but leaves ``"``,
    ``\\``, ``:`` and ``;`` alone, and guild-data.yml pipes free-form
    workflow_dispatch input straight into realm/guild/region. Escaping these
    through is not enough: ``:`` and ``;`` are the field separators of the
    ``H:`` sync header in Core/Data.lua, which Core/Sync.lua parses with
    ``[^:;]*``, so a roster carrying one is silently rejected by every peer.
    Refuse the value instead, where the error is visible.
    """
    if value is None or value == "":
        raise ValueError(f"--{field} is empty after slugifying. Give a real {field} name.")
    bad = sorted({ch for ch in value if ch in FORBIDDEN_META_CHARS})
    if bad:
        shown = " ".join(repr(ch) for ch in bad)
        raise ValueError(
            f"--{field} contains {shown}, which the generated file cannot carry. "
            f"';' and ':' separate the fields of the addon's sync header, and '\"' "
            f"and '\\' break the Lua string. Got: {value!r}"
        )
    for ch in value:
        if ord(ch) < 0x20 or ord(ch) == 0x7F:
            raise ValueError(
                f"--{field} contains the control character {ch!r}, which cannot appear "
                f"in the generated file. Got: {value!r}"
            )


@dataclass
class Character:
    name: str
    realm_slug: str
    ilvl: int

    @property
    def key(self) -> str:
        return f"{self.name}-{self.realm_slug}"


# ----------------------------------------------------------------------
# Retry timing
# ----------------------------------------------------------------------
def parse_retry_after(value) -> float | None:
    """Seconds from a Retry-After header, which may be a count or an HTTP date."""
    if value is None:
        return None
    text = str(value).strip()
    if not text:
        return None
    try:
        return max(0.0, float(int(text)))
    except ValueError:
        pass
    try:
        when = parsedate_to_datetime(text)
    except (TypeError, ValueError, IndexError):
        return None
    if when is None:
        return None
    if when.tzinfo is None:
        when = when.replace(tzinfo=timezone.utc)
    return max(0.0, (when - datetime.now(timezone.utc)).total_seconds())


def retry_delay(attempt: int, retry_after=None) -> float:
    """How long to wait before retry number ``attempt`` (0-based).

    Blizzard sends Retry-After on a 429 and it used to be ignored entirely.
    Failing that, exponential backoff with full jitter: without the jitter the
    worker threads all back off for identical intervals and come back in a
    single burst that re-trips the very throttle they are waiting out.
    """
    honoured = parse_retry_after(retry_after)
    if honoured is not None:
        return min(honoured, MAX_BACKOFF)
    ceiling = min(BASE_BACKOFF * (2 ** attempt), MAX_BACKOFF)
    return random.uniform(ceiling / 2.0, ceiling)


# ----------------------------------------------------------------------
# API client
# ----------------------------------------------------------------------
class BlizzardClient:
    def __init__(self, client_id: str, client_secret: str, region: str = "us"):
        self.region = region
        self.session = requests.Session()
        self.session.headers["Accept"] = "application/json"
        self._authenticate(client_id, client_secret)

    def _authenticate(self, client_id: str, client_secret: str) -> None:
        response = requests.post(
            OAUTH_HOST,
            data={"grant_type": "client_credentials"},
            auth=(client_id, client_secret),
            timeout=30,
        )
        response.raise_for_status()
        token = response.json()["access_token"]
        self.session.headers["Authorization"] = f"Bearer {token}"

    def get(self, path: str, namespace: str, **params):
        """Return the decoded body, or None for a genuine 404.

        Raises RetryExhausted when the retry budget runs out. Returning None
        for both used to make a throttled request indistinguishable from a
        character with no profile.
        """
        url = API_HOST.format(region=self.region) + path
        params.setdefault("locale", "en_US")
        params["namespace"] = f"{namespace}-{self.region}"

        last_status = None
        for attempt in range(RETRY_ATTEMPTS):
            response = self.session.get(url, params=params, timeout=30)

            if response.status_code == 404:
                return None
            if response.status_code in RETRY_STATUS:
                last_status = response.status_code
                if attempt == RETRY_ATTEMPTS - 1:
                    break
                time.sleep(retry_delay(attempt, response.headers.get("Retry-After")))
                continue

            response.raise_for_status()
            return response.json()

        raise RetryExhausted(
            f"HTTP {last_status} after {RETRY_ATTEMPTS} attempts: {path}"
        )

    def guild_roster(self, realm_slug: str, guild_slug: str):
        data = self.get(
            f"/data/wow/guild/{realm_slug}/{guild_slug}/roster",
            namespace="profile",
        )
        return (data or {}).get("members", [])

    def character_ilvl(self, realm_slug: str, name: str):
        data = self.get(
            f"/profile/wow/character/{realm_slug}/{name.lower()}",
            namespace="profile",
        )
        if not data:
            return None
        return data.get("equipped_item_level") or data.get("average_item_level")


# ----------------------------------------------------------------------
# Lua emitter
# ----------------------------------------------------------------------
LUA_HEADER = """\
-- RoS-Tools/Data/GuildData.lua
-- AUTO-GENERATED by Tools/fetch_guild_info.py -- DO NOT EDIT BY HAND.

local _, ns = ...

ns.GuildData = {{
  meta = {{
    -- generated_epoch is the authority: a plain UTC epoch, so the addon can
    -- age the data without knowing what zone this machine was in.
    -- generated_at is the same instant rendered as UTC, kept for humans
    -- reading the file and as the fallback for schema 2 readers.
    generated_epoch = {generated_epoch},
    generated_at = "{generated_at}",
    region = "{region}",
    realm = "{realm}",
    guild = "{guild}",
    schema = 3,
  }},
  ilvls = {{
"""

LUA_FOOTER = """  },
}
"""


def escape_lua(value: str) -> str:
    """Escape a value for a Lua 5.1 short string.

    A backslash and a double quote are not the whole set: a raw newline,
    carriage return or NUL inside a short string is a compile error, and a
    file that does not compile leaves ns.GuildData nil with no error anyone
    sees. Control characters go out as three-digit decimal escapes -- Lua 5.1
    reads ``\\0`` followed by a digit as one decimal escape, so the padding is
    what keeps ``NUL`` + ``"5"`` from becoming chr(5).
    """
    out = []
    for ch in value:
        if ch == "\\":
            out.append("\\\\")
        elif ch == '"':
            out.append('\\"')
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\r":
            out.append("\\r")
        elif ch == "\t":
            out.append("\\t")
        elif ord(ch) < 0x20 or ord(ch) == 0x7F:
            out.append("\\%03d" % ord(ch))
        else:
            out.append(ch)
    return "".join(out)


def write_lua(path: Path, characters: list[Character], meta: dict) -> None:
    """Write the export atomically.

    The default --out is the in-repo Data/GuildData.lua, and opening it "w"
    truncated the committed payload the instant the handle opened: a Ctrl-C or
    a full disk part-way through the loop left half a file behind. Build a
    sibling temp file and os.replace() it onto the target, which is atomic on
    the same filesystem -- readers see the old file or the new one, never a
    partial one.
    """
    characters = sorted(characters, key=lambda c: unicodedata.normalize("NFKD", c.key).lower())

    # Every string in the meta is escaped, not just the character keys. realm
    # and guild come from slugify() and region straight off the command line,
    # and guild-data.yml feeds all three from free-form workflow_dispatch input.
    safe_meta = {
        key: escape_lua(value) if isinstance(value, str) else value
        for key, value in meta.items()
    }

    temp = path.with_name(f"{path.name}.tmp-{os.getpid()}")
    try:
        # utf-8 (never utf-8-sig, so no BOM) and newline="\n" for LF endings.
        with temp.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(LUA_HEADER.format(**safe_meta))
            for character in characters:
                handle.write(f'    ["{escape_lua(character.key)}"] = {character.ilvl},\n')
            handle.write(LUA_FOOTER)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp, path)
    except BaseException:
        # BaseException, not Exception: KeyboardInterrupt is the case this
        # exists for, and it must not leave the temp file lying beside the
        # payload it was meant to protect.
        try:
            temp.unlink()
        except FileNotFoundError:
            pass
        raise


# ----------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------
def failure_threshold(target_count: int, override: int | None = None) -> int:
    """How many HARD failures are tolerable for a roster of this size.

    2% of the targets, never less than HARD_FAILURE_FLOOR. --max-failures
    overrides it for the run where a maintainer has looked at the failures and
    decided to publish anyway.
    """
    if override is not None:
        return max(0, int(override))
    return max(HARD_FAILURE_FLOOR, math.ceil(target_count * HARD_FAILURE_RATE))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--region", default="us")
    parser.add_argument("--realm", required=True, help="Guild's home realm, e.g. khadgar")
    parser.add_argument("--guild", required=True, help='Guild name, e.g. "Riddle of Steel"')
    parser.add_argument("--min-level", type=int, default=1,
                        help="Skip characters below this level")
    parser.add_argument("--workers", type=int, default=DEFAULT_WORKERS)
    # The %% is doubled because argparse %-formats help strings itself.
    parser.add_argument("--max-failures", type=int, default=None,
                        help="Abort without writing when more than this many characters fail "
                             f"with API errors. Default: {int(HARD_FAILURE_RATE * 100)}%% of "
                             f"the roster, at least {HARD_FAILURE_FLOOR}. Characters with no "
                             "profile are not counted.")
    parser.add_argument("--client-id", default=os.environ.get("BLIZZARD_CLIENT_ID"))
    parser.add_argument("--client-secret", default=os.environ.get("BLIZZARD_CLIENT_SECRET"))
    parser.add_argument("--out", type=Path,
                        default=Path(__file__).resolve().parent.parent / "Data" / "GuildData.lua")
    args = parser.parse_args()

    if not args.client_id or not args.client_secret:
        return parser.error(
            "Missing credentials. Set BLIZZARD_CLIENT_ID and BLIZZARD_CLIENT_SECRET, "
            "or pass --client-id/--client-secret."
        )

    # region is slugified like the other two -- it used to go into the Lua
    # header exactly as typed, with nothing between free-form workflow input
    # and the generated file.
    region_slug = slugify(args.region)
    realm_slug = slugify(args.realm)
    guild_slug = slugify(args.guild)

    try:
        check_meta_value("region", region_slug)
        check_meta_value("realm", realm_slug)
        check_meta_value("guild", guild_slug)
    except ValueError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    client = BlizzardClient(args.client_id, args.client_secret, region_slug)

    print(f"Fetching roster for {guild_slug} on {realm_slug} ({region_slug})...")
    try:
        members = client.guild_roster(realm_slug, guild_slug)
    except (RetryExhausted, requests.RequestException) as exc:
        print(f"error: could not read the guild roster: {exc}", file=sys.stderr)
        print("This is an API failure, not an empty guild. Nothing was written.",
              file=sys.stderr)
        return 1
    if not members:
        print("No roster returned. Check the realm and guild names.", file=sys.stderr)
        return 1

    targets = []
    for member in members:
        character = member.get("character", {})
        name = character.get("name")
        level = character.get("level", 0)
        member_realm = character.get("realm", {}).get("slug", realm_slug)
        if name and level >= args.min_level:
            targets.append((name, member_realm))

    # No targets means nothing to write, and "nothing" sails straight past the
    # hard-failure budget below -- zero failures is never over the limit -- to
    # overwrite the committed roster with an empty one. A --min-level above
    # every member of the guild is the easy way to get here.
    # compare_guild_data.py refuses an empty export before a publish, so CI is
    # covered, but a local run writes Data/GuildData.lua in place and there is
    # nothing downstream of that. The abort added for partial rosters walked
    # right past the emptiest roster of all.
    if not targets:
        print(
            f"\nerror: none of the {len(members)} roster entries is at least level "
            f"{args.min_level}, so there is nothing to query. Writing now would "
            "replace the committed roster with an empty one. Nothing was written.",
            file=sys.stderr,
        )
        return 1

    print(f"{len(targets)} characters to query...")

    results: list[Character] = []

    # Two kinds of "no result", kept apart. A 404 or a profile with no item
    # level is NORMAL -- a private profile or a character that never logged in
    # -- and has always been tolerated. Retries exhausted, or a network error,
    # means the character has an item level that this run failed to read, and
    # publishing the roster without it is data loss. Collapsing both into one
    # counter is what let a throttled run print "Wrote 178 entries", exit 0, and
    # sail through compare_guild_data.py's 66% floor with 44 members missing.
    no_profile = 0
    hard_failures: list[str] = []

    def fetch(target):
        name, member_realm = target
        try:
            ilvl = client.character_ilvl(member_realm, name)
        except RetryExhausted as exc:
            return (None, "hard", f"{name}-{member_realm}: rate limited or unavailable ({exc})")
        except requests.RequestException as exc:
            return (None, "hard", f"{name}-{member_realm}: {type(exc).__name__}: {exc}")
        if not ilvl:
            return (None, "no-profile", None)
        return (Character(name=name, realm_slug=member_realm, ilvl=int(ilvl)), "ok", None)

    with futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        for index, (character, kind, detail) in enumerate(pool.map(fetch, targets), start=1):
            if kind == "ok":
                results.append(character)
            elif kind == "no-profile":
                no_profile += 1
            else:
                hard_failures.append(detail)
            if index % 25 == 0:
                print(f"  {index}/{len(targets)}")

    threshold = failure_threshold(len(targets), args.max_failures)

    print()
    print(f"{len(results)} characters resolved")
    print(f"{no_profile} with no profile data (private, never logged in, or no item level)")
    print(f"{len(hard_failures)} API failures (retries exhausted or network error), "
          f"limit {threshold}")

    if len(hard_failures) > threshold:
        print(
            f"\nerror: {len(hard_failures)} of {len(targets)} characters could not be read "
            f"because of API failures, which is over the limit of {threshold} "
            f"({int(HARD_FAILURE_RATE * 100)}% of the roster, floor {HARD_FAILURE_FLOOR}). "
            "These are NOT missing profiles -- those are counted separately and tolerated. "
            "Publishing now would overwrite a complete roster with a partial one. "
            "Nothing was written.",
            file=sys.stderr,
        )
        for line in hard_failures[:10]:
            print(f"  {line}", file=sys.stderr)
        if len(hard_failures) > 10:
            print(f"  ... and {len(hard_failures) - 10} more", file=sys.stderr)
        print(
            "\nRe-run when the API is healthy. To publish anyway, pass "
            "--max-failures with a number you have actually looked at.",
            file=sys.stderr,
        )
        return 1

    generated = time.time()
    meta = {
        # UTC on both counts. time.strftime() with no time argument uses
        # local wall clock and records no offset, which left the addon
        # unable to tell how old the export actually was.
        "generated_epoch": int(generated),
        "generated_at": time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime(generated)),
        "region": region_slug,
        "realm": realm_slug,
        "guild": guild_slug,
    }

    args.out.parent.mkdir(parents=True, exist_ok=True)
    write_lua(args.out, results, meta)

    print(f"\nWrote {len(results)} entries to {args.out}")
    return 0


def run() -> int:
    """main() with a backstop, so a crash never looks like a clean result."""
    try:
        return main()
    except SystemExit:
        raise
    except Exception as exc:  # noqa: BLE001 -- deliberate backstop
        print(f"error: {type(exc).__name__}: {exc}", file=sys.stderr)
        print("Nothing was written.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(run())
