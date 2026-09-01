#!/usr/bin/env python3
"""Regression tests for the developer tools in Tools/.

    python3 Tools/ops-checks.py

Stdlib unittest only, no network, no new dependencies. Every test here pins
down a bug that shipped, and each one fails if its fix is reverted:

  * fetch_guild_info: a run whose characters failed with API errors must abort
    WITHOUT writing and return non-zero, instead of publishing a partial roster
    and exiting 0. Characters with no profile stay tolerated.
  * fetch_guild_info: the output is written to a sibling temp file and
    os.replace()d into position, so an interrupted write leaves the previously
    committed payload intact rather than truncated.
  * fetch_guild_info: escape_lua covers a raw newline, carriage return and NUL,
    not just a backslash and a quote; and ':' ';' '|' '"' '\\' are rejected in
    region/realm/guild rather than escaped through, because the addon's `H:`
    sync header cannot represent them at all.
  * fetch_guild_info: a run with no targets at all -- --min-level above every
    member -- aborts instead of writing an empty roster over the committed one.
  * compare_guild_data: a publish is refused when the export describes a
    different region/realm/guild, or when it overlaps the published roster by
    too little -- a count-only guard cannot tell 200 characters from 200
    DIFFERENT characters.
  * compare_guild_data: a roster that shares NOT ONE character with the
    published one is refused at every size. SMALL_ROSTER exempts the percentage
    guards; it must not exempt wholesale replacement.
  * compare_guild_data: an unexpected exception exits 2, never 1. Exit 1 means
    "nothing changed" and the workflow maps it to a green run.

Tests here must be able to FAIL. Several in this file could not: they asserted
on arithmetic they had done themselves rather than on the function under test,
or they set up an input that a different guard rejected first, so the guard they
named could be deleted outright and the class stayed green. Each such test now
constructs the one input only its own guard can explain, and the comment above
it says what used to be wrong with it.
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import sys
import tempfile
import types
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parent


def _load(name: str, filename: str):
    """Import a Tools/ script by path, stubbing 'requests' if it is absent.

    The stub keeps this file runnable on a machine that has never installed
    the exporter's one dependency -- these tests never make a request.
    """
    if "requests" not in sys.modules:
        try:
            import requests  # noqa: F401
        except ImportError:
            stub = types.ModuleType("requests")

            class RequestException(Exception):
                pass

            class Session:  # pragma: no cover - never exercised
                def __init__(self):
                    self.headers = {}

            stub.RequestException = RequestException
            stub.Session = Session
            stub.post = None
            sys.modules["requests"] = stub

    spec = importlib.util.spec_from_file_location(name, TOOLS / filename)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


fetch = _load("rostools_fetch_guild_info", "fetch_guild_info.py")
compare = _load("rostools_compare_guild_data", "compare_guild_data.py")


# ----------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------
def make_export(entries: dict[str, int], region="us", realm="khadgar",
                guild="riddle-of-steel", epoch=1_700_000_000) -> str:
    """A file shaped like a real export, for the comparer to parse."""
    lines = [
        "local _, ns = ...",
        "",
        "ns.GuildData = {",
        "  meta = {",
        f"    generated_epoch = {epoch},",
        '    generated_at = "2026-08-31 09:00:00",',
        f'    region = "{region}",',
        f'    realm = "{realm}",',
        f'    guild = "{guild}",',
        "    schema = 3,",
        "  },",
        "  ilvls = {",
    ]
    for key, ilvl in entries.items():
        lines.append(f'    ["{key}"] = {ilvl},')
    lines.append("  },")
    lines.append("}")
    return "\n".join(lines) + "\n"


def roster(count: int, prefix: str = "Alpha", ilvl: int = 600) -> dict[str, int]:
    return {f"{prefix}{i}-khadgar": ilvl for i in range(count)}


@contextlib.contextmanager
def captured():
    """Swallow stdout/stderr so a failing assertion's output stays readable."""
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        yield out, err


class TempDirCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)


# ======================================================================
# Bug 7 -- a partial roster must never be written, and must not exit 0
# ======================================================================
class HardFailureThreshold(unittest.TestCase):
    """The 2%-with-a-floor-of-3 budget for API failures."""

    def test_floor_applies_to_a_small_roster(self):
        # 2% of 30 is 0.6; the floor is what keeps the guard meaningful.
        self.assertEqual(fetch.failure_threshold(30), 3)

    def test_percentage_applies_to_a_real_roster(self):
        # The reported case: 222 members -> ceil(4.44) = 5.
        self.assertEqual(fetch.failure_threshold(222), 5)

    def test_the_reported_failure_count_is_over_the_limit(self):
        # 44 of 222 failed. That must not be publishable.
        self.assertGreater(44, fetch.failure_threshold(222))

    def test_a_handful_of_failures_is_still_publishable(self):
        # `assertLessEqual(2, failure_threshold(222))` used to stand here and
        # asserted nothing at all: HARD_FAILURE_FLOOR is 3, so it holds for
        # every non-negative roster size and for any implementation that
        # returns the floor and ignores the percentage entirely. Pin the
        # percentage instead -- on a 222-member roster the answer must be
        # ABOVE the floor, which only the 2% term can produce.
        self.assertGreater(fetch.failure_threshold(222), fetch.HARD_FAILURE_FLOOR)
        self.assertLess(2, fetch.failure_threshold(222))

    def test_override_is_honoured(self):
        self.assertEqual(fetch.failure_threshold(222, 40), 40)
        self.assertEqual(fetch.failure_threshold(222, 0), 0)

    def test_a_negative_override_cannot_go_below_zero(self):
        self.assertEqual(fetch.failure_threshold(222, -5), 0)


class ThresholdAbortsTheWrite(TempDirCase):
    """End to end: a throttled run must not overwrite the committed roster."""

    ORIGINAL = "-- the complete roster that is already committed\nns.GuildData = {}\n"

    def _run(self, hard: int, missing: int = 0, total: int = 100, extra=()):
        """Run main() against a fake API where `hard` characters fail hard."""
        out = self.tmp / "GuildData.lua"
        out.write_text(self.ORIGINAL, encoding="utf-8")

        class FakeClient:
            def __init__(self, *a, **k):
                pass

            def guild_roster(self, realm_slug, guild_slug):
                return [
                    {"character": {"name": f"Alpha{i}", "level": 80,
                                   "realm": {"slug": "khadgar"}}}
                    for i in range(total)
                ]

            def character_ilvl(self, realm_slug, name):
                index = int(name[len("Alpha"):])
                if index < hard:
                    raise fetch.RetryExhausted("HTTP 429 after 5 attempts")
                if index < hard + missing:
                    return None          # a real 404 -- private or never played
                return 600

        original = fetch.BlizzardClient
        fetch.BlizzardClient = FakeClient
        argv = sys.argv
        sys.argv = [
            "fetch_guild_info.py",
            "--realm", "khadgar",
            "--guild", "Riddle of Steel",
            "--client-id", "id", "--client-secret", "secret",
            "--workers", "4",
            "--out", str(out),
            *extra,
        ]
        try:
            with captured() as (stdout, stderr):
                status = fetch.main()
        finally:
            fetch.BlizzardClient = original
            sys.argv = argv
        return status, out, stdout.getvalue() + stderr.getvalue()

    def test_too_many_api_failures_abort_without_writing(self):
        # 20 of 100 failed. threshold is max(3, ceil(2)) = 3.
        status, out, _ = self._run(hard=20)
        self.assertNotEqual(status, 0, "a throttled run must not exit 0")
        self.assertEqual(out.read_text(encoding="utf-8"), self.ORIGINAL,
                         "the committed roster was overwritten by a partial one")

    def test_the_output_says_which_kind_of_failure_it_was(self):
        _, _, text = self._run(hard=20)
        self.assertIn("API failures", text)
        self.assertIn("NOT missing profiles", text)

    def test_characters_with_no_profile_are_still_tolerated(self):
        # 30 of 100 have no profile at all. That is normal and must publish.
        status, out, text = self._run(hard=0, missing=30)
        self.assertEqual(status, 0)
        self.assertIn("no profile data", text)
        self.assertIn("Wrote 70 entries", text)
        self.assertNotEqual(out.read_text(encoding="utf-8"), self.ORIGINAL)

    def test_a_few_api_failures_still_publish(self):
        status, out, _ = self._run(hard=3)
        self.assertEqual(status, 0)
        self.assertIn('["Alpha99-khadgar"] = 600,', out.read_text(encoding="utf-8"))

    def test_max_failures_can_override_the_limit(self):
        status, out, _ = self._run(hard=20, extra=("--max-failures", "25"))
        self.assertEqual(status, 0)
        self.assertNotEqual(out.read_text(encoding="utf-8"), self.ORIGINAL)

    def test_the_threshold_boundary_is_where_failure_threshold_says_it_is(self):
        # The reported roster: 222 members, so the limit is ceil(4.44) = 5.
        # Exactly at the limit publishes; one over aborts. This is the pair the
        # tautological `assertLessEqual(2, failure_threshold(222))` was standing
        # in for -- it pins both the number AND that main() compares with `>`.
        self.assertEqual(fetch.failure_threshold(222), 5)

        status, out, _ = self._run(hard=5, total=222)
        self.assertEqual(status, 0, "5 failures of 222 is at the limit and must publish")
        self.assertNotEqual(out.read_text(encoding="utf-8"), self.ORIGINAL)

        status, out, _ = self._run(hard=6, total=222)
        self.assertNotEqual(status, 0, "6 failures of 222 is over the limit")
        self.assertEqual(out.read_text(encoding="utf-8"), self.ORIGINAL)

    def test_a_roster_with_nothing_above_min_level_does_not_write_an_empty_file(self):
        # Every member is level 80; --min-level 90 leaves no targets at all.
        # Zero targets means zero hard failures, which is never over the limit,
        # so the abort added for PARTIAL rosters walked straight past the
        # emptiest roster of all and wrote it over the committed one.
        status, out, text = self._run(hard=0, extra=("--min-level", "90"))
        self.assertNotEqual(status, 0, "an empty roster must not exit 0")
        self.assertEqual(out.read_text(encoding="utf-8"), self.ORIGINAL,
                         "the committed roster was overwritten with an empty one")
        self.assertIn("Nothing was written", text)

    def test_a_rejected_guild_name_never_reaches_the_api(self):
        status, out, _ = self._run(hard=0, extra=())
        self.assertEqual(status, 0)  # sanity: the harness itself works
        out.write_text(self.ORIGINAL, encoding="utf-8")
        argv = sys.argv
        sys.argv = [
            "fetch_guild_info.py",
            "--realm", "khadgar",
            "--guild", 'Riddle "of" Steel',
            "--client-id", "id", "--client-secret", "secret",
            "--out", str(out),
        ]
        try:
            with captured():
                status = fetch.main()
        finally:
            sys.argv = argv
        self.assertNotEqual(status, 0)
        self.assertEqual(out.read_text(encoding="utf-8"), self.ORIGINAL)


class RetryExhaustionIsNotA404(unittest.TestCase):
    """The distinction the whole abort rests on."""

    def test_retry_exhausted_is_its_own_exception(self):
        self.assertTrue(issubclass(fetch.RetryExhausted, Exception))

    def test_get_raises_rather_than_returning_none_when_retries_run_out(self):
        client = fetch.BlizzardClient.__new__(fetch.BlizzardClient)
        client.region = "us"

        class Throttled:
            status_code = 429
            headers = {"Retry-After": "0"}

        class Session:
            def get(self, *a, **k):
                return Throttled()

        client.session = Session()
        # A 404 returns None (tolerated). Exhausted retries must NOT: returning
        # None for both is what made a throttled character indistinguishable
        # from one with no profile.
        with self.assertRaises(fetch.RetryExhausted):
            client.get("/profile/wow/character/khadgar/nobody", namespace="profile")

    def test_a_real_404_still_returns_none(self):
        client = fetch.BlizzardClient.__new__(fetch.BlizzardClient)
        client.region = "us"

        class Missing:
            status_code = 404
            headers = {}

        class Session:
            def get(self, *a, **k):
                return Missing()

        client.session = Session()
        self.assertIsNone(
            client.get("/profile/wow/character/khadgar/nobody", namespace="profile")
        )


# ======================================================================
# Bug 10 -- Retry-After, a real ceiling, and jitter
# ======================================================================
class RetryBackoff(unittest.TestCase):
    def test_retry_after_in_seconds_is_honoured(self):
        self.assertEqual(fetch.retry_delay(0, "17"), 17.0)

    def test_retry_after_is_capped(self):
        self.assertEqual(fetch.retry_delay(0, "99999"), fetch.MAX_BACKOFF)

    def test_retry_after_as_an_http_date_is_honoured(self):
        # Any date in the past clamps to zero rather than going negative.
        self.assertEqual(fetch.retry_delay(0, "Wed, 21 Oct 2015 07:28:00 GMT"), 0.0)

    def test_garbage_retry_after_falls_back_to_backoff(self):
        self.assertGreater(fetch.retry_delay(3, "soon"), 0.0)

    def test_the_budget_reaches_tens_of_seconds(self):
        # The old schedule was 2 ** attempt * 0.25 -- 3.75 s across all five
        # attempts, less than one Blizzard throttle window.
        #
        # This used to recompute the schedule from BASE_BACKOFF and MAX_BACKOFF
        # and assert on its own arithmetic without ever calling retry_delay, so
        # reverting retry_delay to the old formula left it green. Sample the
        # REAL function instead. get() sleeps once per attempt except the last,
        # so RETRY_ATTEMPTS - 1 waits make up the budget.
        samples = 200
        draws = [
            [fetch.retry_delay(attempt, None) for _ in range(samples)]
            for attempt in range(fetch.RETRY_ATTEMPTS - 1)
        ]
        worst = sum(max(d) for d in draws)
        # Full jitter puts the floor at ceiling/2, so even the luckiest run
        # still waits a long time -- that is what makes the budget real rather
        # than merely possible.
        best = sum(min(d) for d in draws)

        self.assertGreater(worst, 10.0, f"the whole retry budget is only {worst:.2f}s")
        self.assertGreater(best, 5.0, f"a lucky run waits only {best:.2f}s in total")

    def test_backoff_is_jittered(self):
        # Identical delays across eight threads produce a synchronised burst
        # that re-trips the throttle they are waiting out.
        seen = {fetch.retry_delay(4) for _ in range(50)}
        self.assertGreater(len(seen), 1)


# ======================================================================
# Bug 8 -- the write is atomic
# ======================================================================
class AtomicWrite(TempDirCase):
    def test_a_normal_write_lands_and_leaves_no_temp_file(self):
        out = self.tmp / "GuildData.lua"
        fetch.write_lua(
            out,
            [fetch.Character(name="Alpha", realm_slug="khadgar", ilvl=600)],
            self._meta(),
        )
        text = out.read_text(encoding="utf-8")
        self.assertIn('["Alpha-khadgar"] = 600,', text)
        self.assertEqual(list(self.tmp.iterdir()), [out])

    def test_no_bom_and_lf_endings(self):
        out = self.tmp / "GuildData.lua"
        fetch.write_lua(out, [], self._meta())
        raw = out.read_bytes()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\r\n", raw)

    def test_an_interrupt_mid_write_leaves_the_committed_payload_intact(self):
        out = self.tmp / "GuildData.lua"
        original = "-- the roster that is already committed\nns.GuildData = {}\n"
        out.write_text(original, encoding="utf-8")

        class Exploding:
            """A character whose ilvl blows up while being formatted."""

            name = "Boom"
            realm_slug = "khadgar"
            key = "Boom-khadgar"

            @property
            def ilvl(self):
                raise KeyboardInterrupt("Ctrl-C part-way through the loop")

        with self.assertRaises(KeyboardInterrupt):
            fetch.write_lua(out, [Exploding()], self._meta())

        # The committed file is untouched -- not truncated, not half-written.
        self.assertEqual(out.read_text(encoding="utf-8"), original)
        # And no temp file was left beside it.
        self.assertEqual([p.name for p in self.tmp.iterdir()], ["GuildData.lua"])

    @staticmethod
    def _meta():
        return {
            "generated_epoch": 1_700_000_000,
            "generated_at": "2026-08-31 09:00:00",
            "region": "us",
            "realm": "khadgar",
            "guild": "riddle-of-steel",
        }


# ======================================================================
# Bug 9 -- escaping, and rejecting what cannot be escaped
# ======================================================================
class EscapeLua(unittest.TestCase):
    def test_backslash_and_quote(self):
        self.assertEqual(fetch.escape_lua(r"a\b"), r"a\\b")
        self.assertEqual(fetch.escape_lua('a"b'), 'a\\"b')

    def test_newline_is_escaped(self):
        # A raw newline in a Lua 5.1 short string is a compile error, and a
        # file that does not compile leaves ns.GuildData nil, silently.
        self.assertEqual(fetch.escape_lua("a\nb"), "a\\nb")

    def test_carriage_return_is_escaped(self):
        self.assertEqual(fetch.escape_lua("a\rb"), "a\\rb")

    def test_nul_is_escaped_with_three_digits(self):
        # Three digits, so NUL followed by a digit cannot be read as one
        # decimal escape: "\0" + "5" would otherwise mean chr(5).
        self.assertEqual(fetch.escape_lua("a\x00b"), "a\\000b")
        self.assertEqual(fetch.escape_lua("a\x005"), "a\\0005")

    def test_no_raw_control_characters_survive(self):
        escaped = fetch.escape_lua("".join(chr(c) for c in range(0x20)) + "\x7f")
        for ch in escaped:
            self.assertGreaterEqual(ord(ch), 0x20, f"raw control char {ch!r} survived")

    def test_ordinary_text_and_utf8_are_untouched(self):
        self.assertEqual(fetch.escape_lua("Kel'Thuzad"), "Kel'Thuzad")
        self.assertEqual(fetch.escape_lua("Sœur"), "Sœur")


class MetaValidation(unittest.TestCase):
    """':' ';' '|' '"' '\\' are rejected, not escaped through."""

    def test_the_reported_guild_name_is_rejected(self):
        # slugify('Riddle "of" Steel') -> 'riddle-"of"-steel', which emitted
        # guild = "riddle-"of"-steel", -- a Lua syntax error nothing caught.
        slug = fetch.slugify('Riddle "of" Steel')
        self.assertIn('"', slug)
        with self.assertRaises(ValueError):
            fetch.check_meta_value("guild", slug)

    def test_colon_is_rejected(self):
        # Core/Sync.lua captures the H: header's fields as [^:;]*, so a colon
        # does not corrupt the file -- every peer just silently rejects it.
        with self.assertRaises(ValueError):
            fetch.check_meta_value("realm", "khad:gar")

    def test_semicolon_is_rejected(self):
        with self.assertRaises(ValueError):
            fetch.check_meta_value("realm", "khad;gar")

    def test_pipe_is_rejected(self):
        with self.assertRaises(ValueError):
            fetch.check_meta_value("region", "u|s")

    def test_backslash_is_rejected(self):
        with self.assertRaises(ValueError):
            fetch.check_meta_value("guild", "riddle\\of\\steel")

    def test_control_characters_are_rejected(self):
        with self.assertRaises(ValueError):
            fetch.check_meta_value("guild", "riddle\nof-steel")

    def test_an_empty_value_is_rejected(self):
        with self.assertRaises(ValueError):
            fetch.check_meta_value("realm", "")

    def test_ordinary_values_pass(self):
        for field, value in (("region", "us"), ("realm", "khadgar"),
                             ("guild", "riddle-of-steel"), ("realm", "kelthuzad")):
            fetch.check_meta_value(field, value)  # must not raise

    def test_the_error_names_the_field_and_the_value(self):
        with self.assertRaises(ValueError) as caught:
            fetch.check_meta_value("guild", 'riddle-"of"-steel')
        message = str(caught.exception)
        self.assertIn("guild", message)
        self.assertIn("of", message)


class MetaIsEscapedOnTheWayOut(TempDirCase):
    def test_meta_values_go_through_escape_lua(self):
        # LUA_HEADER.format(**meta) used to interpolate meta verbatim; only the
        # character keys were escaped.
        out = self.tmp / "GuildData.lua"
        fetch.write_lua(out, [], {
            "generated_epoch": 1,
            "generated_at": "2026-08-31 09:00:00",
            "region": "us",
            "realm": "khadgar",
            "guild": 'riddle\\of"steel',
        })
        text = out.read_text(encoding="utf-8")
        self.assertIn(r'guild = "riddle\\of\"steel"', text)


# ======================================================================
# Bug 11 -- the publish guard is not count-only
# ======================================================================
class ComparerCase(TempDirCase):
    """Runs compare_guild_data over two files and returns its exit code."""

    def _run(self, old_text: str, new_text: str) -> int:
        old = self.tmp / "old.lua"
        new = self.tmp / "new.lua"
        old.write_text(old_text, encoding="utf-8")
        new.write_text(new_text, encoding="utf-8")
        argv = sys.argv
        sys.argv = ["compare_guild_data.py", "--new", str(new), "--old", str(old)]
        try:
            with captured():
                return compare.run()
        finally:
            sys.argv = argv


class IdentityGuard(ComparerCase):
    """Only the identity guard may explain any of these.

    Every test here used to build the new roster with prefix="Beta", so the
    overlap was 0% and the OVERLAP guard returned 2 before the identity guard
    was ever consulted: delete `identity_mismatches` entirely and the whole
    class stayed green. The rosters below are IDENTICAL on both sides -- 100%
    overlap, no shrink, same size -- so 2 can only come from region/realm/guild.
    """

    IDENTICAL = roster(200)

    def test_a_different_region_is_refused(self):
        # The reported case: a workflow_dispatch run with region: eu. Same 200
        # characters, one field different. With the guard removed this exits 0
        # and publishes a foreign-region export.
        old = make_export(self.IDENTICAL, region="us")
        new = make_export(self.IDENTICAL, region="eu")
        self.assertEqual(self._run(old, new), 2)

    def test_a_different_guild_is_refused(self):
        old = make_export(self.IDENTICAL, guild="riddle-of-steel")
        new = make_export(self.IDENTICAL, guild="riddle-of-stele")
        self.assertEqual(self._run(old, new), 2)

    def test_a_different_realm_is_refused(self):
        old = make_export(self.IDENTICAL, realm="khadgar")
        new = make_export(self.IDENTICAL, realm="proudmoore")
        self.assertEqual(self._run(old, new), 2)

    def test_the_identity_guard_is_what_speaks(self):
        # And it must say so: the operator has to be able to tell a wrong
        # --region from a churned roster without reading the source.
        old = self.tmp / "old.lua"
        new = self.tmp / "new.lua"
        old.write_text(make_export(self.IDENTICAL, region="us"), encoding="utf-8")
        new.write_text(make_export(self.IDENTICAL, region="eu"), encoding="utf-8")
        argv = sys.argv
        sys.argv = ["compare_guild_data.py", "--new", str(new), "--old", str(old)]
        try:
            with captured() as (stdout, stderr):
                status = compare.run()
        finally:
            sys.argv = argv
        text = stdout.getvalue() + stderr.getvalue()
        self.assertEqual(status, 2)
        self.assertIn("region: published 'us' -> new 'eu'", text)

    def test_a_field_the_new_export_has_lost_is_refused(self):
        # `if was and now and was != now` waved this through in one direction:
        # a field absent from the NEW meta disabled the guard for that field.
        # The exporter always writes all three and rejects an empty value, so a
        # new export missing one is truncated or corrupt.
        old = make_export(self.IDENTICAL, region="us")
        new = make_export(self.IDENTICAL).replace('    region = "us",\n', "")
        self.assertEqual(self._run(old, new), 2)

    def test_the_same_guild_with_real_changes_still_publishes(self):
        entries = roster(200)
        changed = dict(entries)
        changed["Alpha0-khadgar"] = 615
        self.assertEqual(self._run(make_export(entries), make_export(changed)), 0)

    def test_a_field_the_published_export_predates_does_not_block_a_publish(self):
        # The one direction that must stay tolerant: the PUBLISHED file was
        # written before the field existed. Blocking on that would deadlock the
        # pipeline until someone deleted the published file by hand.
        entries = roster(200)
        changed = dict(entries)
        changed["Alpha0-khadgar"] = 615
        old = make_export(entries).replace('    region = "us",\n', "")
        self.assertEqual(self._run(old, make_export(changed)), 0)


class OverlapGuard(ComparerCase):
    def test_a_wholly_different_roster_of_the_same_size_is_refused(self):
        # Same region/realm/guild in the meta, same size, none of the same
        # people. Count-only guards see nothing wrong at all.
        old = make_export(roster(200, prefix="Alpha"))
        new = make_export(roster(200, prefix="Beta"))
        self.assertEqual(self._run(old, new), 2)

    def test_a_roster_that_churned_past_the_floor_is_refused(self):
        old_entries = roster(200, prefix="Alpha")
        keep = dict(list(old_entries.items())[:80])          # 40% overlap
        keep.update(roster(120, prefix="Gamma"))
        self.assertEqual(self._run(make_export(old_entries), make_export(keep)), 2)

    def test_normal_turnover_still_publishes(self):
        old_entries = roster(200, prefix="Alpha")
        new_entries = dict(list(old_entries.items())[:180])  # 90% overlap
        new_entries.update(roster(20, prefix="Gamma"))
        self.assertEqual(self._run(make_export(old_entries), make_export(new_entries)), 0)

    def test_a_small_roster_is_exempt_from_the_PERCENTAGE_guard(self):
        # Below SMALL_ROSTER a percentage guard is noise: a ten-person guild
        # really can lose six members in a week, and 40% overlap must publish.
        old_entries = roster(10, prefix="Alpha")
        new_entries = dict(list(old_entries.items())[:4])
        new_entries.update(roster(6, prefix="Gamma"))
        self.assertEqual(self._run(make_export(old_entries), make_export(new_entries)), 0)

    def test_a_small_roster_is_NOT_exempt_from_wholesale_replacement(self):
        # This case used to be written down as intended behaviour and asserted
        # exit 0: ten published characters replaced by ten entirely different
        # ones, which is a wrong guild/realm/region whose meta happens to agree,
        # not a membership change. SMALL_ROSTER exempts the percentage guards;
        # it must not exempt "not one published character survived".
        old = make_export(roster(10, prefix="Alpha"))
        new = make_export(roster(10, prefix="Beta"))
        self.assertEqual(self._run(old, new), 2)

    def test_wholesale_replacement_is_refused_at_every_size(self):
        for size in (1, 5, 19, 200):
            with self.subTest(size=size):
                old = make_export(roster(size, prefix="Alpha"))
                new = make_export(roster(size, prefix="Beta"))
                self.assertEqual(self._run(old, new), 2)

    def test_overlap_ratio_is_measured_against_the_published_roster(self):
        old_entries = roster(100, prefix="Alpha")
        new_entries = dict(list(old_entries.items())[:50])
        self.assertAlmostEqual(compare.overlap_ratio(old_entries, new_entries), 0.5)
        self.assertAlmostEqual(compare.overlap_ratio({}, new_entries), 1.0)


# ======================================================================
# Bug 12 -- a crash is exit 2, never exit 1
# ======================================================================
class CrashIsExitTwo(TempDirCase):
    def test_a_corrupt_old_file_exits_2_not_1(self):
        # guild-data.yml maps exit 1 to publish=false and a GREEN run, so a
        # UnicodeDecodeError here used to read as "nothing changed" and the
        # data quietly stopped updating.
        old = self.tmp / "old.lua"
        new = self.tmp / "new.lua"
        old.write_bytes(b"\xff\xfe\x00\x00 not utf-8 at all \xc3\x28")
        new.write_text(make_export(roster(200)), encoding="utf-8")

        argv = sys.argv
        sys.argv = ["compare_guild_data.py", "--new", str(new), "--old", str(old)]
        try:
            with captured():
                status = compare.run()
        finally:
            sys.argv = argv
        self.assertEqual(status, 2)

    def test_an_unexpected_exception_becomes_exit_2(self):
        original = compare.main
        compare.main = lambda: (_ for _ in ()).throw(RuntimeError("boom"))
        try:
            with captured():
                self.assertEqual(compare.run(), 2)
        finally:
            compare.main = original

    def test_a_deliberate_exit_1_is_still_exit_1(self):
        # The backstop must not swallow the one meaning exit 1 has.
        original = compare.main
        compare.main = lambda: 1
        try:
            with captured():
                self.assertEqual(compare.run(), 1)
        finally:
            compare.main = original

    def test_argparse_usage_errors_stay_in_the_fail_loudly_lane(self):
        argv = sys.argv
        sys.argv = ["compare_guild_data.py"]  # --new is required
        try:
            with captured():
                with self.assertRaises(SystemExit) as caught:
                    compare.run()
        finally:
            sys.argv = argv
        self.assertNotEqual(caught.exception.code, 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
