# ADR-F008: Un-Reviewing Must Persist, and Take the Files With It

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-29
**Status**: ACCEPTED
**Affects**: `PluginConfiguration.UnreviewedVodStreamIdsJson` / `UnreviewedSeriesIdsJson`
(new), `StrmSyncService.SyncMoviesAsync` / `SyncSeriesCoreAsync` (review gate + a new
removal pass), `StrmSyncService.ReconcileMovieDecisionIdentity` (carries tombstones),
`Configuration/Web/config.js` (maintains the tombstones beside the reviewed sets)

---

## Context

[ADR-F002](002-require-review-before-sync.md) holds un-reviewed titles out of the sync,
with a load-bearing exemption: a title **already on disk** syncs anyway and its StreamId is
added to the reviewed checkpoint. The exemption exists so that a provider re-issuing an
established film's ID cannot quietly withhold a film the user keeps — the identity index
(library folder names and `[tmdbid=]` suffixes) is the record of past keep decisions.

The de-dup view's "reviewed" toggle removes an id from that same checkpoint.

## Problem

The two mechanisms are mutually exclusive in one direction: **an un-review of a title
that is already on disk cannot persist.** The toggle removes the id from the reviewed set,
the next sync sees a title that is neither reviewed nor excluded but whose folder exists,
and the exemption reads that folder as "the user already keeps this" — re-adding the id to
the reviewed checkpoint and saving the config. Observed as the exact user report: un-tick
"reviewed", Save, Sync Movies Now, refresh — "✓ reviewed" again, files still in the
library, forever.

The exemption cannot distinguish the two cases from library state alone:

- **ID churn**: the *provider* withdrew nothing; the user's keep decision is intact and
  merely keyed on a dead id. Restoring it is the whole point of the exemption.
- **Deliberate un-review**: the *user* withdrew the keep decision. The folder being on
  disk is not evidence they keep the title — it is the state they are undoing.

And the user's expectation of "un-review" is not merely "flip a bookmark". When the review
gate is on, the library is supposed to contain what has been reviewed; an un-reviewed
title with files on disk is a title the library still serves. Un-reviewing a title and
finding it still playable is indistinguishable from the un-review having failed — which
in this bug's case it had.

## Alternatives considered

1. **Drop the on-disk exemption entirely.** Fixes the resurrection but reintroduces the
   problem ADR-F002 built it for: a renumbered established film looks un-reviewed, is
   held, its files fall out of `writtenPaths`, and orphan cleanup deletes a film the user
   keeps. Worse in every direction.
2. **Make the gate never write the checkpoint back.** The auto-review write-back is what
   heals ID drift; removing it strands every re-issued id in the review queue.
3. **Treat an un-review as an exclusion.** The blocklist is the wrong store: excluding is
   a stronger decision (never sync again, delete the folder) than "put this back in my
   review queue", and ADR-F002 already documents that bulk-triaging via exclusions
   poisons the reviewed derivation.
4. **A tombstone store the exemption must respect.** Chosen.

## Decision

Two new decision stores, one per content type: `UnreviewedVodStreamIdsJson` and
`UnreviewedSeriesIdsJson`, JSON id arrays in the reviewed checkpoint's format. An id in a
tombstone store means **the user deliberately marked this title un-reviewed**.

- The **UI** writes the tombstone beside the reviewed set: un-ticking "reviewed" (single
  toggle or "Mark all matching unreviewed") adds the row's ids; re-reviewing (toggle,
  "Mark all matching reviewed", or re-including a previously excluded title) clears them.
  The stores are in the guarded-stores table, so they get the same unreadable-store
  protection and the same round-trip on every save.
- The **gate** checks the tombstones *before* the on-disk exemption. A tombstoned title
  is held like any un-reviewed title — no write, no detail fetch, no auto-review — and is
  recorded for a **removal pass** that runs where the exclusion removal runs, through the
  same `RemoveExcludedContent` and its ADR-012 safety contract (only `.strm`/`.nfo`, never
  a folder the plugin cannot prove it wrote, never a recursive delete).
- The **identity pass** carries tombstones across re-issued ids (ADR-F004), with the same
  newer-decision-wins guard as exclusions: a tombstone is not carried onto an id the user
  has since reviewed and kept. A tombstone also counts as a live decision for identity
  record pruning and backfill, so a rotated un-review cannot be stranded as "no decision".

So the full contract of un-review under the review gate is: **the mark persists, the
title is held out, and its files leave the library on the same run.** Re-reviewing brings
it back untouched.

### Failing safe on an unparseable store

`DeserializeIdSet` returns `null` for a field that will not parse. Reading that as
"nothing is tombstoned" and resuming the exemption would resurrect every deliberate
un-review — the exact bug this ADR fixes. Reading it as "hold everything" would withhold
the whole un-reviewed library. The gate takes the first cost and refuses the second: the
**exemption stands down** (on-disk titles are held, not auto-reviewed), with an error in
the log. Holding is the reversible direction; a wrongful auto-review is not. The identity
pass likewise stands down entirely, as it already does for an unparseable reviewed store.

## Consequences

- Un-review finally means something with the gate on: the title returns to the review
  queue, the library stops serving it, and neither the gate nor the exemption undoes it.
- With the gate **off**, an un-review remains a bookmark only — no files are removed and
  the sync writes everything not excluded, exactly as before. The tombstones are still
  recorded, so turning the gate on later applies them.
- Exclusion is still the stronger "never again" decision; un-review is now the reversible
  "not right now, ask me again" one. The de-dup view's help text says so.
- A tombstoned title's files are removed by the same targeted pass exclusions use, so
  orphan cleanup's safety threshold is not involved and `CleanupOrphans` being off does
  not leave stragglers.
- The held-titles log line still counts tombstoned titles, and a second log line names
  what was removed, because "the review queue looked wrong" with no evidence is how the
  original bug survived.
- Store-size reporting (`Decision stores:` trend line) covers the two new stores. The
  counts-canary line format is untouched — it is byte-compatible with users' existing
  logs and stays frozen.
- The identity pass now reads three stores; an unparseable tombstone store disables the
  pass for that run (error logged) rather than carrying the other two stores across
  without the tombstones, which would let a re-issued id resurrect a withdrawn decision.

## Implementation references

- `Emby.Xtream.Plugin/PluginConfiguration.cs` (the two stores)
- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (gate + removal pass in both syncs,
  `ReconcileMovieDecisionIdentity` step 4b)
- `Emby.Xtream.Plugin/Configuration/Web/config.js` (toggle, bulk actions, re-include,
  guarded stores, Load-time merge)
- `Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs`,
  `SyncSeriesIntegrationTests.cs`, `MovieDecisionIdentityTests.cs`
