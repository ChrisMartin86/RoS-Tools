# Real-time ilvl propagation via addon comm — design & implementation spec

Handoff doc for whoever implements this. Repo: `RoS-Tools` (WoW retail addon,
Interface 120000, Lua 5.1). Read `CLAUDE.md` at repo root first — this feature
must obey all constraints there; this doc only adds detail specific to this
feature.

## Problem this solves

`Data/GuildData.lua` is a static export, refreshed once a day by CI and pushed
to installed clients by the Sidecar tray app on a ~6h poll. That's fine for
"who in the guild is geared" but stale for "did the person standing next to me
in Org right now just finish a BiS trinket." This feature closes that gap for
players who are online together, using WoW's addon-message channel — no
server, no API key, nothing the Sidecar's Blizzard-API-avoidance rules
(`[[ros-tools-sidecar]]` in project memory) touch at all, because this never
leaves the game client.

**Scope, deliberately narrow:** this is *not* a peer-to-peer replacement for
the Sidecar/static-file sync. It does not broadcast or request the full
roster. Each client broadcasts only its own character's current ilvl, only
when it changes, only to `GUILD`. Everyone else's static/Sidecar-fed data is
untouched as the fallback. Full-roster freshness stays the Sidecar's job;
this feature only makes *currently-online* people's numbers live.

## Wire protocol

- **Prefix:** `RoSTools1` (the trailing digit is a wire-format version, not
  the addon version — bump it if the payload shape ever changes
  incompatibly, so a mismatched old client just ignores messages it doesn't
  recognize instead of misparsing them). Must be registered with
  `C_ChatInfo.RegisterAddonMessagePrefix("RoSTools1")` before anything is
  sent or received — messages on an unregistered prefix are silently
  dropped by the client since ~8.0.
- **Channel:** `GUILD` only. No `PARTY`/`RAID`/`WHISPER`. This is also the
  trust boundary — only people already in the guild can send you anything.
- **Payload:** plain text, one entry per message: `<ilvl>` — the sender's
  own current item level as a bare integer, e.g. `"293"`. Nothing else
  needs to go over the wire, because **the key is not taken from the
  payload** — see Trust model below. This keeps every message well under
  255 bytes, so there is no need for chunking, sequencing, or a
  serialization library. Do not pull in AceComm/AceSerializer/ChatThrottleLib
  for this — three vendored libraries plus an `embeds.xml` and TOC changes
  is disproportionate to a payload that is a 3-digit number, and nothing
  else in this addon takes on Ace3 as a dependency.

## Trust model — important, don't skip

`CHAT_MSG_ADDON` hands the receiver the sender's name-realm as `arg2`/`arg4`
(exact args below), supplied by the client, not the payload text. **Bind the
update to that sender identity, never to anything the message body claims.**
A message only ever updates the ilvl for the character that actually sent it.
This is what stops a malicious or buggy client from broadcasting fake numbers
for *other* people's names. Since the payload carries no key, there's nothing
to spoof.

## Files to add / touch

| File | Change |
|---|---|
| `Core/Comm.lua` (new) | All of the logic below. Not registered via `ns:RegisterModule` — wired directly into the existing event frame, the same way `Core/Events.lua` calls `ns.LoadConfig()` and `ns.Data:Build()` directly rather than through the module-dispatch table. Rationale: Comm is infrastructure `Data` and the UI modules read from, parallel to how `Data.lua` itself isn't a registered module. |
| `Core/Data.lua` | Add a live-overlay table + accessor, see below. Existing query functions (`GetByKey`, `Get`, `GetForUnit`, `GetForGUID`, `Find`, `Top`, `Stats`) must prefer the overlay over the static `ilvls` table when a key is present in both. |
| `Core/Config.lua` | Add `commEnabled = true` and `commBroadcast = true` to `DEFAULTS`. `commEnabled` gates both send and receive; `commBroadcast` (only meaningful if `commEnabled`) lets someone receive live updates without announcing their own — e.g. an officer alt they don't want pinging the channel. Both surface automatically in `/ros set` and the README options table per the existing convention. |
| `RoS-Tools.toc` | Add `Core\Comm.lua` after `Core\Data.lua`, before the `# Modules` block — it needs `ns.Util`, `ns.db`, and `ns.Data` to exist, and every module may want to read `ns.Comm` state. |
| `.luacheckrc` | Add to `read_globals`: `"C_ChatInfo"`, `"IsInGuild"`, `"GetGuildInfo"` (only if used), `"GetTime"`. `C_AddOns` is already listed; `C_ChatInfo` is not. |
| `README.md` | Document the new `/ros set commenabled` / `commbroadcast` options in the existing options table (grep for `staleDays` to find the pattern). |
| `CHANGELOG.md` | Entry under `## Unreleased`, per the Versioning section of `CLAUDE.md`. |

## `Core/Comm.lua` — behavior spec

```
local _, ns = ...
```

**Lifecycle (called explicitly from `Core/Events.lua`, not via module dispatch):**

- On `ADDON_LOADED` (after `ns.LoadConfig()` and `ns.Data:Build()` have run):
  call `C_ChatInfo.RegisterAddonMessagePrefix("RoSTools1")` and
  `frame:RegisterEvent("CHAT_MSG_ADDON")` on the shared `RoSToolsEventFrame`
  (or a second small frame owned by `Comm.lua` — either is fine, but reuse
  the existing frame if it's not meaningfully more code, to keep "one event
  frame" true per the architecture notes).
- On `PLAYER_LOGIN`: nothing needs to broadcast immediately. Do **not**
  broadcast your own ilvl on login — that's a guild-wide message burst
  every time someone logs in, which is exactly the spam this design is
  trying to avoid. Broadcasting only happens on an actual ilvl change (below).
- Wrap every entry point in `pcall` the way `Init.lua`'s `dispatch()` does
  for modules — a bug in `Comm.lua` must degrade to "no live updates," never
  break `ADDON_LOADED`/`PLAYER_LOGIN` for the rest of the addon.

**Detecting your own ilvl change and broadcasting:**

- Hook `PLAYER_EQUIPMENT_CHANGED` (fires per-slot; debounce with a short
  `C_Timer.After` coalescing window, e.g. 2s, so equipping a full set doesn't
  send N messages).
- Read the new value via the existing `ns.Data:PlayerIlvl()` (already wraps
  `GetAverageItemLevel()`).
- Only send if: `ns.db.commEnabled and ns.db.commBroadcast`,
  `IsInGuild()` is true, the value actually changed since the last value
  *this session* broadcast (track it in a local, not SavedVariables), the
  change is at least 1 point, and at least `N` seconds (suggest 60) have
  passed since this client's last broadcast. That cooldown is the real
  spam guard — item level can flicker (temp enchants, food buffs affecting
  displayed average in some cases) and the guild channel is shared,
  rate-limited bandwidth other addons use too.
- Send with `C_ChatInfo.SendAddonMessage("RoSTools1", tostring(ilvl), "GUILD")`.

**Receiving:**

- `CHAT_MSG_ADDON` handler args (retail): `prefix, message, channel, sender`
  is the practical subset — confirm against current `CHAT_MSG_ADDON`
  documentation, arg order has shifted across expansions before. `sender`
  arrives as `"Name-Realm"` (or bare `"Name"` if same-realm depending on
  client version) — this is what feeds the trust-bound key, not the message
  body.
- Ignore if `prefix ~= "RoSTools1"` or `channel ~= "GUILD"`.
- Ignore your own messages (`sender` matching `ns.playerName`/`UnitName("player")`
  combo — compare against `UnitName("player")` plus realm, don't string-compare
  raw `sender` to a half-built key).
- Parse `message` as an integer; reject non-numeric or absurd values (e.g.
  outside 1–999) defensively — a malformed message from an addon version
  mismatch should be dropped, not crash the handler.
- Turn `sender` into the same `Name-realm-slug` key format the rest of the
  addon uses: `ns.Util.NormalizeKey(sender, ns.playerRealmSlug)` (this
  already exists in `Util.lua` and handles the bare-name/no-realm case by
  falling back to the local player's realm — correct here since
  `CHAT_MSG_ADDON` sender is same-realm-or-qualified, never cross-realm in a
  way `NormalizeKey`'s fallback would mishandle).
- Hand `(key, ilvl)` to `ns.Data:ApplyLiveUpdate(key, ilvl)`.
- Gate receiving on `ns.db.commEnabled` too (not just sending) — if a user
  turned the feature off they shouldn't get live overlay updates either.
- `ns.Debug` every accepted/rejected message when debug is on, matching the
  existing `/ros set debug` pattern — this is exactly the kind of thing
  that's invisible without it.

## `Core/Data.lua` — overlay changes

Add a third table alongside the existing `ilvls`/`lowered`/`meta` locals,
e.g. `liveOverlay = {}` (also add `liveOverlayLowered` for case-insensitive
lookup, mirroring the existing `lowered` table — or fold both into one
`{key=, ilvl=}`-shaped entry map, whichever reads cleaner against the
existing style).

- `Data:ApplyLiveUpdate(key, ilvl)` — new function. Validates inputs the
  same way `Build()` does (`type(k) == "string"`, `tonumber` + `math.floor`
  the ilvl), stores into the overlay, does **not** touch `ilvls` or `count`,
  and does **not** write to `RoSToolsDB` or any file — this is in-memory,
  session-only, gone on `/reload` or logout, matching the "SavedVariables
  hold settings only" hard constraint. The next `ADDON_LOADED` rebuild from
  the static file is what "resets" it, same as today.
- `Data:Build()` should **not** wipe the overlay — it only fires once at
  `ADDON_LOADED`, before `Comm.lua` has received anything, so this is moot
  in practice, but don't add a `wipe(liveOverlay)` call there by reflex; it
  would be actively wrong if `Build()` is ever called again mid-session.
- Every read path that resolves a key to an ilvl —
  `GetByKey`, and transitively `Get`, `GetForUnit`, `GetForGUID` — must check
  the overlay first and fall back to the static table. `Find`, `Top`, and
  `Stats` iterate the merged view (overlay entries override same-key static
  entries, plus any overlay-only keys — there won't be any, since you only
  ever hear from existing guild members, but don't assume that).
- Consider exposing `Data:IsLive(key)` (true if the overlay, not the static
  file, is the source for that key) so `Tooltip.lua`/`Roster.lua` can
  optionally flag a number as "live" in the UI later. Not required for v1 —
  flag it as a follow-up rather than building it now.

## Guardrails (all straight from `CLAUDE.md`, restated because a feature like
this is exactly where they're easy to violate)

- Lua 5.1 only — no `//`, no bitwise ops, no `goto`.
- `Core/Comm.lua` goes in the TOC *above* `Core/Events.lua`, in the Core
  block, not the Modules block — it isn't a `RegisterModule` citizen.
- Never write anything received over the wire into `RoSToolsDB` — that
  reintroduces exactly the anti-pattern 2.0 removed.
- Never touch `Data/GuildData.lua` from this code path — it is
  exporter-generated only, addon code must only ever read it.
- Guard every `C_ChatInfo` / `IsInGuild` call for existing as usual — this
  is genuinely stable modern API, unlike the Communities UI stuff in
  `Roster.lua`, so it doesn't need the same defensive widget-probing, but
  still nil-guard `IsInGuild()` results before branching.
- Run `luacheck .` before calling this done — it will fail on the new
  globals until `.luacheckrc` is updated (see table above).

## Testing / verification plan

No Lua test suite exists (per `CLAUDE.md`), so this is in-game only:

1. Two accounts (or one account + a guildmate) both in Riddle of Steel,
   both online, both with the updated addon.
2. Change gear on client A (equip a higher/lower ilvl piece). Confirm client
   B's tooltip/roster number for A updates within the debounce+cooldown
   window without a `/reload`.
3. Toggle `/ros set commenabled` off on B — confirm B stops receiving (A's
   number reverts to whatever the static file says on B).
4. Toggle `/ros set commbroadcast` off on A — confirm A stops sending but
   still receives.
5. `/ros set debug` on B, watch the accept/reject log lines while A changes
   gear repeatedly — confirm the cooldown suppresses rapid-fire messages
   rather than relaying every single one.
6. Log out and back in (or `/reload`) on B — confirm the live overlay is
   gone and B is back to purely static-file data until new messages arrive,
   proving nothing persisted to SavedVariables.
7. `luacheck .` clean.

## Open decision for Chris, not the implementing agent

None of the above needs a design call mid-implementation — the scope,
protocol, and trust model are fixed by this doc. The one open question is
product-level, not technical: should `commEnabled` default to `true` (opt-out)
or `false` (opt-in) for existing installs picking up the update? Recommend
`true` — it's guild-only, low-bandwidth, and off-by-default features tend to
just never get used — but that's Chris's call, not something to guess at
mid-implementation.
