using System.Collections.Concurrent;
using Fraylo.Browser.Abstractions;

namespace Fraylo.Browser;

/// <summary>
/// Thực thi cơ chế Single-Instance Profile Lock cho userDataDir của từng profile trình duyệt.
/// Kết hợp SemaphoreSlim trong tiến trình và FileStream độc quyền (FileShare.None) cấp hệ điều hành.
/// </summary>
public sealed class ProfileOwner : IProfileOwner, IDisposable
{
    private readonly Func<string, string> _userDataDirResolver;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<string, ActiveLockEntry> _activeLocks = new();
    private bool _disposed;

    private sealed record ActiveLockEntry(string OwnerTag, DateTime AcquiredAtUtc, FileStream? FileLockStream);

    public ProfileOwner(Func<string, string> userDataDirResolver)
    {
        _userDataDirResolver = userDataDirResolver ?? throw new ArgumentNullException(nameof(userDataDirResolver));
    }

    public async Task<IProfileOwnerLock> AcquireOwnerLockAsync(
        string profileId,
        string ownerTag,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerTag);

        var semaphore = _semaphores.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));

        bool acquired = await semaphore.WaitAsync(timeout, cancellationToken);
        if (!acquired)
        {
            var currentHolder = _activeLocks.TryGetValue(profileId, out var existing) ? existing.OwnerTag : "unknown";
            throw new TimeoutException($"Không thể chiếm khóa profile '{profileId}'. Đang bị nắm giữ bởi '{currentHolder}'.");
        }

        FileStream? fileStream = null;
        try
        {
            string userDataDir = _userDataDirResolver(profileId);
            Directory.CreateDirectory(userDataDir);

            string lockFilePath = Path.Combine(userDataDir, ".Fraylo_lock");
            fileStream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            var entry = new ActiveLockEntry(ownerTag, DateTime.UtcNow, fileStream);
            _activeLocks[profileId] = entry;

            return new ProfileOwnerLock(this, profileId, ownerTag, entry.AcquiredAtUtc);
        }
        catch
        {
            fileStream?.Dispose();
            semaphore.Release();
            throw;
        }
    }

    public bool IsLocked(string profileId)
    {
        return _activeLocks.ContainsKey(profileId);
    }

    public string? GetCurrentOwnerTag(string profileId)
    {
        return _activeLocks.TryGetValue(profileId, out var entry) ? entry.OwnerTag : null;
    }

    private void ReleaseLock(string profileId, string ownerTag)
    {
        if (_activeLocks.TryRemove(profileId, out var entry))
        {
            try
            {
                entry.FileLockStream?.Dispose();
            }
            catch
            {
                // Bỏ qua lỗi khi đóng file lock
            }
        }

        if (_semaphores.TryGetValue(profileId, out var sem))
        {
            try
            {
                sem.Release();
            }
            catch (ObjectDisposedException)
            {
                // Bỏ qua nếu semaphore đã dispose
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var kvp in _activeLocks)
        {
            try
            {
                kvp.Value.FileLockStream?.Dispose();
            }
            catch { }
        }
        _activeLocks.Clear();

        foreach (var sem in _semaphores.Values)
        {
            try
            {
                sem.Dispose();
            }
            catch { }
        }
        _semaphores.Clear();
    }

    private sealed class ProfileOwnerLock : IProfileOwnerLock
    {
        private readonly ProfileOwner _parent;
        private int _isDisposed;

        public string ProfileId { get; }
        public string OwnerTag { get; }
        public DateTime AcquiredAtUtc { get; }

        public ProfileOwnerLock(ProfileOwner parent, string profileId, string ownerTag, DateTime acquiredAtUtc)
        {
            _parent = parent;
            ProfileId = profileId;
            OwnerTag = ownerTag;
            AcquiredAtUtc = acquiredAtUtc;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                _parent.ReleaseLock(ProfileId, OwnerTag);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
