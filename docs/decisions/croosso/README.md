# Local decision records

This directory holds ADRs for decisions made in **this repo**
(`croosso/emby-xtream-dedupe`) — a fork of `andyj682/emby-xtream-dedupe`, which is itself
a fork of `firestaerter3/emby-xtream`.

## Why another namespace

The `F` sequence in [`../fork/`](../fork/README.md) solved the upstream collision: this
lineage and upstream both allocated from `001, 002, 003 …` and silently collided (two
different `016`s, `017`s and `018`s existed before the fork ADRs were renumbered).

The same problem exists one level down, and it has already fired twice. This repo and
`andyj682` both allocate from `F`: both minted a different **F008** (unreview tombstones
here, publish-the-wanted-set there — resolved by renumbering theirs to F009), and
ADR-C001 here sat one number ahead of whatever `andyj682` allocates next. A future merge
would bring their F010 in under a different filename — git reports nothing, and every
`see ADR-C001` in the tree stops naming one document.

So decisions authored in this repo live here, in their own sequence, cited as
**`ADR-C001`**, `ADR-C002`, and so on. `andyj682` cannot collide with a directory it
does not know exists, and a bare `ADR-Fnnn` anywhere in this tree keeps meaning exactly
what it means in theirs.

## Conventions

- Filenames are `NNN-kebab-case-title.md`, numbered from `001` in **this** directory.
- Cite them in code, docs and commit messages as `ADR-C001`, never `ADR-001` or `ADR-F001`.
- `../fork/` keeps the ADRs of the andyj682 lineage — inherited ones stay there, cited
  by their `F` numbers exactly as in that repo. Do not re-file them here.
- Never renumber an existing local ADR. If one is superseded, add a new one and mark the
  old one `SUPERSEDED BY ADR-Cnnn`.
- If a decision from here is ever contributed upstream (to `andyj682` or beyond), it will
  be renumbered into the target's sequence at that point — the `C` number is this repo's
  citation, not a portable one.

## Index

| ADR | Title | Status |
| --- | --- | --- |
| [ADR-C001](001-decision-store-sidecar.md) | Own the decision stores instead of the configuration blob | Accepted |
