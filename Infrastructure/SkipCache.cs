using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure
{
    /// <summary>
    /// Tracks which subtitle files have already been synced (by content
    /// hash), so both the instant watcher and the scheduled sweep skip
    /// files that are already up to date - and so the watcher doesn't
    /// re-trigger on the overwrite the sidecar itself causes when it
    /// replaces the original .srt with the synced version.
    /// </summary>
    public class SkipCache
    {
        private readonly string _path;
        private readonly ILogger _logger;
        private readonly object _lock = new();
        private Dictionary<string, string> _hashes = new();

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
                              ?? new Dictionary<string, string>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subsync: failed to load skip-cache, starting fresh");
                    _hashes = new Dictionary<string, string>();
                }
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_hashes);
                    File.WriteAllText(_path, json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subsync: failed to persist skip-cache");
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
