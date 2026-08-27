using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// Tracks consecutive sync failures per subtitle file, keyed to the file's
/// current content so a file the user has since replaced or fixed gets a
/// fresh attempt automatically instead of staying stuck skipped because of
/// bytes that no longer exist - same reasoning as why <see cref="SkipCache"/>
/// is hash-keyed rather than path-keyed.
/// </summary>
public class FailCache : IFailCache
{
    /// <summary>See <see cref="SkipCache"/>'s SaveBatchSize for why this is batched.</summary>
    private const int SaveBatchSize = 25;

    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private sealed record FailureRecord(string ContentHash, int ConsecutiveFailures);

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private Dictionary<string, FailureRecord> _failures = [];
    private int _pendingWrites;
    private DateTime _lastSaveUtc = DateTime.UtcNow;
    private readonly int _maxConsecutiveFailures;

    public FailCache(string dataFolderPath, int maxConsecutiveFailures, ILogger logger)
    {
        _maxConsecutiveFailures = maxConsecutiveFailures;
        _logger = logger;
        Directory.CreateDirectory(dataFolderPath);
        _path = Path.Combine(dataFolderPath, "sync-failures.json");
        Load();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
                return;

            try
            {
                var json = File.ReadAllText(_path);
                _failures = JsonSerializer.Deserialize<Dictionary<string, FailureRecord>>(json)
                            ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subsync: failed to load fail-cache, starting fresh");
                _failures = [];
            }
        }
    }

    /// <summary>See <see cref="SkipCache"/>'s Save() for why this is atomic temp-then-rename.</summary>
    private void Save()
    {
        lock (_lock)
        {
            var tempPath = _path + ".tmp";
            try
            {
                var json = JsonSerializer.Serialize(_failures);
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Subsync: failed to persist fail-cache");

                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogDebug(cleanupEx, "Subsync: failed to clean up fail-cache temp file");
                }
            }
        }
    }

    public bool IsCached(string subtitlePath)
    {
        if (_maxConsecutiveFailures <= 0)
            return false;

        FailureRecord? record;
        lock (_lock)
        {
            if (!_failures.TryGetValue(subtitlePath, out record))
                return false;
        }

        if (!string.Equals(record.ContentHash, HashHex(subtitlePath), StringComparison.OrdinalIgnoreCase))
            return false;

        return record.ConsecutiveFailures >= _maxConsecutiveFailures;
    }

    public void AddToCache(string subtitlePath)
    {
        var hash = HashHex(subtitlePath);

        bool save;
        lock (_lock)
        {
            var streak = _failures.TryGetValue(subtitlePath, out var existing)
                         && string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase)
                ? existing.ConsecutiveFailures + 1
                : 1;

            _failures[subtitlePath] = new FailureRecord(hash, streak);
            _pendingWrites++;
            save = _pendingWrites >= SaveBatchSize || DateTime.UtcNow - _lastSaveUtc >= SaveInterval;
        }

        if (save)
            Flush();
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_pendingWrites == 0)
                return;

            Save();
            _pendingWrites = 0;
            _lastSaveUtc = DateTime.UtcNow;
        }
    }

    public int RemoveMissingFiles()
    {
        lock (_lock)
        {
            List<string> missing =
            [
                .. from path in _failures.Keys
                let directory = Path.GetDirectoryName(path)
                where !string.IsNullOrEmpty(directory) && Directory.Exists(directory)
                where !File.Exists(path)
                select path
            ];

            if (missing.Count == 0)
                return 0;

            if (missing.Count * 2 > _failures.Count)
            {
                _logger.LogWarning(
                    "Subsync: skipping fail-cache cleanup - {Missing} of {Total} tracked subtitles look "
                    + "missing, which is far more likely a mount problem than deleted files",
                    missing.Count, _failures.Count);
                return 0;
            }

            foreach (var path in missing)
                _failures.Remove(path);

            _pendingWrites += missing.Count;
            return missing.Count;
        }
    }

    public int Clear()
    {
        lock (_lock)
        {
            var count = _failures.Count;
            if (count == 0)
                return 0;

            _failures.Clear();
            Save();
            _pendingWrites = 0;
            _lastSaveUtc = DateTime.UtcNow;
            return count;
        }
    }

    public void RemoveForPath(string subtitlePath)
    {
        lock (_lock)
        {
            if (!_failures.Remove(subtitlePath))
                return;

            _pendingWrites++;
        }
    }

    public int RemoveForPaths(IEnumerable<string> subtitlePaths)
    {
        lock (_lock)
        {
            var removed = subtitlePaths.Count(path => _failures.Remove(path));

            if (removed == 0)
                return 0;

            Save();
            _pendingWrites = 0;
            _lastSaveUtc = DateTime.UtcNow;
            return removed;
        }
    }

    public void Dispose()
    {
        Flush();
        GC.SuppressFinalize(this);
    }

    private static string HashHex(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = File.OpenRead(path);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}