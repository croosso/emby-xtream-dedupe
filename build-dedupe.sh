#!/usr/bin/env bash
# Fork-owned full build: both Emby targets, both test configurations, and the guards CI runs.
#
# Emby.Xtream.Plugin/build.sh is shared with upstream and is 4.9-only on both counts — it runs
# `dotnet test` with no -c (so Debug, i.e. the 4.9 path) and publishes `-c Release`. Since
# upstream's single-DLL change (their ADR-017), that one build is what releases ship on both
# Emby versions; this wrapper keeps the 4.10 SDK compiled and tested against the real test
# suite (upstream's release workflow only compiles it and runs a load check), and publishes it
# to out_4_10 as a locally buildable verification artifact.
#
# Run from anywhere:  bash build-dedupe.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="$REPO_ROOT/Emby.Xtream.Plugin"
TESTS_DIR="$REPO_ROOT/Emby.Xtream.Plugin.Tests"
OUT_4_10="$REPO_ROOT/out_4_10"

# Mirrors the derivation in build.sh. Deliberately duplicated rather than factored out: build.sh
# is upstream-shared, and extracting a helper would put fork-specific structure into a file
# upstream actively maintains. Twelve lines is the cheaper divergence.
GIT_DESC=$(git -C "$REPO_ROOT" describe --tags --match 'dedupe-v*' 2>/dev/null || echo "")
if [ -z "$GIT_DESC" ]; then
    VERSION="0.0.1"
elif echo "$GIT_DESC" | grep -qE -- '-[0-9]+-g[0-9a-f]+$'; then
    BASE=$(echo "$GIT_DESC" | sed 's/^dedupe-v//' | sed 's/-[0-9]*-g[0-9a-f]*$//')
    COMMITS=$(echo "$GIT_DESC" | grep -oE -- '-[0-9]+-g[0-9a-f]+$' | cut -d'-' -f2)
    VERSION="${BASE}.${COMMITS}"
else
    VERSION="${GIT_DESC#dedupe-v}"
fi

echo "=== Full build: version $VERSION (from git: ${GIT_DESC:-none}) ==="

# ---------------------------------------------------------------------------
# 1. Delete-site guard. Cheap, independent of the build, and reports before a
#    compile error can hide it — same ordering CI uses. The guard self-tests
#    first: a guard that cannot reject is the same problem as a test that
#    cannot fail.
# ---------------------------------------------------------------------------
if command -v python3 >/dev/null 2>&1; then
    echo ""
    echo "=== Delete-site guard ==="
    python3 "$REPO_ROOT/scripts/test-check-delete-sites.py"
    python3 "$REPO_ROOT/scripts/check-delete-sites.py"
else
    echo ""
    echo "!!! python3 not found — SKIPPING the delete-site guard."
    echo "!!! CI still enforces it, but this run has not checked it."
fi

# ---------------------------------------------------------------------------
# 2. Emby 4.9: tests + publish, through the shared script so its behaviour and
#    this one cannot drift.
# ---------------------------------------------------------------------------
echo ""
echo "=== Emby 4.9 (shared build.sh: tests + publish) ==="
dotnet restore "$TESTS_DIR/"
( cd "$PLUGIN_DIR" && bash build.sh )

# ---------------------------------------------------------------------------
# 3. Emby 4.10: tests, then publish.
#    No --no-restore: Release_4_10 has its own PackageReferences (System.Text.Json 8.x rather
#    than 6.x) that the restore above did not fetch.
#    Expect MORE tests here than in the 4.9 run — XtreamLiveStreamTests has four [Fact]s behind
#    `#if EMBY_4_10` covering AddConsumer/RemoveConsumer, which exist only in this build. A
#    differing count is correct, not a fault.
# ---------------------------------------------------------------------------
echo ""
echo "=== Emby 4.10 (tests) ==="
dotnet test "$TESTS_DIR/" -c Release_4_10 -v minimal

echo ""
echo "=== Emby 4.10 (publish) ==="
dotnet publish "$PLUGIN_DIR/Emby.Xtream.Plugin.csproj" \
    -c Release_4_10 -o "$OUT_4_10" --no-self-contained -p:Version="$VERSION"

# ---------------------------------------------------------------------------
# 4. Where everything went.
# ---------------------------------------------------------------------------
echo ""
echo "=== Build output (v$VERSION) ==="
ls -la "$PLUGIN_DIR/out/Emby.Xtream.Plugin.dll" "$OUT_4_10/Emby.Xtream.Plugin.dll"

cat <<EOF

Deploy the single-DLL build — it loads on Emby 4.9 and 4.10 alike (upstream ADR-017):

  docker cp $PLUGIN_DIR/out/Emby.Xtream.Plugin.dll <container>:/config/plugins/

Then: docker restart <container>

$OUT_4_10/Emby.Xtream.Plugin.dll is the 4.10-SDK verification build. It is compiled and tested
here so that SDK cannot drift, but releases ship the 4.9-SDK build above under both asset names.
EOF
