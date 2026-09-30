using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// The parts of the sync that delete files from the library, plus the helpers that decide
    /// what counts as in use. Kept in their own file so the mutation tests (stryker-config.json)
    /// can target exactly this code: Stryker could not compile its changes to the whole of
    /// StrmSyncService.cs, and a line range into that file went stale whenever code was added
    /// above it (issue #75).
    ///
    /// Also holds the sync's own bookkeeping deletes — dated catalogue snapshots, configuration
    /// rollback copies, deleted-path records, and the one-time episode-filename migration —
    /// because the delete-site guard requires EVERY delete call of this class to live here
    /// where the mutation tests reach it.
    ///
    /// Mutants that survive on purpose, so nobody chases them:
    /// - conditions that only decide whether to log (the logger calls themselves are skipped
    ///   through ignore-methods in stryker-config.json);
    /// - the "*.strm" search patterns: ownership is decided by file content, so the glob only
    ///   saves reading other files (the same survivor is noted in StrmOwnership);
    /// - the empty-folder loop in CleanupOrphans: Directory.Delete is not recursive and refuses
    ///   a folder that still has files, and the library folder itself cannot become empty,
    ///   because cleanup refuses to run when the sync wrote nothing (ADR-013).
    /// </summary>
    public partial class StrmSyncService
    {
        /// <summary>
        /// Finds the STRM files already on disk for <paramref name="seriesName"/>, if any.
        /// </summary>
        /// <remarks>
        /// Used only on the empty-detail path, so the per-series readdir stays off the hot loop.
        /// Folder names carry an optional metadata-ID suffix, so the comparison strips it the same
        /// way the pre-fetch smart-skip index does.
        /// </remarks>
        private string[] FindExistingSeriesStrms(PluginConfiguration config, string subFolder, string seriesName)
        {
            try
            {
                var parent = Path.Combine(config.StrmLibraryPath, subFolder);
                if (!Directory.Exists(parent))
                {
                    return Array.Empty<string>();
                }

                foreach (var dir in Directory.GetDirectories(parent))
                {
                    var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                    if (string.Equals(stripped, seriesName, StringComparison.OrdinalIgnoreCase))
                    {
                        return Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not probe existing STRM files for series '{0}': {1}", seriesName, ex.Message);
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// Deletes the on-disk folders of items the user has explicitly excluded.
        /// </summary>
        /// <remarks>
        /// Runs independently of <see cref="PluginConfiguration.CleanupOrphans"/> and ignores
        /// <see cref="PluginConfiguration.OrphanSafetyThreshold"/>. That threshold exists to survive
        /// a provider returning a truncated catalogue; an exclusion is a deliberate user action, so
        /// suppressing the delete would just look like the filter doing nothing.
        ///
        /// Folder matching strips any metadata-ID suffix, so an excluded title is found whether it
        /// was written as "Some Movie", "Some Movie [tmdbid=123]" or "Some Show [tvdbid=456]".
        ///
        /// A folder this run wrote into is never touched. Matching is by cleaned name, ignoring
        /// case, while the exclusion itself is by stream ID, so a kept entry whose name differs
        /// only in case (or cleans to the same name) shares the excluded entry's folder. Without
        /// this guard the kept title was deleted on every sync. Callers skip this pass when a
        /// category failed to load, because titles from it never reached writtenPaths and so
        /// are not protected. See ADR-018.
        /// </remarks>
        /// <param name="config">Active plugin configuration (supplies the library root).</param>
        /// <param name="excludedItems">Cleaned display name + category ID for each excluded item.</param>
        /// <param name="folderMode">"single", "multiple" or "custom".</param>
        /// <param name="categoryNames">Category ID → name, used by "multiple" mode.</param>
        /// <param name="folderMappings">Category ID → folder, used by "custom" mode.</param>
        /// <param name="rootFolder">"Movies" or "Shows".</param>
        /// <param name="writtenPaths">Every STRM this run wrote or kept.</param>
        /// <returns>The number of folders deleted.</returns>
        private int RemoveExcludedContent(
            PluginConfiguration config,
            List<Tuple<string, int?>> excludedItems,
            string folderMode,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            string rootFolder,
            HashSet<string> writtenPaths)
        {
            if (excludedItems == null || excludedItems.Count == 0)
            {
                return 0;
            }

            var removed = 0;
            var writtenDirs = BuildWrittenDirectories(writtenPaths, config.StrmLibraryPath);

            // subFolder → { folderNameWithoutIdSuffix → fullPath }. One readdir per subfolder.
            var dirIndexCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in excludedItems)
            {
                var sanitized = SanitizeFileName(item.Item1);
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    continue;
                }

                var subFolder = BuildContentFolderPath(
                    folderMode, item.Item2, categoryNames, folderMappings, rootFolder);
                if (subFolder == null)
                {
                    continue;
                }

                Dictionary<string, string> dirIndex;
                if (!dirIndexCache.TryGetValue(subFolder, out dirIndex))
                {
                    dirIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var fullPath = Path.Combine(config.StrmLibraryPath, subFolder);
                    if (Directory.Exists(fullPath))
                    {
                        foreach (var dir in Directory.GetDirectories(fullPath))
                        {
                            var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                            if (!string.IsNullOrEmpty(stripped) && !dirIndex.ContainsKey(stripped))
                            {
                                dirIndex[stripped] = dir;
                            }
                        }
                    }

                    dirIndexCache[subFolder] = dirIndex;
                }

                string existingDir;
                if (!dirIndex.TryGetValue(sanitized, out existingDir))
                {
                    continue;
                }

                if (writtenDirs.Contains(NormalizeDirectory(existingDir)))
                {
                    _logger.Info(
                        "Keeping '{0}' for excluded item '{1}': this sync wrote an included title into the same folder",
                        existingDir, sanitized);
                    continue;
                }

                try
                {
                    // Delete only the files this plugin actually wrote, verified by content, then
                    // prune whatever that emptied. Matching is by title alone, so a match is not
                    // proof of ownership: without this a user's own "Ben-Hur" folder would be
                    // destroyed by excluding the provider's "Ben-Hur", and a hand-written .nfo or a
                    // trailer.strm sitting beside our output would go with it. See ADR-014.
                    var deletedFiles = StrmOwnership.DeleteOwnedFiles(
                        existingDir, config.BaseUrl, config.DispatcharrUrl, out var folderGone);

                    if (deletedFiles == 0)
                    {
                        _logger.Debug(
                            "Skipping '{0}' for excluded item '{1}': nothing in it was written by this plugin",
                            existingDir, sanitized);
                        continue;
                    }

                    dirIndex.Remove(sanitized);
                    removed++;
                    _logger.Info(
                        folderGone
                            ? "Removed excluded item folder: {0}"
                            : "Removed plugin files for excluded item, folder kept (still has other content): {0}",
                        existingDir);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to remove excluded item folder '{0}': {1}", existingDir, ex.Message);
                }
            }

            if (removed > 0)
            {
                _logger.Info("Removed {0} folder(s) for explicitly excluded items under {1}", removed, rootFolder);
            }


            return removed;
        }


        /// <summary>
        /// Every directory that holds a path in <paramref name="writtenPaths"/>, and its ancestors
        /// up to (not including) the library root. Episodes sit in a season folder below the
        /// show folder an exclusion matches, so the ancestors count as written too.
        /// </summary>
        private static HashSet<string> BuildWrittenDirectories(HashSet<string> writtenPaths, string libraryRoot)
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (writtenPaths == null)
            {
                return dirs;
            }

            var root = NormalizeDirectory(libraryRoot);
            string[] snapshot;
            lock (writtenPaths) { snapshot = writtenPaths.ToArray(); }

            foreach (var path in snapshot)
            {
                var dir = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(dir))
                {
                    var normalized = NormalizeDirectory(dir);
                    if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase) || !dirs.Add(normalized))
                    {
                        break;
                    }

                    dir = Path.GetDirectoryName(dir);
                }
            }

            return dirs;
        }

        /// <summary>
        /// Adds the STRM files already in <paramref name="dir"/> to <paramref name="writtenPaths"/>.
        /// Used when writing an item failed, so its existing files still count as in use.
        /// </summary>
        private void RecordExistingStrms(string dir, HashSet<string> writtenPaths)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            try
            {
                if (Directory.Exists(dir))
                {
                    RecordStrms(Directory.GetFiles(dir, "*.strm"), writtenPaths);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not list existing STRM files in '{0}': {1}", dir, ex.Message);
            }
        }

        private static void RecordStrms(IEnumerable<string> paths, HashSet<string> writtenPaths)
        {
            lock (writtenPaths)
            {
                foreach (var path in paths)
                {
                    writtenPaths.Add(path);
                }
            }
        }

        private static string NormalizeDirectory(string path)
            => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private int CleanupOrphans(
            string rootPath, HashSet<string> validPaths, double safetyThreshold, PluginConfiguration config)
        {
            if (!Directory.Exists(rootPath))
            {
                return 0;
            }

            var existingStrms = Directory.GetFiles(rootPath, "*.strm", SearchOption.AllDirectories);

            // Nothing was written or preserved this run, but files exist on disk. That is a
            // provider returning an empty catalogue, not the user deleting their whole library.
            // The ratio guard below cannot catch this at small N (it only applies above 10 files),
            // so refuse outright rather than emptying the library. See ADR-013.
            if (validPaths.Count == 0 && existingStrms.Length > 0)
            {
                _logger.Warn(
                    "Orphan cleanup skipped: the catalogue produced no files this run but {0} STRM file(s) exist under {1} — refusing to treat an empty catalogue as a deletion",
                    existingStrms.Length, rootPath);
                return 0;
            }

            // Being a .strm under our library root is not proof we wrote it. Verify ownership
            // before considering anything for deletion — a user's own STRM that the provider
            // never listed would otherwise look exactly like an orphan. Only orphan candidates
            // are read, so the cost stays proportional to deletions, not to library size.
            var orphans = existingStrms
                .Where(s => !validPaths.Contains(s))
                .Where(s => StrmOwnership.IsOwnedStrm(s, config.BaseUrl, config.DispatcharrUrl))
                .ToList();

            var foreignCount = existingStrms.Length - validPaths.Count - orphans.Count;
            if (foreignCount > 0)
            {
                _logger.Info(
                    "Orphan cleanup: leaving {0} STRM file(s) under {1} that this plugin did not write",
                    foreignCount, rootPath);
            }

            // Ratio is taken over the files we could actually have written (what we wrote or kept,
            // plus the orphans we own). Counting foreign files in the denominator would dilute the
            // ratio and make the guard fire less often than intended.
            var ownedTotal = validPaths.Count + orphans.Count;
            if (safetyThreshold > 0 && ownedTotal > 10 && orphans.Count > 0)
            {
                double ratio = (double)orphans.Count / ownedTotal;
                if (ratio > safetyThreshold)
                {
                    _logger.Warn(
                        "Orphan cleanup skipped: {0}/{1} ({2:P0}) exceeds safety threshold {3:P0} — possible provider issue",
                        orphans.Count, ownedTotal, ratio, safetyThreshold);
                    return 0;
                }
            }

            var removed = 0;

            // A successful deletion was previously logged nowhere, at any level — only the count
            // was. So "the sync removed 360 episodes" was unanswerable after the fact: the files
            // were gone and nothing recorded which ones. Worse, the class of damage this hides is
            // exactly the dangerous one — a title whose provider id churned leaves a .strm that is
            // not in writtenPaths, so it is deleted as an orphan even though the user still wants
            // it (see ADR-F004). Keep a sample so the question is answerable next time.
            // From andyj682/emby-xtream-dedupe (ADR-F005).
            const int DeletedSampleSize = 15;
            // Every deletion, not just the sample. Bounded by the orphan count, which the ratio
            // guard above already caps, so this cannot grow to the size of the library.
            var deleted = new List<string>();

            foreach (var strmFile in orphans)
            {
                try
                {
                    // delete-ok: `orphans` is already filtered through StrmOwnership.IsOwnedStrm
                    // above, so every path here is one this plugin wrote.
                    File.Delete(strmFile);
                    removed++;
                    deleted.Add(RelativeToRoot(strmFile, rootPath));

                    // Remove empty parent directories
                    var dir = Path.GetDirectoryName(strmFile);
                    while (!string.IsNullOrEmpty(dir) &&
                           !string.Equals(dir, rootPath, StringComparison.OrdinalIgnoreCase) &&
                           Directory.Exists(dir) &&
                           Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        // delete-ok: prunes a directory the loop condition just proved empty.
                        Directory.Delete(dir);
                        dir = Path.GetDirectoryName(dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to cleanup orphan '{0}': {1}", strmFile, ex.Message);
                }
            }

            if (removed > 0)
            {
                // Sorted so the sample is stable rather than directory-order.
                deleted.Sort(StringComparer.OrdinalIgnoreCase);
                var sample = deleted.Count > DeletedSampleSize
                    ? deleted.GetRange(0, DeletedSampleSize)
                    : deleted;

                // Past the sample the log alone stops being able to answer "what went?" — and
                // that is exactly the size of event where the question gets asked. The record is
                // written whatever the log level, because the question is always asked in
                // hindsight and a diagnostic you had to enable beforehand cannot answer it.
                var recordPath = deleted.Count > sample.Count
                    ? WriteDeletionRecord(rootPath, deleted)
                    : null;

                _logger.Info(
                    "Removed {0} orphaned STRM files from {1}: {2}{3}",
                    removed, rootPath,
                    string.Join(", ", sample),
                    deleted.Count > sample.Count
                        ? ", ... (full list: " + (recordPath ?? "could not be written") + ")"
                        : string.Empty);
            }

            return removed;
        }

        /// <summary>
        /// Writes the complete list of paths a cleanup removed, and returns where it went
        /// (null if it could not be written). Called only when the deletion count exceeds the
        /// sample the log line carries.
        /// <para>
        /// Never throws: the files are already gone by the time this runs, so failing to record
        /// what happened must not also fail the sync.
        /// </para>
        /// <para>
        /// From andyj682/emby-xtream-dedupe (ADR-F005), moved here with the delete code it
        /// reports on.
        /// </para>
        /// </summary>
        private string WriteDeletionRecord(string rootPath, List<string> deletedPaths)
        {
            var directory = DeletionRecordDirectory;
            if (string.IsNullOrEmpty(directory))
            {
                try
                {
                    // Plugin.Instance throws when ApplicationPaths is not initialised yet, which
                    // is why this is guarded rather than read at construction.
                    directory = Plugin.Instance?.ApplicationPaths?.LogDirectoryPath;
                }
                catch (Exception ex)
                {
                    _logger.Debug("No log directory available for the deleted-path record: {0}", ex.Message);
                    return null;
                }
            }

            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(directory);

                // Timestamp FIRST so an ordinary name sort is a chronological sort — the prune
                // below depends on that, and putting the root name first would group Movies and
                // Shows into separate runs and prune the wrong files.
                var leaf = Path.GetFileName(rootPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    "xtream-deleted-{0:yyyyMMdd-HHmmss}-{1}.txt",
                    DateTime.Now,
                    string.IsNullOrEmpty(leaf) ? "library" : SanitizeFileName(leaf));
                var path = Path.Combine(directory, fileName);

                using (var writer = new StreamWriter(path, false))
                {
                    writer.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "# {0} file(s) removed from {1} at {2:yyyy-MM-dd HH:mm:ss}",
                        deletedPaths.Count, rootPath, DateTime.Now));
                    writer.WriteLine("# Paths are relative to the root above.");
                    foreach (var deletedPath in deletedPaths)
                    {
                        writer.WriteLine(deletedPath);
                    }
                }

                PruneDeletionRecords(directory);
                return path;
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not write the deleted-path record: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Keeps the deleted-path records bounded. Name-sorted, which is chronological because
        /// the timestamp leads the filename.
        /// </summary>
        private void PruneDeletionRecords(string directory)
        {
            const int KeepRecords = 10;

            var existing = Directory.GetFiles(directory, "xtream-deleted-*.txt");
            if (existing.Length <= KeepRecords)
            {
                return;
            }

            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < existing.Length - KeepRecords; i++)
            {
                try
                {
                    // delete-ok: removes this plugin's own diagnostic records from Emby's log
                    // directory, matched on the "xtream-deleted-*.txt" name pattern it writes
                    // itself. These are text files about the library, never library content, so
                    // the StrmOwnership check does not apply and nothing here can reach a .strm.
                    File.Delete(existing[i]);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Could not prune old deleted-path record '{0}': {1}",
                        existing[i], ex.Message);
                }
            }
        }

        /// <summary>
        /// Trims the library root off a path so the deleted-file sample reads as
        /// <c>Show Name [tmdbid=1]/Season 01/....strm</c> rather than repeating the root on
        /// every entry. Falls back to the full path if it does not sit under the root.
        /// </summary>
        private static string RelativeToRoot(string fullPath, string rootPath)
        {
            if (!string.IsNullOrEmpty(rootPath)
                && fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath
                    .Substring(rootPath.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return fullPath;
        }
        /// <summary>
        /// Keeps the most recent <paramref name="keep"/> dated snapshots.
        /// </summary>
        private void PruneCatalogueSnapshots(string directory, int keep)
        {
            var files = Directory.GetFiles(directory, "catalogue-ids-*.tsv");
            if (files.Length <= keep)
            {
                return;
            }

            // ISO dates sort chronologically as text, so ordinal order is oldest-first.
            Array.Sort(files, StringComparer.Ordinal);

            for (var i = 0; i < files.Length - keep; i++)
            {
                try
                {
                    // delete-ok: prunes this plugin's own dated catalogue listings from the
                    // "snapshots" folder it created. These are TSV files the plugin wrote
                    // itself, never library content. The glob matches only its own
                    // "catalogue-ids-*.tsv" naming, so a copy the user has deliberately
                    // preserved by renaming it with a prefix is immune — the same escape the
                    // external script's prune leaves open, and the one that saved a recovery.
                    File.Delete(files[i]);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Could not prune old snapshot '{0}': {1}", files[i], ex.Message);
                }
            }
        }


        /// <summary>
        /// One-time rename of episode STRM files from the old title-bearing form
        /// ("Show - S01E02 - Some Title.strm") to the title-free form
        /// ("Show - S01E02.strm").
        ///
        /// Renaming in place matters. Letting the sync converge on the new names instead
        /// would write every episode afresh and orphan every old one — on a large library
        /// that is an orphan ratio around 50%, far above
        /// <see cref="PluginConfiguration.OrphanSafetyThreshold"/>, so cleanup would refuse
        /// and the tree would carry two copies of everything until someone raised the
        /// threshold by hand.
        ///
        /// Deliberately does NOT touch the delta watermark or the stored episode hashes
        /// (unlike <see cref="CheckAndUpgradeNamingVersion"/>): the files land exactly where
        /// the next sync expects them, so nothing needs re-fetching.
        /// </summary>
        /// <returns>Number of files renamed or removed.</returns>
        internal int MigrateEpisodeFilenames(PluginConfiguration config, Action saveConfig)
        {
            if (config.EpisodeFilenameMigrationVersion >= CurrentEpisodeFilenameVersion)
                return 0;

            var showsRoot = Path.Combine(config.StrmLibraryPath ?? string.Empty, "Shows");
            var renamed = 0;
            var collapsed = 0;

            if (Directory.Exists(showsRoot))
            {
                // Runs once, but walks the whole tree — surface it rather than leaving an
                // unexplained pause at the start of the sync.
                _seriesProgress.Phase = "Migrating episode filenames";

                string[] files;
                try
                {
                    files = Directory.GetFiles(showsRoot, "*.strm", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    // Leave the version unset so the migration is retried next run rather
                    // than being silently skipped on a transient I/O error.
                    _logger.Warn("Episode filename migration: could not scan '{0}': {1}", showsRoot, ex.Message);
                    return 0;
                }

                foreach (var path in files)
                {
                    var match = TitledEpisodeFileRegex.Match(Path.GetFileNameWithoutExtension(path) ?? string.Empty);
                    if (!match.Success) continue;

                    // Only touch files this plugin wrote — the same guard orphan cleanup uses,
                    // so a hand-placed .strm that happens to match the pattern is left alone.
                    if (!StrmOwnership.IsOwnedStrm(path, config.BaseUrl, config.DispatcharrUrl)) continue;

                    var dir = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(dir)) continue;
                    var target = Path.Combine(dir, match.Groups["base"].Value + ".strm");

                    try
                    {
                        if (File.Exists(target))
                        {
                            // Both forms already present — the pair of files this change exists
                            // to prevent. The title-free one is canonical and the next sync
                            // corrects its URL if it differs, so drop the titled twin.
                            // delete-ok: the loop skips every path StrmOwnership.IsOwnedStrm
                            // rejects, so this only ever removes a STRM the plugin wrote, and
                            // only when the file it would have been renamed to already exists.
                            File.Delete(path);
                            collapsed++;
                        }
                        else
                        {
                            File.Move(path, target);
                            renamed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Episode filename migration: could not rename '{0}': {1}", path, ex.Message);
                    }
                }
            }

            config.EpisodeFilenameMigrationVersion = CurrentEpisodeFilenameVersion;
            saveConfig?.Invoke();

            if (renamed > 0 || collapsed > 0)
            {
                _logger.Info(
                    "Episode filename migration: renamed {0} file(s) to the title-free form, removed {1} duplicate(s)",
                    renamed, collapsed);
            }

            return renamed + collapsed;
        }


        /// <summary>
        /// Takes a rollback copy of the configuration file if it has changed since the last one
        /// (ADR-F005 mechanism 3). Returns the path written, or null if nothing was.
        /// <para>
        /// Called at the START of a sync, before the run performs any writes of its own — the
        /// naming-version upgrade and the review gate's write-back both save the configuration,
        /// so a copy taken later would already be of the post-write state.
        /// </para>
        /// <para>
        /// This is a rollback, not a backup. It sits on the same volume as the file it protects
        /// and does nothing for a lost disk; what it covers is a bad write through the plugin's
        /// own save path, where the last good state is otherwise gone. Never throws — failing to
        /// take a safety copy must not stop a sync the user asked for.
        /// </para>
        /// </summary>
        internal string SnapshotConfigurationForRollback(PluginConfiguration config)
        {
            var keep = config.ConfigRollbackCount;
            if (keep <= 0)
            {
                return null;
            }

            try
            {
                var source = ConfigRollbackSourcePath;
                if (string.IsNullOrEmpty(source))
                {
                    // Plugin.Instance throws before ApplicationPaths is initialised, so this is
                    // guarded rather than resolved at construction.
                    source = Plugin.InstanceOrNull?.ConfigPath;
                }

                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                {
                    return null;
                }

                var directory = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, RollbackFolderName);
                Directory.CreateDirectory(directory);

                // Skip when nothing changed, or a user who syncs hourly and edits nothing would
                // churn several megabytes a day and push the real last-good state out of the
                // retention window — which would defeat the whole mechanism.
                var currentHash = HashFile(source);
                var existing = Directory.GetFiles(directory, "*.xml");
                Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
                if (existing.Length > 0 && currentHash != null
                    && string.Equals(currentHash, HashFile(existing[existing.Length - 1]), StringComparison.Ordinal))
                {
                    return null;
                }

                // Never overwrite, and never rely on the clock for uniqueness. The movie sync
                // saves the configuration and the series sync runs straight afterwards, so two
                // copies arriving in the same instant is ordinary — and back-to-back syncs have
                // been observed completing inside a single millisecond, so even millisecond
                // stamps collide. A collision used to mean one copy silently replacing the
                // other, destroying exactly the state being preserved; now it just takes the
                // next free name.
                //
                // The collision counter is separated by '_' and zero-padded, both deliberately.
                // The prune sorts by name and relies on that being chronological: '_' (0x5F)
                // sorts AFTER '.' (0x2E), so "…123_001.xml" follows "…123.xml" rather than
                // preceding it, which a '-' separator would have got backwards. Padding keeps
                // 002 after 001 rather than after 010.
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
                var target = Path.Combine(directory, stamp + ".xml");
                for (var attempt = 1; File.Exists(target) && attempt < 1000; attempt++)
                {
                    target = Path.Combine(
                        directory,
                        string.Format(CultureInfo.InvariantCulture, "{0}_{1:D3}.xml", stamp, attempt));
                }

                File.Copy(source, target, false);

                PruneConfigurationCopies(directory, keep);
                _logger.Debug("Configuration rollback copy written to {0}", target);
                return target;
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not take a configuration rollback copy: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>SHA-256 of a file, or null if it cannot be read.</summary>
        private static string HashFile(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    return BitConverter.ToString(sha.ComputeHash(stream));
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Keeps the rollback copies bounded. Name-sorted, which is chronological because the
        /// filename is the timestamp.
        /// </summary>
        private void PruneConfigurationCopies(string directory, int keep)
        {
            var existing = Directory.GetFiles(directory, "*.xml");
            if (existing.Length <= keep)
            {
                return;
            }

            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < existing.Length - keep; i++)
            {
                try
                {
                    // delete-ok: removes this plugin's own rollback copies from the
                    // "xtream-rollback" folder it created beside its configuration file. These are copies of the
                    // plugin's XML settings, never library content, so the StrmOwnership check
                    // does not apply and nothing here can reach a .strm or a media folder.
                    File.Delete(existing[i]);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Could not prune old rollback copy '{0}': {1}", existing[i], ex.Message);
                }
            }
        }


    }
}
