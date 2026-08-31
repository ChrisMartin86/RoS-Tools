# Roster snapshot propagation via addon comm — design & implementation spec

Status: **implemented.** `Core/Sync.lua` and its wiring exist; see the
"Corrections made during implementation" section at the end for the three
places the code deliberately departs from this spec. Companion to
`ADDON-COMM-DESIGN.md`, which specifies the *live per-player* ilvl channel
already shipped in `Core/Comm.lua`. This document specifies a second,
separate channel that propagates the whole `GuildData` snapshot.

Read `CLAUDE.md` first. One rule in it is deliberately amended by this
design; the amendment is written out in full at the end.

## Problem this solves
`Data/GuildData.lua` is a static export. Before this design it reached a
user's disk exactly two ways: reinstalling the addon, or leaving the Sidecar
resident. In practice most guildmates did neither. Their roster was whatever
shipped with the version they installed, and it decayed until they reinstalled.

This mechanism is now *the* general-user update path, and the only one: the
one-off PowerShell updaters were deleted on 2026-08-29, and the Sidecar is
maintainer-only. A guildmate installs from CurseForge and nothing else.

`Core/Comm.lua` does not fix this. It only makes *currently online*
guildmates' own numbers live, one player per message. Someone who logs on
after a raid night sees fresh numbers for the six people online and a
three-week-old file for everyone else.

The fix: a client holding a newer export gives it to a client holding an
older one, over the addon-message channel. One person running the Sidecar
keeps the whole guild current, without anyone else installing anything.

What this is **not**: it is not a replacement for the exporter or the
Sidecar. The addon cannot write files and never will. CI remains the only
Blizzard API consumer, the Sidecar remains the only thing that writes
`Data/GuildData.lua`, and this feature only moves an already-generated
snapshot from one client's memory to another's SavedVariables.

## Model
Epidemic gossip between symmetric peers. There is no server, no origin, no
privileged client — only "who currently holds the highest
`generated_epoch`", which changes hands constantly and is the only ranking
that exists.

1. **Announce.** A client says "my export is from epoch N" over `GUILD`.
   Nothing else. ~20 bytes.
2. **Pull.** A client whose own epoch is lower whispers a holder a request.
   Nobody pushes; a snapshot is only ever accepted in answer to a request
   this client made.
3. **Serve.** That one holder whispers back the serialized snapshot in
   paced chunks, to that one requester.
4. **Re-announce.** The moment a client adopts a snapshot, its epoch
   changed — so it announces again, and is now itself a holder. This is
   the beat that makes the thing epidemic rather than hub-and-spoke.

`generated_epoch` from `meta` is the only version number. It already
exists, it is already a UTC epoch, and the exporter already guarantees it
moves forward. Nothing new needs generating, and nothing needs to agree on
who is authoritative — the highest number wins, and equal numbers mean
byte-identical data by construction.

### Peer symmetry — no privileged node

Every participating client runs the identical state machine and is
simultaneously a potential requester and a potential server. Specifically:

- **Sidecar users are not special.** A client that adopted a snapshot two
  minutes ago serves it exactly as readily as the machine that generated
  it. The second client to sync is a valid source for the third. This is
  the difference between one Sidecar covering a guild and one Sidecar
  being a bottleneck and a single point of failure.
- **No rank gate.** Officers get no special standing. See the trust
  section for why the damage ceiling makes this affordable.
- **No election, no coordinator, no membership list.** Clients never agree
  on anything or track who else exists. They hear announcements, compare
  one integer, and act alone.
- **Ties break randomly.** When several peers announce the same highest
  epoch, the requester picks one at random rather than first-heard. Without
  this, everyone piles onto whoever has the lowest latency and the design
  quietly grows a hub.
- **No node is required to be online.** The guild converges as long as the
  newest snapshot exists on *some* client that logs in occasionally. If the
  Sidecar machine is off for a week, the guild stays at whatever epoch it
  had already spread to, and resumes when it returns.

The one asymmetry left is intentional: the snapshot still originates
outside the mesh, from CI and the Sidecar. The addon cannot generate a
roster, only relay one.

## Wire protocol
**A new prefix, `RoSToolsD1`** — not an extension of `RoSTools1`. Two
reasons: 2.2.0 clients never registered this prefix, so `CHAT_MSG_ADDON`
does not even fire for them (rather than firing and being rejected), and
the two channels can version independently. Bump the trailing digit only
on an incompatible payload change.

Field separator is `:`. Entry separators are `;` and `=`. **`|` is never
used** — it is WoW's escape-sequence introducer and the last thing that
should appear in a chat-path payload. Keys are `[A-Za-z][A-Za-z0-9]*` plus
`-` plus a slug of `[a-z0-9-]`, so no separator can appear inside a field.

| Msg | Channel | Shape | Meaning |
|---|---|---|---|
| `V` | `GUILD` | `V:<epoch>:<count>` | "my export is epoch N with C entries" |
| `Q` | `WHISPER` | `Q:<nonce>:<myEpoch>` | "send me yours; here is my request id" |
| `D` | `WHISPER` | `D:<nonce>:<seq>:<total>:<chunk>` | one chunk of the snapshot |
| `X` | `WHISPER` | `X:<nonce>:<reason>` | refusal — busy, cooled down, or not actually newer |

`X` exists so a requester fails over to the next-best holder in a second
instead of sitting out its full timeout.

### Serialized body

The concatenation of all `D` chunks, in `seq` order, is:

```
H:<epoch>:<region>:<realm>:<guild>:<schema>;Aep-baelgun=293;Akiza-antonidas=282;...
```

Sizing, against the current 220-entry export: entries average ~20 bytes,
so the body is ~4.5 KB. `SendAddonMessage` caps a message at 255 bytes;
the `D` header costs ~22, so **chunks are 200 bytes** with headroom. That
is ~23 chunks, paced one per 0.25s via a chained `C_Timer.After` — about
six seconds per transfer, well under any throttle.

Chunks are split blindly at 200 bytes and parsed only after reassembly, so
a split can land mid-key without consequence.

A realm dictionary (`R0=khadgar` … then `Aep=0=293`) would cut the body
~40%. Deliberately **not** in v1 — six seconds is already unnoticeable and
the dictionary adds a second format to get wrong. Revisit only if the
roster triples.

## Trust model — read this one carefully
This is where the feature genuinely differs from `Core/Comm.lua`, and the
difference is not small.

In `Comm.lua` the trust boundary is airtight by construction: the update
key comes from `sender`, so a client can only ever speak for itself. Here
a sender speaks for **the entire roster**, and the result is written to
SavedVariables where it survives logout. A bad payload is durable.

The damage ceiling bounds how paranoid to be: the worst outcome is wrong
item level numbers in a tooltip. No currency, no items, no code execution,
no data leaving the machine. Nobody is getting robbed. That argues for
cheap sanity checks over cryptography, but not for skipping them.

Every one of these is required:

- **Pull-only.** A `D` chunk is accepted only when it matches an
  outstanding request: right sender, right nonce, arriving inside the
  timeout window. Unsolicited snapshot pushes are dropped without parsing.
  This alone removes the drive-by attack.
- **Nonce per request.** `math.random` over a wide range, regenerated per
  attempt. Stops a stale reply from a previous attempt landing late and
  overwriting a better one.
- **Sender must be a guild member — established structurally, not by API.**
  We only ever request from a peer whose announcement we heard, and an
  announcement reaches us one of two ways: over `GUILD`, which only guild
  members can send, or whispered back within 60s of our own `GUILD`
  announce, which requires having received that announce. Either way the
  peer was in the guild. This is stronger than querying the roster API,
  which returns 0 members until the roster loads and would fail open at
  exactly the moment sync runs. `sender` itself is server-supplied and the
  payload cannot influence it.
- **Identity must match.** `region`/`realm`/`guild` in the body must equal
  the shipped file's `meta`. A snapshot for another guild is discarded, not
  merged.
- **Epoch sanity.** Reject `epoch > time() + 300` (no data from the
  future — this is what stops "claim epoch 9999999999 and win forever"),
  reject `epoch <= myEpoch` (no reason to accept it), reject anything
  older than 90 days.
- **Count sanity.** Reject if entry count is below 50% or above 200% of
  `GetNumGuildMembers()`. Catches both a truncated transfer and a
  fabricated roster.
- **Per-entry sanity.** Key must match `^[^%s:;=]+%-[%a%d%-]+$` and be at
  most 48 bytes; ilvl must be an integer in 1..999. Bad entries are dropped
  individually, and a snapshot that loses more than 10% of its entries to
  this is rejected whole. See the corrections section for why the name half
  is not `%a`.
- **Visible provenance.** `/ros sync` always names who a snapshot came
  from and when. `/ros sync forget` drops it. Nothing arrives silently and
  unattributably.

A rank gate — accept only from guild rank ≤ N — was considered and
**rejected**. It reintroduces a privileged node into a design whose whole
point is not having one, for a threat whose worst outcome is a wrong number
in a tooltip, and it fails outright whenever the person running the Sidecar
isn't an officer. Guild membership is the only gate.

## Storm control
The failure mode to avoid is thirty clients logging in at raid time and
all whispering the same holder at once. With no coordinator, this is
handled entirely by jitter and suppression.

**Announcing** happens on four occasions, all sharing one rule: pick a
random delay, and cancel if a peer announces an epoch ≥ ours before the
delay elapses. Someone else already said what we were going to say.

| Occasion | Delay | Why |
|---|---|---|
| `PLAYER_LOGIN` | 5–15s | tell the guild what we've got |
| After adopting a snapshot | 3–8s | beat 4 — spread what we just learned |
| Periodic anti-entropy | every 20 min ± 5 | convergence without anyone logging in or out |
Plus a fifth case that is not a broadcast at all: **hearing an epoch older
than ours triggers a direct whispered `V` back to that one peer**, jittered
0.5–3s and rate-limited to once per peer per minute.

This is the P2P-ish part worth being explicit about: it is a *nudge*, not a
push. Hearing that a peer is stale never causes us to send data — it causes
us to re-state our version number and wait to be asked. It is whispered
rather than broadcast because exactly one client needs to hear it, and it
is immediate because the alternative — waiting for the next broadcast —
means a client that logs in after every holder has already announced sits
at a stale roster until the 20-minute anti-entropy beat. The harness caught
exactly that.
Pull-only survives intact.

**Requesting.** Wait 3s after hearing an epoch newer than ours, collecting
other announcements in that window, then request from a **randomly chosen**
peer among those tied at the highest epoch. One request in flight; 20s
timeout; on timeout or `X`, fail over to another peer at that epoch;
**three attempts per session**, then stop until reload or `/ros sync now`.

**Serving.** Refuse (`X`) while already serving. Global 30s cooldown
between dumps. Per-requester cooldown of 10 minutes. Hard cap of five
dumps per session. A holder at its cap goes quiet rather than retrying —
with beat 4 in place, other peers are already carrying the same snapshot.

Net traffic at a 30-person raid invite: thirty ~20-byte announcements
spread over ten seconds, and a handful of whispered transfers that
fan out — one holder serves two or three peers, each of whom immediately
becomes a holder for the rest.

## Storage
New SavedVariables sub-table, `RoSToolsDB.syncedData`, **keyed by guild
identity**:

```lua
RoSToolsDB.syncedData = {
  ["us/khadgar/riddle-of-steel"] = {
    epoch      = 1787774418,
    schema     = 3,
    ilvls      = { ["Aep-baelgun"] = 293, ... },
    receivedAt = 1787780000,   -- epoch, for "/ros sync" provenance
    from       = "Icebyte",    -- sender name, provenance only, never a key
  },
}
```

The key costs nothing today — there is exactly one entry, for the guild in
the shipped file. It exists so that carrying snapshots for more than one
guild later is an additive change rather than a SavedVariables migration.
See the transport section. Prune to a single entry on every `Build()`
anyway: anything not matching the shipped file's identity is dropped, so a
guild transfer doesn't leave a dead roster on disk forever.

`Core/Data.lua:Build()` picks a source instead of assuming one:

- shipped epoch ≥ synced epoch → build from `ns.GuildData`, and **clear
  the synced entry**, since the file has caught up and keeping it is pure
  SV bloat. This is the path a Sidecar user takes on every update.
- synced epoch > shipped epoch and identity matches → build from the
  synced entry.
- identity mismatch, or malformed → drop the entry, build from the file.

The pre-2.0 `RiddledTooltip_DB` backfill stays, but runs **only when the
source is the shipped file.** Backfilling legacy keys into an adopted
snapshot would resurrect departed members through the back door.

`Data:SourceInfo()` returns `"file"` or `"sync"` plus the provenance
fields, for `/ros sync` and the login line.

The live overlay is untouched by any of this. It still sits on top of
whichever source won, still session-only, still never persisted.

## Files to add / touch
| File | Change |
|---|---|
| `Core/Sync.lua` | **new** — all of it. Announce, request, serve, reassemble, validate, adopt. |
| `Core/Data.lua` | source selection in `Build()`, `Data:Export()` (serialize current authoritative table), `Data:AdoptSnapshot(tbl)`, `Data:SourceInfo()`. |
| `Core/Config.lua` | three new `DEFAULTS` (below). |
| `Core/Events.lua` | `callSync()` alongside `callComm()`; route `CHAT_MSG_ADDON` to both. |
| `Modules/Commands.lua` | `/ros sync`, `/ros sync now`, `/ros sync forget`. |
| `RoS-Tools.toc` | `Core\Sync.lua` after `Core\Comm.lua`, above `# Modules`. |
| `.luacheckrc` | `GetNumGuildMembers`, `GetGuildRosterInfo`, `C_GuildInfo`, `UnitFullName`. |
| `README.md` | options table rows; a short "how the roster stays fresh" paragraph. |
| `CHANGELOG.md` | entry under `## Unreleased` › `### Added`. No `## Version:` bump — same convention as the sidecar and comm entries already sitting there. |
| `CLAUDE.md` | the amendment below. |

`Core/Sync.lua` follows `Comm.lua`'s precedent exactly: **not** a
`ns:RegisterModule()` citizen, wired directly into `Core/Events.lua`
through a `pcall`-wrapped `callSync()`, registering its own events in
`OnInitialize`/`OnEnable`. A bug in it degrades to "no snapshot sync" and
touches nothing else.

### New settings

```lua
syncEnabled = true,   -- participate in roster snapshot sync at all
syncShare   = true,   -- serve snapshots to guildmates (off = leech only)
syncNotify  = true,   -- print a line when a newer snapshot is adopted
```

Mirrors the `commEnabled` / `commBroadcast` pair. `syncEnabled = false`
gates both directions, receive included — same as `commEnabled` does.

### `/ros sync`

- `/ros sync` — source (`file` or `sync`), epoch, age, entry count, and
  for an adopted snapshot: who it came from and when.
- `/ros sync now` — re-announce and request immediately, ignoring the
  three-attempt cap. Manual escape hatch; also the fastest way to test.
- `/ros sync forget` — drop `syncedData`, rebuild from the shipped file.

## Guardrails (from `CLAUDE.md`, restated because this feature is exactly the kind that erodes them)
- **Lua 5.1.** No `goto`, no `//`, no bitwise ops, no `\u{}`. String ops
  through `string.*` / `:` methods only.
- **The addon never calls the internet.** This feature is not an
  exception — addon messages are the game's own channel and go nowhere
  Blizzard doesn't already route.
- **No secret on the client, ever.** Nothing here needs one, and nothing
  here is a reason to introduce one.
- **`Data/GuildData.lua` is still generated and still never hand-edited.**
  The addon cannot write it. An adopted snapshot lives in SavedVariables
  and never becomes a file.
- **`.toc` load order is manual and significant.** `Core\Events.lua` stays
  last.
- **New WoW globals go in `.luacheckrc`** or lint fails.
- 2-space Lua indent, LF, final newline, 120 columns. User-facing output
  through `ns.Print` / `ns.Warn` / `ns.Error`; debug through `ns.Debug`.

## The `CLAUDE.md` amendment
The current rule reads:

> **SavedVariables (`RoSToolsDB`) hold settings only.** The ilvl table is
> rebuilt from the static file on every `ADDON_LOADED`. Don't persist data
> there; 2.0 removed exactly that anti-pattern.

That rule exists for a good reason and this feature does violate its
letter, so it should be amended deliberately rather than quietly broken.
What 2.0 deleted was *derived, unversioned, unattributed* data that had no
way to be invalidated and silently outlived its source. An adopted
snapshot is the opposite: it is a complete export carrying its own epoch,
guild identity and provenance, and it is dropped automatically the moment
the shipped file catches up. Proposed replacement wording:

> **SavedVariables (`RoSToolsDB`) hold settings, plus at most one adopted
> roster snapshot (`syncedData`).** The ilvl table is rebuilt on every
> `ADDON_LOADED` from whichever source has the newer `generated_epoch` —
> the shipped `Data/GuildData.lua` or a snapshot received from a guildmate
> over addon comm. `syncedData` is cleared automatically as soon as the
> shipped file is newer. Nothing else is persisted: no scraped values, no
> live-overlay data, no derived tables. 2.0 removed *unversioned* persisted
> data, and that stays removed — anything stored here must carry an epoch
> and a self-invalidation rule.

## Transport boundary — guild-only today


Everything above assumes one guild, and that assumption is load-bearing in
exactly two places. Naming them now means a future change is additive.

**Where the assumption lives.**

1. `GUILD` is the only announce channel. It is also the reason the trust
   model gets to be as thin as it is: `SendAddonMessage(..., "GUILD")` can
   only reach guild members, and `sender` on the receiving side is
   server-supplied. Guild membership is free authentication.
2. One snapshot is assumed to be *the* snapshot. `Build()` picks a single
   winner and the shipped file's `meta` is the identity every peer is
   checked against.

**What would have to change to go wider.**

- *Cross-guild in a group* (`PARTY` / `RAID` / `INSTANCE_CHAT`): protocol
  is unchanged, but `V` must carry a guild identity so peers ignore
  announcements about rosters that aren't theirs, and the guild-membership
  check has to be replaced with something weaker — probably "same group",
  which is a much softer guarantee than "same guild".
- *Multiple rosters on one client*: `syncedData` is already keyed for
  this. `Build()` and `Data:*` would need a notion of an active roster
  rather than the single implicit one, which is a real refactor of
  `Core/Data.lua` and not a small one.
- *Cross-realm friends* (`BNSendGameData`): possible, different API,
  different rate limits, and no membership check at all. Would need actual
  signing to be defensible, which is where "no secret on the client, ever"
  starts to bite.

**Forward-compatibility rule, adopted now:** *parsers ignore unknown
trailing fields.* A `V:<epoch>:<count>` reader must accept
`V:<epoch>:<count>:<guild-id>` and ignore what it doesn't recognize. This
makes adding a field a non-breaking change instead of a `RoSToolsD2`.

## Testing / verification plan
`luacheck .` is the only automated check and it proves nothing about
behavior. This needs two accounts in the same guild.

1. `luacheck .` clean. Pre-existing `Modules/Roster.lua` warning aside, no
   new output.
2. Single client, `/ros set debug`, `/reload`. Expect the announce to fire
   once after its jitter delay and nothing else. Confirm no snapshot is
   requested when nobody newer is online.
3. Age client B artificially: edit its `Data/GuildData.lua` `generated_epoch`
   backward (a *local* edit for testing only — never commit it). Log both
   in. B should request from A, and A should serve. Watch the chunk count
   in debug output on both sides.
4. `/ros stats` and `/ros top` on B before and after. Entry count should
   move to A's, and a member present in A's export but absent from B's
   should appear.
5. **Departure test.** Remove an entry from A's export before step 3. After
   B adopts, that entry must be *gone* on B, not merged back in. This is
   the check that snapshot-replacement actually replaced.
6. `/reload` on B. The adopted snapshot must persist and still win.
7. Update B's shipped file to something newer than A's (Sidecar, or
   `scripts/Deploy-RoSTools.ps1` from a checkout with a fresh export).
   `/reload`. `/ros sync` must report source
   `file`, and `RoSToolsDB.syncedData` must be gone from
   `WTF\Account\...\SavedVariables\RoS-Tools.lua`.
8. `/ros set syncEnabled off` on B, `/reload`, repeat step 3. B must
   neither request nor accept.
9. Storm sanity: three or more clients logging in within a few seconds of
   each other. Confirm announcements are suppressed as designed and that
   no client serves more than its cap.
10. **Relay test — the P2P claim, and the one thing worth three accounts.**
    A newest, B and C both stale. Bring A and B up, let B adopt, then log
    A out entirely before bringing C up. C must sync from B. If C sits at
    its old epoch, beat 4 isn't firing and the mesh is a star.
11. Tie-break spread: with A and B both at the newest epoch, C's choice of
    source should vary across repeated `/ros sync now` runs rather than
    always landing on the same peer.
12. Interaction with `Core/Comm.lua`: with both features on, a live ilvl
    update received after a snapshot adoption must still win over the
    adopted number. The overlay sits on top of whichever source built the
    table.

## Open decisions for Chris, not for the implementing agent
1. **Defaults.** `syncEnabled` / `syncShare` proposed as `true` (opt-out),
   matching `commEnabled`. Serving costs a guildmate ~4.5 KB of whispers a
   few times a session; that is the whole cost of opting in by default.
   Recommend true, but this is a "the guild gets it automatically" call and
   it is yours.
2. **Login-line change.** Should the `PLAYER_LOGIN` line say when the
   roster came from a guildmate rather than the file? Proposed: only when
   `syncNotify` is on and only at the moment of adoption, to keep the
   login line short.
3. **Anti-entropy interval.** 20 minutes ± 5 is a guess. It is ~20 bytes
   per client per interval, so it could be 5 minutes without anyone
   noticing, or 60 if the periodic beat feels like clutter in a debug log.
4. **The cross-guild question, deferred not decided.** The transport
   section above lays out what changes if this ever needs to leave the
   guild. Nothing in v1 forecloses it, but nothing in v1 implements it
   either. Worth a second look only if RoS-Tools ever ships to a second
   guild.

### Resolved since the first draft

- **Rank gate: no.** Rejected on P2P grounds. It reintroduces privileged
  nodes for a threat whose worst outcome is a wrong tooltip number, and it
  breaks outright the moment the person running the Sidecar isn't an
  officer.
- **May a non-Sidecar client serve? Yes.** This is beat 4 and the single
  most important P2P property in the design — without it the mesh is a
  star with the Sidecar at the center.

## Corrections made during implementation

Three places where the code deliberately departs from the spec above. All
three were found by `Tools/sync-harness.lua`, which runs the real
`Core/*.lua` against a stubbed WoW API — there is no game client in this
environment, and these would otherwise have been found by the guild.

1. **The key regex would have silently dropped real members.** The spec
   said `^%a[%w]*%-[%a%d%-]+$`. `%a` is locale-dependent and does not match
   the UTF-8 bytes in a name like `Arrøw`, who is in the live roster today.
   Every such member would have been quietly discarded on every sync, and
   with enough of them the 10% bad-entry rule would have rejected whole
   snapshots for no visible reason. The name half is now
   `[^%s:;=]+` — anything that isn't a separator — with a 48-byte cap. The
   realm half stays `%a%d%-`, since realm slugs come from Blizzard and are
   ASCII by construction.

2. **Guild membership is checked structurally rather than through the
   roster API.** The spec called for `GetNumGuildMembers` /
   `GetGuildRosterInfo`. That API returns 0 until the guild roster has
   loaded, which at login is exactly when sync is running — so the check
   would have failed open precisely when it mattered. The protocol already
   guarantees what the check was for; see the trust section. The roster API
   is still used for the entry-count sanity check, where a false 0 just
   skips the check instead of rejecting good data.

3. **A stale peer gets a direct whispered reply, not a broadcast nudge.**
   The original rule ("re-announce if we haven't in 10 minutes") meant a
   client logging in after everyone else had already announced would hear
   nothing back and sit stale until the 20-minute anti-entropy beat. The
   harness's relay scenario failed on exactly this. Replaced with an
   immediate, jittered, per-peer-rate-limited whisper of our version number
   to the one client that needs it. Still pull-only — the reply carries a
   version, never data.

4. **Every cap and blacklist has to be time-bounded.** The first cut
   treated a refusal as terminal: three refusals and the client stopped
   syncing until relog. Contention is the *normal* case here — everyone's
   collect window expires on the same announcement, one peer wins the race
   and the rest get "busy" — so the common path burned every attempt and
   blacklisted every holder in the guild. Refusals are now classified
   retryable (`busy`, `cool`, `wait`) versus terminal (`off`, `old`), a
   retryable one refunds the attempt, and the anti-entropy beat clears every
   cap, blacklist and coordination flag in the file. A client that gives up
   for a whole session is a bug, not caution.

5. **The suppression flag swallowed the adoption announce.**
   `announceSuppress` survived `scheduleAnnounce`'s early return, and
   suppression triggered on `epoch >= mine` — so the very announcement that
   made a client request also poisoned its own pending announce, and the
   post-adoption "I'm a holder now" beat never fired. The mesh silently
   degraded to a star around whoever ran the exporter, which is the one
   property this design exists to avoid. Suppression now triggers only on an
   exact restatement of our own epoch, and the flag is cleared on every
   entry.

6. **A watchdog keyed on the nonce cannot be extended.** `C_Timer.After`
   can't be cancelled, so re-arming a longer timeout for a large transfer
   left the original 30-second timer live — it fired mid-transfer and threw
   the whole thing away. Any roster past ~700 members could never complete,
   silently, forever. The deadline now lives on `pending`; a timer that
   fires early re-arms itself for the remainder.

7. **`GetNumGuildMembers()` cannot be used for plausibility at all.** It
   fills in asynchronously, so during the first seconds of a session — which
   is exactly when sync runs — it returns arbitrary partial values. A bound
   derived from it rejects good snapshots as a matter of routine, in either
   direction. Entry count is now checked against a fixed ceiling. The same
   asynchrony means a *miss* against the guild roster is only trustworthy
   once the member count has held still; a hit is always definitive.

8. **Duplicate keys are never honest.** A legitimate body is serialized
   from a Lua table, so its keys are unique by construction. Counting parsed
   entries rather than distinct ones let a body of one key repeated 300
   times pass every size check as "300 members" and then collapse the
   receiver's roster to a single entry — which the receiver would then
   announce to the guild as authoritative. Any duplicate now rejects the
   whole snapshot.

9. **The pre-2.0 backfill must not be re-exported.** `Data:Build()` already
   guarded the ingest side, but `Data:Export()` iterated the same merged
   table, so legacy keys from one person's decade-old `Riddled_Data.lua`
   would propagate to the entire guild. Worse, realm-less legacy keys failed
   enough of the receiver's per-entry validation to trip the 10% bad-entry
   rule, so the guild's newest holder became the one client nobody could
   sync from — presenting as "sync just doesn't work here", with the reason
   visible only under `/ros set debug`. Legacy keys are now tracked and
   excluded from the wire body. Relatedly, the legacy `RiddledTooltip_Meta`
   backfill copied *every* field, letting a stale local file overwrite the
   guild identity that snapshots are validated against.

10. **"I can't judge this" is not "throw it away."** The snapshot store was
    pruned of everything whenever `Data:IdentityKey()` returned nil — which
    happens when `Data/GuildData.lua` fails to load, a state WoW reaches by
    skipping a file with a syntax error and carrying on. The adopted roster,
    the only copy anywhere, was erased on the next login with no way back.
