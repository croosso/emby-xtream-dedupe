using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// A point-in-time snapshot of the seven decision stores (ADR-C001). The reviewed, unreviewed
    /// and identity members are deliberately nullable: <c>null</c> means "the source could not
    /// be parsed", which is not the same as empty, and callers gate on that distinction — see
    /// <see cref="StrmSyncService.DeserializeIdSet"/>. The exclusion members cannot fail to parse
    /// (they round-trip as <c>int[]</c>), so they are always present.
    /// </summary>
    internal class DecisionState
    {
        public HashSet<int> ExcludedVodStreamIds = new HashSet<int>();
        public HashSet<int> ExcludedSeriesIds = new HashSet<int>();

        /// <summary>Null when the source field held something that would not parse.</summary>
        public HashSet<int> ReviewedVodStreamIds;

        /// <summary>Null when the source field held something that would not parse.</summary>
        public HashSet<int> ReviewedSeriesIds;

        /// <summary>Null when the source field held something that would not parse.</summary>
        public HashSet<int> UnreviewedVodStreamIds;

        /// <summary>Null when the source field held something that would not parse.</summary>
        public HashSet<int> UnreviewedSeriesIds;

        /// <summary>StreamId → TMDB id. Null when the source field held something that would not parse.</summary>
        public Dictionary<int, int> VodDecisionTmdbIds;

        public DecisionState Clone()
        {
            var copy = new DecisionState();
            copy.ExcludedVodStreamIds = new HashSet<int>(ExcludedVodStreamIds ?? new HashSet<int>());
            copy.ExcludedSeriesIds = new HashSet<int>(ExcludedSeriesIds ?? new HashSet<int>());
            copy.ReviewedVodStreamIds = ReviewedVodStreamIds == null
                ? null
                : new HashSet<int>(ReviewedVodStreamIds);
            copy.ReviewedSeriesIds = ReviewedSeriesIds == null
                ? null
                : new HashSet<int>(ReviewedSeriesIds);
            copy.UnreviewedVodStreamIds = UnreviewedVodStreamIds == null
                ? null
                : new HashSet<int>(UnreviewedVodStreamIds);
            copy.UnreviewedSeriesIds = UnreviewedSeriesIds == null
                ? null
                : new HashSet<int>(UnreviewedSeriesIds);
            copy.VodDecisionTmdbIds = VodDecisionTmdbIds == null
                ? null
                : new Dictionary<int, int>(VodDecisionTmdbIds);
            return copy;
        }
    }

    /// <summary>
    /// The serialized, authoritative home of the seven decision stores (ADR-C001).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stores used to live only in <see cref="PluginConfiguration"/>, serialized by Emby as
    /// part of one XML blob. Every writer — the dashboard page, a sync folding recognized titles
    /// into the reviewed checkpoint, the identity reconcile pass — did read-modify-write of that
    /// whole blob, so two concurrent writers silently lost one of them. An external approval
    /// source (Seerr webhook) would have made that routine instead of rare.
    /// </para>
    /// <para>
    /// All mutations now run through <see cref="Mutate"/>, under one in-process lock, and persist
    /// atomically to <c>decisions.json</c> under the records root (temp file + replace, so a
    /// crash can never leave a torn store). The configuration fields remain as mirrors, refreshed
    /// by the store on every mutation, so every existing mechanism that copies or restores the
    /// configuration — rollback copies, scheduled backups, restore — still carries the decisions,
    /// and the dashboard keeps displaying them without new endpoints. The mirror is never read
    /// back once the file exists; a configuration save that arrives with store values that differ
    /// from the store's is routed through <see cref="Replace"/> by
    /// <see cref="Plugin.UpdateConfiguration"/> before it lands.
    /// </para>
    /// <para>
    /// When no records root resolves (unit tests, or ApplicationPaths not yet initialized) the
    /// store falls back to configuration-backed mode: the configuration fields are the state,
    /// parsed fresh for every operation. That is exactly the pre-ADR-C001 behavior, including the
    /// fail-open handling of unparseable fields — a store that failed to read is never rebuilt,
    /// because rebuilding would silently discard every decision recorded so far.
    /// </para>
    /// <para>
    /// Internal rather than public on purpose: Emby's SimpleInjector scan instantiates public
    /// service classes before <see cref="Plugin.Instance"/> exists, and this class has an
    /// ILogger-shaped constructor (see the note on ContentExclusionFilter).
    /// </para>
    /// </remarks>
    internal sealed class DecisionStore
    {
        /// <summary>The authoritative store file, under the records root.</summary>
        internal const string StoreFileName = "decisions.json";

        /// <summary>Timestamped copies taken before wholesale replaces; sibling of the store file.</summary>
        internal const string DecisionCopiesFolderName = "decision-copies";

        /// <summary>
        /// How many pre-replace copies to keep. Deliberately not a configuration field: the mirrors
        /// inside every configuration copy already give the decisions a longer, richer history,
        /// and these copies exist only to cover the window since the last configuration save.
        /// </summary>
        private const int CopyKeepCount = 10;

        /// <summary>Registry key for configuration-backed mode (no records root resolved).</summary>
        private const string ConfigModeKey = "(config)";

        /// <summary>
        /// Current on-disk format. 2 added the unreviewed-tombstone stores (ADR-F008): a build that
        /// predates them reads a v2 file as read-only (the version gate below) rather than load
        /// it, persist, and silently drop the tombstones from the file. Version 1 files are still
        /// read — their tombstone members are seeded from the configuration mirrors, which are
        /// the only tombstone record a v1-era build kept — and are upgraded to v2 on first write.
        /// </summary>
        private const int StoreFormatVersion = 2;

        /// <summary>The oldest file version this build can read.</summary>
        private const int OldestReadableStoreFormatVersion = 1;

        /// <summary>Version of the file actually loaded, for the v1 seeding above. 0 until a load.</summary>
        private int _loadedFileVersion;

        private static readonly ConcurrentDictionary<string, DecisionStore> Stores =
            new ConcurrentDictionary<string, DecisionStore>(StringComparer.OrdinalIgnoreCase);

        private readonly object _gate = new object();

        /// <summary>Null in configuration-backed mode.</summary>
        private readonly string _path;

        private readonly string _copiesDir;
        private readonly ILogger _logger;

        /// <summary>Cached state, file-backed mode only. Lazily loaded under the lock.</summary>
        private DecisionState _state;

        private bool _loaded;

        /// <summary>
        /// Set when the store file exists but this build cannot read it (a newer format version).
        /// The file is left untouched rather than overwritten: destroying a store a newer plugin
        /// wrote is the same failure class as deleting a folder the sync just wrote.
        /// </summary>
        private bool _readOnly;

        /// <summary>
        /// Set when the store file cannot be initialized or written (an unwritable records root,
        /// a full disk). The store degrades to configuration-backed behavior instead of failing
        /// the sync that feeds it — the same rule as the counts log and the rollback copy: a
        /// record that can break the thing it documents is worse than no record.
        /// </summary>
        private bool _degraded;

        private DecisionStore(string recordsRoot, ILogger logger)
        {
            _logger = logger;
            if (!string.IsNullOrEmpty(recordsRoot))
            {
                _path = Path.Combine(recordsRoot, StoreFileName);
                _copiesDir = Path.Combine(recordsRoot, DecisionCopiesFolderName);
            }
        }

        /// <summary>
        /// Returns the process-wide store for the given records root. One store per root, because
        /// a restore can relocate the root and the records either side of that move are separate
        /// histories (see the relocation note in RestoreConfiguration).
        /// </summary>
        internal static DecisionStore GetOrCreate(string recordsRoot, ILogger logger)
        {
            var key = string.IsNullOrEmpty(recordsRoot) ? ConfigModeKey : recordsRoot;
            return Stores.GetOrAdd(key, k => new DecisionStore(recordsRoot, logger));
        }

        /// <summary>
        /// A fresh instance for a records root that the registry already holds. For tests that
        /// need to observe what a restarted process would read from disk — the shared instance's
        /// cached state would hide a file that failed to round-trip.
        /// </summary>
        internal static DecisionStore CreateUnsharedForTests(string recordsRoot, ILogger logger)
        {
            return new DecisionStore(recordsRoot, logger);
        }

        /// <summary>True when decisions persist to <c>decisions.json</c> rather than the configuration.</summary>
        internal bool FileBacked
        {
            get { return _path != null; }
        }

        /// <summary>
        /// Returns a snapshot of the current state. The reviewed and identity members follow the
        /// fail-open contract: null means unreadable, and callers must stand down rather than read
        /// null as "nothing reviewed".
        /// </summary>
        internal DecisionState Read(PluginConfiguration config)
        {
            lock (_gate)
            {
                EnsureLoaded(config);
                return _state.Clone();
            }
        }

        /// <summary>
        /// Runs <paramref name="body"/> against the current state under the store's lock, then
        /// persists whatever the body left. This is the single mutation path: no caller writes the
        /// stores any other way, which is what makes concurrent writers safe.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The body mutates the state in place and may return a value for the caller. A member
        /// that was null on load (unreadable source) is written back only if the body replaced it
        /// with something readable — an unreadable store is left untouched for repair, never
        /// rebuilt, for the reason its own error messages give.
        /// </para>
        /// <para>
        /// In configuration-backed mode the state is parsed fresh from the configuration on every
        /// call, so there is no cached copy to go stale between calls.
        /// </para>
        /// </remarks>
        internal T Mutate<T>(PluginConfiguration config, Func<DecisionState, T> body)
        {
            if (body == null)
            {
                throw new ArgumentNullException("body");
            }

            lock (_gate)
            {
                EnsureLoaded(config);
                var result = body(_state);
                PersistLocked(config);
                return result;
            }
        }

        /// <summary>
        /// Folds ids into a reviewed checkpoint. Additive by design: the review gate never marks
        /// anything un-reviewed, and merging into the current state under the lock is what stops
        /// the fold clobbering a concurrent decision made since the sync started.
        /// </summary>
        internal void AddReviewed(PluginConfiguration config, bool seriesIds, IEnumerable<int> ids)
        {
            if (ids == null)
            {
                return;
            }

            Mutate(config, state =>
            {
                var set = seriesIds ? state.ReviewedSeriesIds : state.ReviewedVodStreamIds;
                var fieldName = seriesIds ? "ReviewedSeriesIdsJson" : "ReviewedVodStreamIdsJson";
                if (set == null)
                {
                    _logger.Error(
                        "{0} could not be parsed, so reviewed mark(s) from this sync were not recorded. The unreadable "
                        + "store is left untouched rather than rebuilt — rebuilding would silently discard every "
                        + "decision recorded so far. Check the plugin configuration file.",
                        fieldName);
                    return 0;
                }

                var added = 0;
                foreach (var id in ids)
                {
                    if (set.Add(id))
                    {
                        added++;
                    }
                }

                return added;
            });
        }

        /// <summary>
        /// Applies another configuration's stores wholesale — the dashboard's full-list save
        /// semantics and a restore's re-import. A store in <paramref name="incoming"/> that will
        /// not parse is NOT applied: the store keeps its current value for it, exactly as an
        /// unreadable field is never trusted on read.
        /// </summary>
        internal void Replace(PluginConfiguration config, PluginConfiguration incoming)
        {
            Mutate(config, state =>
            {
                if (incoming == null)
                {
                    return false;
                }

                TakeCopyBeforeReplaceLocked();

                state.ExcludedVodStreamIds = ContentExclusionFilter.BuildSet(incoming.ExcludedVodStreamIds);
                state.ExcludedSeriesIds = ContentExclusionFilter.BuildSet(incoming.ExcludedSeriesIds);

                var reviewedVod = StrmSyncService.DeserializeIdSet(incoming.ReviewedVodStreamIdsJson);
                if (reviewedVod != null)
                {
                    state.ReviewedVodStreamIds = reviewedVod;
                }
                else if (!string.IsNullOrEmpty(incoming.ReviewedVodStreamIdsJson))
                {
                    _logger.Warn(
                        "The saved configuration's ReviewedVodStreamIdsJson could not be parsed. The decision store "
                        + "keeps the reviewed marks it already has; the unreadable value still lands in the "
                        + "configuration, where the review gate will fail open rather than withhold the catalogue.");
                }

                var reviewedSeries = StrmSyncService.DeserializeIdSet(incoming.ReviewedSeriesIdsJson);
                if (reviewedSeries != null)
                {
                    state.ReviewedSeriesIds = reviewedSeries;
                }
                else if (!string.IsNullOrEmpty(incoming.ReviewedSeriesIdsJson))
                {
                    _logger.Warn(
                        "The saved configuration's ReviewedSeriesIdsJson could not be parsed. The decision store "
                        + "keeps the reviewed marks it already has; the unreadable value still lands in the "
                        + "configuration, where the review gate will fail open rather than withhold the catalogue.");
                }

                var tmdb = StrmSyncService.DeserializeTmdbMap(incoming.VodDecisionTmdbIdsJson);
                if (tmdb != null)
                {
                    state.VodDecisionTmdbIds = tmdb;
                }
                else if (!string.IsNullOrEmpty(incoming.VodDecisionTmdbIdsJson))
                {
                    _logger.Warn(
                        "The saved configuration's VodDecisionTmdbIdsJson could not be parsed. The decision store "
                        + "keeps the identity records it already has, for repair rather than rebuild.");
                }

                // The tombstone stores follow the same rule as the reviewed ones. A dashboard
                // save is the ONLY writer that can remove a tombstone wholesale (re-reviewing),
                // so not applying a readable value here would make an un-review impossible to
                // undo through the UI; keeping an unreadable one would resurrect a withdrawn
                // title on the next sync instead.
                var unreviewedVod = StrmSyncService.DeserializeIdSet(incoming.UnreviewedVodStreamIdsJson);
                if (unreviewedVod != null)
                {
                    state.UnreviewedVodStreamIds = unreviewedVod;
                }
                else if (!string.IsNullOrEmpty(incoming.UnreviewedVodStreamIdsJson))
                {
                    _logger.Warn(
                        "The saved configuration's UnreviewedVodStreamIdsJson could not be parsed. The decision "
                        + "store keeps the un-review marks it already has; the unreadable value still lands in the "
                        + "configuration, where the review gate will fail open rather than resurrect a withdrawal.");
                }

                var unreviewedSeries = StrmSyncService.DeserializeIdSet(incoming.UnreviewedSeriesIdsJson);
                if (unreviewedSeries != null)
                {
                    state.UnreviewedSeriesIds = unreviewedSeries;
                }
                else if (!string.IsNullOrEmpty(incoming.UnreviewedSeriesIdsJson))
                {
                    _logger.Warn(
                        "The saved configuration's UnreviewedSeriesIdsJson could not be parsed. The decision "
                        + "store keeps the un-review marks it already has; the unreadable value still lands in the "
                        + "configuration, where the review gate will fail open rather than resurrect a withdrawal.");
                }

                return true;
            });
        }

        /// <summary>
        /// True when two configurations carry the same decisions. Compare parsed values, not the
        /// raw fields: the same set can serialize with different spacing or ordering, and treating
        /// that as a change would push every ordinary settings-only save through the store.
        /// </summary>
        internal static bool DecisionStoresEqual(PluginConfiguration a, PluginConfiguration b)
        {
            if (a == null || b == null)
            {
                return true;
            }

            if (!ContentExclusionFilter.BuildSet(a.ExcludedVodStreamIds)
                .SetEquals(ContentExclusionFilter.BuildSet(b.ExcludedVodStreamIds)))
            {
                return false;
            }

            if (!ContentExclusionFilter.BuildSet(a.ExcludedSeriesIds)
                .SetEquals(ContentExclusionFilter.BuildSet(b.ExcludedSeriesIds)))
            {
                return false;
            }

            if (!SameParsedSet(
                StrmSyncService.DeserializeIdSet(a.ReviewedVodStreamIdsJson),
                StrmSyncService.DeserializeIdSet(b.ReviewedVodStreamIdsJson)))
            {
                return false;
            }

            if (!SameParsedSet(
                StrmSyncService.DeserializeIdSet(a.ReviewedSeriesIdsJson),
                StrmSyncService.DeserializeIdSet(b.ReviewedSeriesIdsJson)))
            {
                return false;
            }

            // The tombstone stores must be compared for the same reason they must be Replaced:
            // a save that only toggles an un-review is a decision-store change, and treating it
            // as settings-only would let the store overwrite it with the pre-save value.
            if (!SameParsedSet(
                StrmSyncService.DeserializeIdSet(a.UnreviewedVodStreamIdsJson),
                StrmSyncService.DeserializeIdSet(b.UnreviewedVodStreamIdsJson)))
            {
                return false;
            }

            if (!SameParsedSet(
                StrmSyncService.DeserializeIdSet(a.UnreviewedSeriesIdsJson),
                StrmSyncService.DeserializeIdSet(b.UnreviewedSeriesIdsJson)))
            {
                return false;
            }

            return SameParsedMap(
                StrmSyncService.DeserializeTmdbMap(a.VodDecisionTmdbIdsJson),
                StrmSyncService.DeserializeTmdbMap(b.VodDecisionTmdbIdsJson));
        }

        private static bool SameParsedSet(HashSet<int> x, HashSet<int> y)
        {
            // Both unreadable counts as equal: there is nothing to route through the store, and
            // the unreadable value reaches the configuration either way, as it always has.
            if (x == null || y == null)
            {
                return x == y;
            }

            return x.SetEquals(y);
        }

        private static bool SameParsedMap(Dictionary<int, int> x, Dictionary<int, int> y)
        {
            if (x == null || y == null)
            {
                return x == y;
            }

            if (x.Count != y.Count)
            {
                return false;
            }

            foreach (var kv in x)
            {
                int value;
                if (!y.TryGetValue(kv.Key, out value) || value != kv.Value)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Loads the state for the current call. File-backed: lazily migrates from the
        /// configuration mirrors on first use, or reads the store file. Configuration-backed:
        /// parses the configuration fresh, so no state outlives the call. Never throws: an
        /// unusable records root degrades the store to configuration-backed behavior rather than
        /// fail the sync.
        /// </summary>
        private void EnsureLoaded(PluginConfiguration config)
        {
            if (_path == null)
            {
                _state = ParseFromConfig(config);
                return;
            }

            if (_loaded)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));

                if (File.Exists(_path))
                {
                    var loaded = TryReadFile();
                    if (loaded != null)
                    {
                        // A version 1 file predates the tombstone stores, so its tombstone members
                        // read as null — which would otherwise mean "unreadable" and stand the
                        // review gate's exemption down. The configuration mirrors hold the only
                        // tombstone record a v1-era build kept, so they seed those members. The
                        // next write upgrades the file to version 2 and the members round-trip
                        // like every other store.
                        if (_loadedFileVersion == 1)
                        {
                            var fromConfig = ParseFromConfig(config);
                            loaded.UnreviewedVodStreamIds = fromConfig.UnreviewedVodStreamIds;
                            loaded.UnreviewedSeriesIds = fromConfig.UnreviewedSeriesIds;
                        }

                        _state = loaded;
                        _loaded = true;
                        return;
                    }

                    if (_readOnly)
                    {
                        // A format this build does not understand. Do not load from it, do not
                        // write over it, and do not retry on every call — the mirrors are the
                        // readable fallback and writes stay in them until the plugin is updated.
                        _state = ParseFromConfig(config);
                        _loaded = true;
                        return;
                    }
                }
                else
                {
                    // First use: the configuration mirrors are the only record of the decisions
                    // that exists, so they seed the store file. From here on the file is
                    // authoritative and the mirrors are only ever written, never read back.
                    _state = ParseFromConfig(config);
                    _loaded = true;
                    WriteFileLocked(_state);
                    return;
                }

                // An existing file that would not read. It has been quarantined for inspection
                // (see TryReadFile), so the mirrors — which the store keeps fresh, and which
                // every configuration copy carries — become the recovery source. Both paths
                // fail open rather than treat the damage as "no decisions": a store that fails
                // to read must never read as "nothing reviewed".
                _state = ParseFromConfig(config);
                _loaded = true;
            }
            catch (Exception ex)
            {
                _degraded = true;
                _loaded = true;
                _state = ParseFromConfig(config);
                if (_logger != null)
                {
                    _logger.Error(
                        "The decision store at {0} could not be initialized ({1}). Decisions fall back to the "
                        + "configuration for this run, which still records every decision — only the protection "
                        + "against concurrent writers is lost until the folder is writable again.",
                        _path, ex.Message);
                }
            }
        }

        /// <summary>
        /// Parses the state out of a configuration's mirror fields. Nulls are preserved: they are
        /// the unreadable marker the fail-open contract depends on.
        /// </summary>
        private static DecisionState ParseFromConfig(PluginConfiguration config)
        {
            var state = new DecisionState();
            if (config != null)
            {
                state.ExcludedVodStreamIds = ContentExclusionFilter.BuildSet(config.ExcludedVodStreamIds);
                state.ExcludedSeriesIds = ContentExclusionFilter.BuildSet(config.ExcludedSeriesIds);
                state.ReviewedVodStreamIds = StrmSyncService.DeserializeIdSet(config.ReviewedVodStreamIdsJson);
                state.ReviewedSeriesIds = StrmSyncService.DeserializeIdSet(config.ReviewedSeriesIdsJson);
                state.UnreviewedVodStreamIds = StrmSyncService.DeserializeIdSet(config.UnreviewedVodStreamIdsJson);
                state.UnreviewedSeriesIds = StrmSyncService.DeserializeIdSet(config.UnreviewedSeriesIdsJson);
                state.VodDecisionTmdbIds = StrmSyncService.DeserializeTmdbMap(config.VodDecisionTmdbIdsJson);
            }

            return state;
        }

        /// <summary>Persists the state after a mutation: file first, then the mirrors.</summary>
        private void PersistLocked(PluginConfiguration config)
        {
            if (_path != null && !_readOnly && !_degraded)
            {
                try
                {
                    WriteFileLocked(_state);
                }
                catch (Exception ex)
                {
                    // Degrade rather than fail the writer: the mirrors below still record every
                    // decision, and a retry next call would only throw again.
                    _degraded = true;
                    if (_logger != null)
                    {
                        _logger.Error(
                            "The decision store at {0} could not be written ({1}). Decisions still go to the "
                            + "configuration, but are unprotected against concurrent writers until the folder "
                            + "is writable again.",
                            _path, ex.Message);
                    }
                }
            }

            WriteMirrors(config, _state);
        }

        /// <summary>
        /// Refreshes the configuration mirror fields from the state. Called after every mutation,
        /// which is what keeps every mechanism that copies, backs up or restores the
        /// configuration carrying an up-to-date record of the decisions. Unreadable (null)
        /// members are skipped so an unreadable mirror is never silently "repaired" into an empty
        /// one — the failure must stay visible until a readable value replaces it.
        /// </summary>
        private static void WriteMirrors(PluginConfiguration config, DecisionState state)
        {
            if (config == null)
            {
                return;
            }

            config.ExcludedVodStreamIds = OrderById(state.ExcludedVodStreamIds);
            config.ExcludedSeriesIds = OrderById(state.ExcludedSeriesIds);

            if (state.ReviewedVodStreamIds != null)
            {
                config.ReviewedVodStreamIdsJson = StrmSyncService.SerializeIdSet(state.ReviewedVodStreamIds);
            }

            if (state.ReviewedSeriesIds != null)
            {
                config.ReviewedSeriesIdsJson = StrmSyncService.SerializeIdSet(state.ReviewedSeriesIds);
            }

            if (state.UnreviewedVodStreamIds != null)
            {
                config.UnreviewedVodStreamIdsJson = StrmSyncService.SerializeIdSet(state.UnreviewedVodStreamIds);
            }

            if (state.UnreviewedSeriesIds != null)
            {
                config.UnreviewedSeriesIdsJson = StrmSyncService.SerializeIdSet(state.UnreviewedSeriesIds);
            }

            if (state.VodDecisionTmdbIds != null)
            {
                config.VodDecisionTmdbIdsJson = StrmSyncService.SerializeTmdbMap(state.VodDecisionTmdbIds);
            }
        }

        private static int[] OrderById(HashSet<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return new int[0];
            }

            var ordered = ids.ToList();
            ordered.Sort();
            return ordered.ToArray();
        }

        /// <summary>The on-disk shape. Nulls are written on purpose: they record "unreadable".</summary>
        private sealed class DecisionFileState
        {
            public int Version { get; set; }

            public List<int> ExcludedVodStreamIds { get; set; }

            public List<int> ExcludedSeriesIds { get; set; }

            public List<int> ReviewedVodStreamIds { get; set; }

            public List<int> ReviewedSeriesIds { get; set; }

            /// <summary>Added in format version 2; absent in a version 1 file.</summary>
            public List<int> UnreviewedVodStreamIds { get; set; }

            /// <summary>Added in format version 2; absent in a version 1 file.</summary>
            public List<int> UnreviewedSeriesIds { get; set; }

            public Dictionary<string, int> VodDecisionTmdbIds { get; set; }
        }

        private DecisionState TryReadFile()
        {
            try
            {
                var fileState = STJ.JsonSerializer.Deserialize<DecisionFileState>(File.ReadAllText(_path));
                if (fileState == null)
                {
                    return null;
                }

                if (fileState.Version != StoreFormatVersion
                    && fileState.Version != OldestReadableStoreFormatVersion)
                {
                    // A version this build does not understand was written by a newer build. Keep
                    // the file: overwriting it would destroy decisions we cannot even read.
                    _logger.Error(
                        "{0} was written in an unknown format (version {1}, expected {2}). The decision store is "
                        + "read-only for this run and reads fall back to the configuration; the file is left "
                        + "untouched. Update to the plugin version that wrote it.",
                        _path, fileState.Version, StoreFormatVersion);
                    _readOnly = true;
                    return null;
                }

                var state = new DecisionState();
                state.ExcludedVodStreamIds = new HashSet<int>(fileState.ExcludedVodStreamIds ?? new List<int>());
                state.ExcludedSeriesIds = new HashSet<int>(fileState.ExcludedSeriesIds ?? new List<int>());
                state.ReviewedVodStreamIds = fileState.ReviewedVodStreamIds == null
                    ? null
                    : new HashSet<int>(fileState.ReviewedVodStreamIds);
                state.ReviewedSeriesIds = fileState.ReviewedSeriesIds == null
                    ? null
                    : new HashSet<int>(fileState.ReviewedSeriesIds);

                // Version 1 predates the tombstone stores, so the fields are absent rather than
                // empty-or-unreadable; EnsureLoaded seeds them from the configuration mirrors.
                // In a v2 file, absent and null both mean the tombstone store could not be parsed
                // when it was written, and the fail-open null contract applies as for every
                // other JSON store.
                if (fileState.Version >= StoreFormatVersion)
                {
                    state.UnreviewedVodStreamIds = fileState.UnreviewedVodStreamIds == null
                        ? null
                        : new HashSet<int>(fileState.UnreviewedVodStreamIds);
                    state.UnreviewedSeriesIds = fileState.UnreviewedSeriesIds == null
                        ? null
                        : new HashSet<int>(fileState.UnreviewedSeriesIds);
                }

                _loadedFileVersion = fileState.Version;

                state.VodDecisionTmdbIds = new Dictionary<int, int>();
                if (fileState.VodDecisionTmdbIds != null)
                {
                    foreach (var kv in fileState.VodDecisionTmdbIds)
                    {
                        int streamId;
                        if (int.TryParse(kv.Key, NumberStyles.None, CultureInfo.InvariantCulture, out streamId)
                            && streamId > 0 && kv.Value > 0)
                        {
                            state.VodDecisionTmdbIds[streamId] = kv.Value;
                        }
                    }
                }

                if (fileState.VodDecisionTmdbIds == null)
                {
                    state.VodDecisionTmdbIds = null;
                }

                return state;
            }
            catch (Exception ex)
            {
                // Quarantine rather than delete: the file is the user's accumulated decisions,
                // unreadable is not the same as worthless, and the copy is what anyone repairing
                // it will need. Same contract as the configuration's own unreadable-store rules.
                try
                {
                    Directory.CreateDirectory(_copiesDir);
                    var quarantine = Path.Combine(
                        _copiesDir,
                        "decisions-corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                    File.Move(_path, quarantine);
                    _logger.Error(
                        "{0} could not be read ({1}). It was moved to {2} for inspection, and the decision stores "
                        + "fall back to the configuration's own copies of them. Check the store-size log before "
                        + "assuming anything was lost.",
                        _path, ex.Message, quarantine);
                }
                catch
                {
                    _logger.Error(
                        "{0} could not be read ({1}) and could not be moved aside. The decision stores fall back to "
                        + "the configuration's own copies of them.",
                        _path, ex.Message);
                }

                return null;
            }
        }

        private void WriteFileLocked(DecisionState state)
        {
            var fileState = new DecisionFileState();
            fileState.Version = StoreFormatVersion;
            fileState.ExcludedVodStreamIds = OrderedList(state.ExcludedVodStreamIds);
            fileState.ExcludedSeriesIds = OrderedList(state.ExcludedSeriesIds);
            fileState.ReviewedVodStreamIds = state.ReviewedVodStreamIds == null
                ? null
                : OrderedList(state.ReviewedVodStreamIds);
            fileState.ReviewedSeriesIds = state.ReviewedSeriesIds == null
                ? null
                : OrderedList(state.ReviewedSeriesIds);
            fileState.UnreviewedVodStreamIds = state.UnreviewedVodStreamIds == null
                ? null
                : OrderedList(state.UnreviewedVodStreamIds);
            fileState.UnreviewedSeriesIds = state.UnreviewedSeriesIds == null
                ? null
                : OrderedList(state.UnreviewedSeriesIds);

            if (state.VodDecisionTmdbIds == null)
            {
                fileState.VodDecisionTmdbIds = null;
            }
            else
            {
                var ordered = state.VodDecisionTmdbIds.Keys.ToList();
                ordered.Sort();
                var output = new Dictionary<string, int>(ordered.Count);
                foreach (var id in ordered)
                {
                    output[id.ToString(CultureInfo.InvariantCulture)] = state.VodDecisionTmdbIds[id];
                }

                fileState.VodDecisionTmdbIds = output;
            }

            var json = STJ.JsonSerializer.Serialize(fileState);

            // Temp file + replace, never a direct overwrite: a crash mid-write must not be able
            // to leave a torn store, because the next start would read the damage as the truth.
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(_path))
            {
                try
                {
                    File.Replace(temp, _path, null);
                }
                catch (IOException)
                {
                    // File.Replace can refuse on some filesystems (bind mounts, some network
                    // shares). A same-volume move is the fallback; the copy then delete is not
                    // atomic, but it is the best available there and the mirrors still hold the
                    // same values.
                    File.Copy(temp, _path, true);
                    TryDelete(temp);
                }
            }
            else
            {
                File.Move(temp, _path);
            }
        }

        /// <summary>
        /// Copies the store file aside before a wholesale replace. The mirrors inside every
        /// configuration copy already give the decisions a long history; this covers only the
        /// window since the last configuration save — replace is rare (a dashboard save or a
        /// restore that actually changed decisions), so the copies stay cheap.
        /// </summary>
        private void TakeCopyBeforeReplaceLocked()
        {
            if (_path == null || _readOnly || !File.Exists(_path))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_copiesDir);
                var copy = Path.Combine(
                    _copiesDir,
                    "decisions-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                File.Copy(_path, copy, true);
                PruneCopiesLocked();
            }
            catch (Exception ex)
            {
                // Refusing to replace because a safety copy failed would be worse than not having
                // one — same contract as the configuration's rollback snapshot.
                _logger.Warn("Could not take a pre-change copy of {0}: {1}", _path, ex.Message);
            }
        }

        private void PruneCopiesLocked()
        {
            try
            {
                var copies = Directory.Exists(_copiesDir)
                    ? Directory.GetFiles(_copiesDir, "decisions-*.json")
                    : new string[0];
                if (copies.Length <= CopyKeepCount)
                {
                    return;
                }

                Array.Sort(copies, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < copies.Length - CopyKeepCount; i++)
                {
                    TryDelete(copies[i]);
                }
            }
            catch
            {
                // Pruning is housekeeping; a failure here must never block the mutation.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                // delete-ok: only called on files this store wrote itself — a .tmp beside the
                // store file, or a pruned pre-replace copy past its keep count. Never user content.
                File.Delete(path);
            }
            catch
            {
                // A leftover .tmp is cosmetic; the next write replaces it.
            }
        }

        private static List<int> OrderedList(HashSet<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return new List<int>();
            }

            var ordered = ids.ToList();
            ordered.Sort();
            return ordered;
        }
    }
}
