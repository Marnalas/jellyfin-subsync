using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Subsync.Starter.Infrastructure;

/// <summary>
/// Tracks which subtitle files have already been synced (by content
/// hash), so repeat sweeps skip files that are already up to date -
/// and so a later sweep doesn't mistake the sidecar's own overwrite
/// (replacing the original .srt with the synced version) for a new,
/// unsynced file.
/// </summary>
/// <remarks>
/// Values are stored as "sha256:&lt;hex&gt;". A bare hex value is an entry
/// written by 3.0.0.0 or earlier, when the hash was MD5; those are verified
/// and rewritten in place the first time they're read, so upgrading doesn't
/// force a full library re-sync. Downgrading does: an older build won't
/// recognise the prefixed form.
/// </remarks>
public class SkipCache : ISkipCache
{
    private const string Sha256Prefix = "sha256:";

    /// <summary>
    /// Save() serialises and rewrites the whole dictionary, so doing it per
    /// file made a sweep quadratic in the size of the cache - on a library
    /// with 50k tracked subtitles, megabytes written per subtitle synced.
    /// Batching bounds what an abrupt shutdown loses to the last few
    /// entries, whose files simply get synced again: far cheaper than the
    /// write amplification.
    /// </summary>
    private const int SaveBatchSize = 25;

    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private Dictionary<string, string> _hashes = [];
    private int _pendingWrites;
    private DateTime _lastSaveUtc = DateTime.UtcNow;
    private bool _legacyHashUnavailableLogged;

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
                return;

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
    public bool IsCached(string subtitlePath)
    {
        string? known;
        lock (_lock)
        {
            if (!_hashes.TryGetValue(subtitlePath, out known))
                return false;
        }

        if (known.StartsWith(Sha256Prefix, StringComparison.Ordinal))
        {
            return string.Equals(
                known[Sha256Prefix.Length..],
                HashHex(subtitlePath, HashAlgorithmName.SHA256),
                StringComparison.OrdinalIgnoreCase);
        }

        // Unprefixed: a bare MD5 hex written by 3.0.0.0 or earlier.
        // Verifying it against the file is the only way to avoid forcing a
        // one-off full-library re-sync on upgrade, so MD5 is computed
        // exactly once more per file - in the same pass over the bytes as
        // the SHA-256 that replaces it.
        if (!TryHashBoth(subtitlePath, out var md5Hex, out var sha256Hex))
            return false;

        if (!string.Equals(known, md5Hex, StringComparison.OrdinalIgnoreCase))
            return false;

        lock (_lock)
        {
            _hashes[subtitlePath] = Sha256Prefix + sha256Hex;
            _pendingWrites++;
        }

        return true;
    }

    /// <summary>Records the current content of the subtitle file as "synced".</summary>
    public void AddToCache(string subtitlePath)
    {
        var value = Sha256Prefix + HashHex(subtitlePath, HashAlgorithmName.SHA256);

        bool save;
        lock (_lock)
        {
            _hashes[subtitlePath] = value;
            _pendingWrites++;
            save = _pendingWrites >= SaveBatchSize || DateTime.UtcNow - _lastSaveUtc >= SaveInterval;
        }

        if (save)
            Flush();
    }

    /// <summary>Persists whatever AddToCache has batched up.</summary>
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

    /// <summary>
    /// Drops entries for files that no longer exist. Two guards, because a
    /// wrong answer here silently re-syncs a whole library: a missing
    /// *directory* is read as "that mount isn't there right now" rather
    /// than "those files are gone", and a cleanup that would remove more
    /// than half the cache is refused outright, since that's a mount
    /// problem and not half a library being deleted in one day.
    /// </summary>
    /// <remarks>
    /// Belongs at the end of a sweep, not the start: by then the mounts
    /// have demonstrably been live, whereas at sweep start a volume that
    /// hasn't finished mounting looks exactly like a wholesale deletion.
    /// </remarks>
    public int RemoveMissingFiles()
    {
        lock (_lock)
        {
            List<string> missing =
            [
                .. from path in _hashes.Keys
                let directory = Path.GetDirectoryName(path)
                where !string.IsNullOrEmpty(directory) && Directory.Exists(directory)
                where !File.Exists(path)
                select path
            ];

            if (missing.Count == 0)
                return 0;

            if (missing.Count * 2 > _hashes.Count)
            {
                _logger.LogWarning(
                    "Subsync: skipping skip-cache cleanup - {Missing} of {Total} tracked subtitles look "
                    + "missing, which is far more likely a mount problem than deleted files",
                    missing.Count, _hashes.Count);
                return 0;
            }

            foreach (var path in missing)
                _hashes.Remove(path);

            _pendingWrites += missing.Count;
            return missing.Count;
        }
    }

    /// <summary>
    /// Persists immediately rather than through the batched AddToCache path -
    /// this is a rare, interactive admin action, not sweep hot-path, so there's
    /// no write-amplification concern to batch against.
    /// </summary>
    public int Clear()
    {
        lock (_lock)
        {
            var count = _hashes.Count;
            if (count == 0)
                return 0;

            _hashes.Clear();
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
            if (!_hashes.Remove(subtitlePath))
                return;

            _pendingWrites++;
        }
    }

    /// <summary>See <see cref="Clear"/> for why this persists immediately instead of batching.</summary>
    public int RemoveForPaths(IEnumerable<string> subtitlePaths)
    {
        lock (_lock)
        {
            var removed = subtitlePaths.Count(path => _hashes.Remove(path));

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

    private static string HashHex(string path, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
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

    /// <summary>
    /// One read of the file, two digests. Returns false when MD5 is
    /// unavailable - the case on a FIPS-enforcing host, where creating it
    /// throws. The caller then treats the legacy entry as a miss: the file
    /// is synced once more and stored under the (FIPS-approved) SHA-256
    /// form, so such a host degrades to one extra sync per file instead of
    /// the unhandled exception that used to abort the sweep outright.
    /// </summary>
    private bool TryHashBoth(string path, out string md5Hex, out string sha256Hex)
    {
        md5Hex = string.Empty;
        sha256Hex = string.Empty;

        IncrementalHash md5;
        try
        {
            md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        }
        catch (Exception ex)
        {
            if (_legacyHashUnavailableLogged) return false;
            _legacyHashUnavailableLogged = true;
            _logger.LogWarning(
                ex,
                "Subsync: MD5 is unavailable on this host, so skip-cache entries written before the "
                + "SHA-256 migration can't be verified. Those files are synced once more, then tracked "
                + "by SHA-256 like everything else");

            return false;
        }

        using (md5)
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var stream = File.OpenRead(path))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    md5.AppendData(buffer, 0, read);
                    sha.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            md5Hex = Convert.ToHexString(md5.GetHashAndReset());
            sha256Hex = Convert.ToHexString(sha.GetHashAndReset());
        }

        return true;
    }
}