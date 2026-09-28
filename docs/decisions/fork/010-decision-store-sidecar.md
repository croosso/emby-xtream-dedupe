# ADR-F010: Own the Decision Stores Instead of the Configuration Blob

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-27
**Status**: ACCEPTED
**Affects**: `Service/DecisionStore.cs` (new), `StrmSyncService` (reconcile pass, review
gates, review folds, exclusion reads), `Plugin.UpdateConfiguration`,
`scripts/repair-id-churn.py` (install steps), `stryker-config.json` (line-range shift)

---

## Context

The five decision stores — `ExcludedVodStreamIds`, `ExcludedSeriesIds`,
`ReviewedVodStreamIdsJson`, `ReviewedSeriesIdsJson`, `VodDecisionTmdbIdsJson` — are the
expensive, irreplaceable part of this plugin's state: tens of thousands of individual
decisions that cannot be reconstructed (ADR-F005). They live as fields of
`PluginConfiguration`, serialized by Emby into one XML blob alongside every ordinary
setting.

Every writer of those fields does read-modify-write of the whole blob:

- the dashboard's review UI — it holds the whole configuration in the page and posts it back;
- the sync's review-gate fold — it loads the reviewed checkpoint at the start of a run and
  serializes the whole set back at the end;
- the identity reconcile pass (ADR-F004 stage 3) — it rewrites all three movie stores at
  sync start.

An external approval source was the next planned writer: a Seerr webhook marking titles
reviewed on request approval. That would take the existing races from "rare and
unexplained" to "routine".

## Problem

Two concurrent writers of a shared XML blob silently lose one of them. Concretely, with
today's writers:

- a sync that loaded its checkpoint before a dashboard save lands will **serialize its stale
  copy over the save's decisions** when the review fold runs — the user's just-made
  exclusion vanishes with nothing in any log;
- the reconcile pass holds all three movie stores for the duration of its catalogue
  fetch-and-compute, so a decision made meanwhile is lost at write-back;
- there is no way for a programmatic writer to add one ID without republishing every store.

Emby's SDK offers nothing finer than the one XML blob: `BasePlugin<T>` has no field-level
or transactional API. And the fork ships a single DLL by design ("Only the single DLL file
is needed"), which rules out a bundled database engine.

## Alternatives considered

1. **Serialize writers with a lock, keep the fields as the store.** Fixes
   writer-vs-writer ordering but not the stale-copy problem: a writer that read its
   checkpoint ten minutes ago still publishes that stale copy over decisions made since.
   The fold's lost-update survives the lock.
2. **Lock inside `Plugin.UpdateConfiguration` and merge.** Cannot distinguish "the user
   removed this title" from "the page was loaded before a concurrent write" — a whole-list
   save carries no intent. Merging additively would make un-reviewing impossible from the
   UI.
3. **Store decisions in the Emby library (item metadata).** Coupled to scan state and
   re-reads; exclusions frequently refer to titles with no item on disk at all.
4. **A bundled SQLite/LiteDB sidecar.** Violates the single-DLL deployment guarantee that
   the README's install instructions rest on.

## Decision

The decisions move into a plugin-owned store, and the configuration fields become mirrors
the store keeps fresh.

- **`DecisionStore`** (`Service/DecisionStore.cs`) owns the five stores. One instance per
  records root, shared process-wide (Emby constructs service classes independently of
  `Plugin`, so instance state would give two writers two stores).
- **All mutations run under one lock** through `Mutate`, and persist atomically —
  temp file + `File.Replace` — to `decisions.json` under the records root, so a crash can
  never leave a torn store.
- **The sync folds additively** (`AddReviewed`) into the *current* state instead of
  republishing a checkpoint read at run start; **the reconcile pass computes and writes as
  one locked operation.**
- **The configuration fields stay, as mirrors**, refreshed by the store on every mutation.
  This is deliberate: every existing mechanism that copies, backs up, or restores the
  configuration — the ADR-F005 rollback copies, `BackupConfigurationTask`, the restore
  path — continues to carry the decisions unchanged, and the dashboard keeps displaying
  them with no new endpoints and no `config.js` changes.
- **Configuration saves route through the store.** `Plugin.UpdateConfiguration` — the one
  path every dashboard save and every restore already takes — compares the incoming
  stores with the current ones (parsed, not textual) and, if they differ, applies them
  through the store before the save lands. A settings-only save never touches the store.
- **Configuration-backed mode** when no records root resolves (unit tests, or
  ApplicationPaths not yet initialized): the fields are the state, parsed fresh per
  operation — exactly the pre-ADR-F010 behavior, including the fail-open handling of
  unparseable fields.
- **An unreadable store is never rebuilt.** The null-vs-empty contract of
  `DeserializeIdSet` is preserved end to end: an unreadable reviewed store disables the
  review gate rather than withholding the catalogue, and a store that fails to read is
  quarantined for inspection, not "repaired" into an empty one. A store file in an unknown
  (newer) format makes the store read-only and falls back to the mirrors — an older build
  must not destroy what a newer build wrote.
- **The store never fails the sync.** An unwritable records root degrades the store to
  configuration-backed behavior with one error logged — the same rule as the counts log
  and the rollback copy: a record that can break the thing it documents is worse than no
  record.

## Consequences

- Sync-vs-dashboard, sync-vs-webhook, and webhook-vs-webhook lost updates are gone. The
  dashboard's own stale-page overwrite of a concurrent change remains — inherent to a web
  form that edits full lists — and is unchanged from before.
- A hand-edited configuration (or an offline `repair-id-churn.py` candidate) no longer
  takes effect once `decisions.json` exists: the file is authoritative. The repair script
  now prints the store's path and an install step to move it aside, letting the store
  re-seed from the repaired mirrors. This is the one workflow the ADR knowingly changes.
- `stryker-config.json`'s line-based mutation range over `StrmSyncService.cs` was shifted
  (+45) to keep covering the same code.
- The next step this enables — a REST endpoint that marks titles reviewed for a Seerr
  integration — becomes a thin wrapper over `AddReviewed` rather than a new concurrency
  problem.
