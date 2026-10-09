using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for the serialized decision store (ADR-F010).
    ///
    /// The five decision stores used to be fields of the plugin configuration and every writer
    /// did read-modify-write of that whole blob, so two concurrent writers silently lost one of
    /// them. These tests pin the properties the store exists to guarantee: writes are merged
    /// under a lock, the store file is authoritative once it exists, an unreadable store is
    /// never "repaired" into an empty one, and configuration-backed mode (no records root) is
    /// exactly the pre-ADR-F010 behavior the existing suite depends on.
    /// </summary>
    public class DecisionStoreTests
    {
        private static PluginConfiguration ConfigWith(
            int[] excludedVod = null,
            string reviewedVod = null,
            string reviewedSeries = null,
            string tmdb = null)
        {
            return new PluginConfiguration
            {
                ExcludedVodStreamIds = excludedVod ?? new int[0],
                ReviewedVodStreamIdsJson = reviewedVod ?? string.Empty,
                ReviewedSeriesIdsJson = reviewedSeries ?? string.Empty,
                VodDecisionTmdbIdsJson = tmdb ?? string.Empty,
            };
        }

        // -----------------------------------------------------------------
        // Configuration-backed mode (no records root): today's behavior
        // -----------------------------------------------------------------

        [Fact]
        public void ConfigBacked_AddReviewed_WritesMirrorFieldAdditively()
        {
            var config = ConfigWith(reviewedVod: "[1,2]");
            var store = DecisionStore.GetOrCreate(null, new NullLogger());

            store.AddReviewed(config, false, new[] { 2, 3 });

            var set = StrmSyncService.DeserializeIdSet(config.ReviewedVodStreamIdsJson);
            Assert.NotNull(set);
            Assert.Equal(new HashSet<int> { 1, 2, 3 }, set);
        }

        [Fact]
        public void ConfigBacked_UnreadableReviewedStore_IsLeftUntouched()
        {
            // Not parsed as a JSON array: the unreadable marker (DeserializeIdSet -> null).
            var config = ConfigWith(reviewedVod: "{not-json", excludedVod: new[] { 7, 3 });
            var store = DecisionStore.GetOrCreate(null, new NullLogger());

            store.AddReviewed(config, false, new[] { 5 });

            // The unreadable field must survive verbatim — rebuilding it would silently
            // discard every decision recorded so far.
            Assert.Equal("{not-json", config.ReviewedVodStreamIdsJson);
        }

        [Fact]
        public void ConfigBacked_Read_ReturnsUnreadableMarkerAsNull()
        {
            var config = ConfigWith(reviewedVod: "{not-json");
            var store = DecisionStore.GetOrCreate(null, new NullLogger());

            Assert.Null(store.Read(config).ReviewedVodStreamIds);
        }

        // -----------------------------------------------------------------
        // File-backed mode: migration, authority, mirrors
        // -----------------------------------------------------------------

        [Fact]
        public void FileBacked_FirstUse_MigratesFromMirrorsAndCreatesFile()
        {
            using (var dir = new TempDirectory())
            {
                var config = ConfigWith(
                    excludedVod: new[] { 5, 1 },
                    reviewedVod: "[11]",
                    tmdb: "{\"101\":51529}");
                var store = DecisionStore.GetOrCreate(dir.Path, new NullLogger());

                var state = store.Read(config);

                Assert.Equal(new HashSet<int> { 1, 5 }, state.ExcludedVodStreamIds);
                Assert.Equal(new HashSet<int> { 11 }, state.ReviewedVodStreamIds);
                Assert.Equal(51529, state.VodDecisionTmdbIds[101]);
                Assert.True(File.Exists(Path.Combine(dir.Path, "decisions.json")));
            }
        }

        [Fact]
        public void FileBacked_Read_SurvivesConfigMirrorWipe()
        {
            // The scenario the store exists for: a configuration save (or corruption) wipes the
            // mirror fields, and the decisions must not go with them.
            using (var dir = new TempDirectory())
            {
                var config = ConfigWith(reviewedVod: "[11,12]");
                var store = DecisionStore.GetOrCreate(dir.Path, new NullLogger());
                store.Read(config);

                var wiped = new PluginConfiguration();
                var state = store.Read(wiped);

                Assert.Equal(new HashSet<int> { 11, 12 }, state.ReviewedVodStreamIds);
            }
        }

        [Fact]
        public void FileBacked_AddReviewed_UpdatesFileAndMirrors()
        {
            using (var dir = new TempDirectory())
            {
                var config = ConfigWith(reviewedVod: "[11]");
                var store = DecisionStore.GetOrCreate(dir.Path, new NullLogger());
                store.Read(config);

                store.AddReviewed(config, false, new[] { 12 });

                Assert.Equal(new HashSet<int> { 11, 12 }, StrmSyncService.DeserializeIdSet(config.ReviewedVodStreamIdsJson));

                // A fresh store instance (as a restart would build) reads the file, not mirrors.
                var reloaded = DecisionStore.CreateUnsharedForTests(dir.Path, new NullLogger());
                Assert.Equal(new HashSet<int> { 11, 12 }, reloaded.Read(config).ReviewedVodStreamIds);
            }
        }

        [Fact]
        public void FileBacked_AddReviewed_MergesWithConcurrentDecision()
        {
            // The lost-update bug: a sync that loaded its checkpoint before another writer
            // recorded a decision must not clobber it when it folds its own additions.
            using (var dir = new TempDirectory())
            {
                var config = ConfigWith(reviewedVod: "[11]");
                var store = DecisionStore.GetOrCreate(dir.Path, new NullLogger());
                store.Read(config);

                // Another writer (dashboard save, webhook) records 99 between the sync's
                // read and its fold.
                store.AddReviewed(config, false, new[] { 99 });

                // The sync folds 12 with a checkpoint it read before that.
                store.AddReviewed(config, false, new[] { 12 });

                Assert.Equal(
                    new HashSet<int> { 11, 99, 12 },
                    store.Read(config).ReviewedVodStreamIds);
            }
        }

        [Fact]
        public void FileBacked_Replace_TakesIncomingStoresAndKeepsCopy()
        {
            using (var dir = new TempDirectory())
            {
                var config = ConfigWith(excludedVod: new[] { 1 }, reviewedVod: "[11]");
                var store = DecisionStore.GetOrCreate(dir.Path, new NullLogger());
                store.Read(config);

                var incoming = ConfigWith(
                    excludedVod: new[] { 2, 3 },
                    reviewedVod: "[21,22]",
                    reviewedSeries: "[31]",
                    tmdb: "{\"55\":777}");
                store.Replace(config, incoming);

                var state = store.Read(config);
                Assert.Equal(new HashSet<int> { 2, 3 }, state.ExcludedVodStreamIds);
                Assert.Equal(new HashSet<int> { 21, 22 }, state.ReviewedVodStreamIds);
                Assert.Equal(new HashSet<int> { 31 }, state.ReviewedSeriesIds);
                Assert.Equal(777, state.VodDecisionTmdbIds[55]);

                // A pre-change copy was kept, and the mirrors were refreshed.
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(dir.Path, "decision-copies"), "decisions-*.json"));
                Assert.Equal(new HashSet<int> { 2, 3 }, new HashSet<int>(config.ExcludedVodStreamIds));
            }
        }

        [Fact]
        public void FileBacked_Replace_UnreadableIncomingReviewed_KeepsCurrentValue()
        {
            using (var dir = new TempDirectory())
            {
                var logger = new RecordingLogger();
                var config = ConfigWith(reviewedVod: "[11,12]");
                var store = DecisionStore.GetOrCreate(dir.Path, logger);
                store.Read(config);

                var incoming = ConfigWith(excludedVod: new[] { 9 }, reviewedVod: "{not-json");
                store.Replace(config, incoming);

                var state = store.Read(config);
                // Exclusions replaced; the unreadable reviewed store is not trusted.
                Assert.Equal(new HashSet<int> { 9 }, state.ExcludedVodStreamIds);
                Assert.Equal(new HashSet<int> { 11, 12 }, state.ReviewedVodStreamIds);
                Assert.NotEmpty(logger.Warnings);
            }
        }

        [Fact]
        public void FileBacked_Replace_UnreviewThroughSave_RemovesTombstone()
        {
            // Re-reviewing in the dashboard arrives as a whole-config save whose only
            // decision change is the cleared tombstone. It must land in the store —
            // otherwise the next store mutation republishes the pre-save tombstone and
            // the un-review resurrection ADR-F008 fixed comes back through the store.
            using (var dir = new TempDirectory())
            {
                var config = ConfigWith(reviewedVod: "[5]");
                config.UnreviewedVodStreamIdsJson = "[5]";
                var store = DecisionStore.GetOrCreate(dir.Path, new NullLogger());
                store.Read(config);

                var incoming = ConfigWith(reviewedVod: "[5]");
                store.Replace(config, incoming);

                var state = store.Read(config);
                Assert.NotNull(state.UnreviewedVodStreamIds);
                Assert.Empty(state.UnreviewedVodStreamIds);
                Assert.Equal(string.Empty, config.UnreviewedVodStreamIdsJson);

                // And a restarted process sees the removal, not the pre-save value.
                var reloaded = DecisionStore.CreateUnsharedForTests(dir.Path, new NullLogger());
                var fresh = reloaded.Read(config);
                Assert.NotNull(fresh.UnreviewedVodStreamIds);
                Assert.Empty(fresh.UnreviewedVodStreamIds);
            }
        }

        [Fact]
        public void FileBacked_Version1File_TombstonesSeedFromMirrorsAndUpgradeOnWrite()
        {
            using (var dir = new TempDirectory())
            {
                var storePath = Path.Combine(dir.Path, "decisions.json");
                // A version 1 file, as a pre-tombstone build wrote it: no Unreviewed* fields.
                var v1 = "{\"Version\":1,\"ExcludedVodStreamIds\":[9],\"ExcludedSeriesIds\":[],\"ReviewedVodStreamIds\":[5],\"ReviewedSeriesIds\":null,\"VodDecisionTmdbIds\":null}";
                File.WriteAllText(storePath, v1);

                // The mirrors hold the tombstones a v1-era build recorded through the
                // configuration; absent fields must NOT read as the unreadable marker,
                // which would stand the review gate's exemption down.
                var config = ConfigWith(excludedVod: new[] { 9 }, reviewedVod: "[5]");
                config.UnreviewedVodStreamIdsJson = "[77]";
                config.UnreviewedSeriesIdsJson = "[88]";

                var store = DecisionStore.CreateUnsharedForTests(dir.Path, new NullLogger());
                var state = store.Read(config);

                Assert.Equal(new HashSet<int> { 77 }, state.UnreviewedVodStreamIds);
                Assert.Equal(new HashSet<int> { 88 }, state.UnreviewedSeriesIds);

                // Any mutation upgrades the file to version 2, carrying the tombstones.
                store.AddReviewed(config, false, new[] { 6 });
                var text = File.ReadAllText(storePath);
                Assert.Contains("\"Version\":2", text);
                Assert.Contains("\"UnreviewedVodStreamIds\":[77]", text);
                Assert.Contains("\"UnreviewedSeriesIds\":[88]", text);

                // A restarted process reads them from the file, not the mirrors.
                var reloaded = DecisionStore.CreateUnsharedForTests(dir.Path, new NullLogger());
                var fresh = reloaded.Read(new PluginConfiguration());
                Assert.Equal(new HashSet<int> { 77 }, fresh.UnreviewedVodStreamIds);
                Assert.Equal(new HashSet<int> { 88 }, fresh.UnreviewedSeriesIds);
            }
        }

        [Fact]
        public void FileBacked_CorruptStoreFile_FallsBackToMirrorsAndQuarantines()
        {
            using (var dir = new TempDirectory())
            {
                var logger = new RecordingLogger();
                File.WriteAllText(Path.Combine(dir.Path, "decisions.json"), "{ torn");
                var config = ConfigWith(excludedVod: new[] { 4 }, reviewedVod: "[41]");

                var store = DecisionStore.GetOrCreate(dir.Path, logger);
                var state = store.Read(config);

                // The mirrors are the recovery source, and the damage is kept for inspection.
                Assert.Equal(new HashSet<int> { 41 }, state.ReviewedVodStreamIds);
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(dir.Path, "decision-copies"), "decisions-corrupt-*.json"));
                Assert.Empty(Directory.GetFiles(dir.Path, "decisions.json"));

                // The next write rebuilds the file from the recovered state.
                store.AddReviewed(config, false, new[] { 42 });
                Assert.True(File.Exists(Path.Combine(dir.Path, "decisions.json")));
            }
        }

        [Fact]
        public void FileBacked_UnknownVersion_IsReadOnlyAndMirrorsWin()
        {
            // A store written by a newer build must not be destroyed by an older one.
            using (var dir = new TempDirectory())
            {
                var logger = new RecordingLogger();
                var storePath = Path.Combine(dir.Path, "decisions.json");
                var newer = "{\"Version\":99,\"ExcludedVodStreamIds\":[1],\"ExcludedSeriesIds\":[],\"ReviewedVodStreamIds\":null,\"ReviewedSeriesIds\":null,\"VodDecisionTmdbIds\":null}";
                File.WriteAllText(storePath, newer);

                var config = ConfigWith(reviewedVod: "[51]");
                var store = DecisionStore.GetOrCreate(dir.Path, logger);
                var state = store.Read(config);

                // Falls back to the mirrors...
                Assert.Equal(new HashSet<int> { 51 }, state.ReviewedVodStreamIds);

                // ...and leaves the newer file untouched, including on subsequent writes.
                store.AddReviewed(config, false, new[] { 52 });
                Assert.Equal(newer, File.ReadAllText(storePath));
                Assert.NotEmpty(logger.Errors);
            }
        }

        [Fact]
        public async Task FileBacked_ConcurrentAddReviewed_RecordsEveryId()
        {
            using (var dir = new TempDirectory())
            {
                var config = ConfigWith();
                var store = DecisionStore.GetOrCreate(dir.Path, new NullLogger());
                store.Read(config);

                var ids = Enumerable.Range(1, 400).ToList();
                var chunks = ids.Chunk(50);
                await Task.WhenAll(chunks.Select(chunk => Task.Run(() =>
                    store.AddReviewed(config, false, chunk))));

                var state = store.Read(config);
                Assert.Equal(400, state.ReviewedVodStreamIds.Count);
                Assert.All(ids, id => Assert.Contains(id, state.ReviewedVodStreamIds));
            }
        }

        // -----------------------------------------------------------------
        // The routing from a configuration save (Plugin.UpdateConfiguration path)
        // -----------------------------------------------------------------

        [Fact]
        public void RouteDecisionStoreWrites_EqualStores_DoNotCreateStoreFile()
        {
            using (var dir = new TempDirectory())
            {
                var svc = new StrmSyncService(new NullLogger());
                var current = ConfigWith(excludedVod: new[] { 1, 2 }, reviewedVod: "[5]");
                current.RecordsPath = dir.Path;
                // Same sets, different serialization: must compare equal after parsing.
                var incoming = ConfigWith(excludedVod: new[] { 2, 1 }, reviewedVod: "[ 5 ]");
                incoming.RecordsPath = dir.Path;

                svc.RouteDecisionStoreWrites(current, incoming);

                // The fast path: a settings-only save never brings the store into existence.
                Assert.False(File.Exists(Path.Combine(dir.Path, "decisions.json")));
            }
        }

        [Fact]
        public void RouteDecisionStoreWrites_ChangedStores_ReplaceThroughStore()
        {
            using (var dir = new TempDirectory())
            {
                var svc = new StrmSyncService(new NullLogger());
                var current = ConfigWith(excludedVod: new[] { 1 }, reviewedVod: "[5]");
                current.RecordsPath = dir.Path;
                svc.GetDecisionStore(current).Read(current);

                var incoming = ConfigWith(excludedVod: new[] { 8 }, reviewedVod: "[5,6]");
                svc.RouteDecisionStoreWrites(current, incoming);

                var state = svc.GetDecisionStore(current).Read(current);
                Assert.Equal(new HashSet<int> { 8 }, state.ExcludedVodStreamIds);
                Assert.Equal(new HashSet<int> { 5, 6 }, state.ReviewedVodStreamIds);
            }
        }

        [Fact]
        public void DecisionStoresEqual_DetectsRealChangesOnly()
        {
            var a = ConfigWith(excludedVod: new[] { 1, 2 }, reviewedVod: "[5]", tmdb: "{\"7\":9}");
            var sameReordered = ConfigWith(excludedVod: new[] { 2, 1 }, reviewedVod: "[5]", tmdb: "{\"7\":9}");
            var changed = ConfigWith(excludedVod: new[] { 1, 2 }, reviewedVod: "[5,6]", tmdb: "{\"7\":9}");

            Assert.True(DecisionStore.DecisionStoresEqual(a, sameReordered));
            Assert.False(DecisionStore.DecisionStoresEqual(a, changed));
        }

        [Fact]
        public void DecisionStoresEqual_TombstoneOnlyChange_IsARealChange()
        {
            // A save that only re-reviews one title changes nothing but a tombstone. If that
            // is not detected, the save is treated as settings-only, never routed through
            // Replace, and the store's next mutation republishes the pre-save tombstone.
            var a = ConfigWith(reviewedVod: "[5]");
            a.UnreviewedVodStreamIdsJson = "[5]";
            var reReviewed = ConfigWith(reviewedVod: "[5]");

            Assert.False(DecisionStore.DecisionStoresEqual(a, reReviewed));
        }
    }
}
