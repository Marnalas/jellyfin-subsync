using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure
{
    /// <summary>
    /// Tracks which subtitle files have already been synced (by content
    /// hash), so repeat sweeps skip files that are already up to date -
    /// and so a later sweep doesn't mistake the sidecar's own overwrite
    /// (replacing the original .srt with the synced version) for a new,
    /// unsynced file.
    /// </summary>
    public class SkipCache
    {
        private readonly string _path;
        private readonly ILogger _logger;
        private readonly Lock _lock = new();
        private Dictionary<string, string> _hashes = [];

        public SkipCache(string dataFolderPath, ILogger logger)
        {
            _logger = logger;
            Directory.CreateDirectory(dataFolderPath);
            _path = Path.Combine(dataFolderPath, "skip-cache.json");
            Load();
        }

        private void Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                try
                {
                    var json = File.ReadAllText(_path);
                    _hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                              ?? [];
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subsync: failed to load skip-cache, starting fresh");
                    _hashes = [];
                }
            }
        }

        /// <summary>
        /// Writes to a sibling temp file and renames it over the live one.
        /// The rename is atomic within a directory, so a crash mid-save leaves
        /// either the previous cache or the new one intact - never a truncated
        /// file that fails to parse on the next start and forces a full
        /// library re-sync.
        /// </summary>
        private void Save()
        {
            lock (_lock)
            {
                var tempPath = _path + ".tmp";
                try
                {
                    var json = JsonSerializer.Serialize(_hashes);
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, _path, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subsync: failed to persist skip-cache");

                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogDebug(cleanupEx, "Subsync: failed to clean up skip-cache temp file");
                    }
                }
            }
        }

        /// <summary>Returns true if this exact file content has already been synced.</summary>
        public bool IsAlreadySynced(string subtitlePath)
        {
            var hash = HashFile(subtitlePath);
            lock (_lock)
            {
                return _hashes.TryGetValue(subtitlePath, out var known) && known == hash;
            }
        }

        /// <summary>Records the current content of the subtitle file as "synced".</summary>
        public void MarkSynced(string subtitlePath)
        {
            var hash = HashFile(subtitlePath);
            lock (_lock)
            {
                _hashes[subtitlePath] = hash;
            }

            Save();
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(stream);
            return Convert.ToHexString(bytes);
        }
    }
}
