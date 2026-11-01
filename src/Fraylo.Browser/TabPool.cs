using System.Collections.Concurrent;
using System.Linq;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;

namespace Fraylo.Browser;

/// <summary>
/// Quản lý Tab Pool cho profile trình duyệt.
/// Nguyên tắc: mỗi job chạy trên một tab riêng biệt, cùng profile chia sẻ context.
/// Tối đa N job chạy đồng thời trên N tab riêng tương ứng.
/// </summary>
public sealed class TabPool : ITabPool
{
    // ProfileId -> (JobId -> BrowserTabHandle)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, BrowserTabHandle>> _profileTabs = new(StringComparer.OrdinalIgnoreCase);

    public Task<IBrowserTabHandle> AcquireTabAsync(
        string profileId,
        string role,
        string jobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        // Quy tắc Shared AI: chỉ cho phép 3 role tab hợp lệ và mỗi role tối đa 1 tab
        if (string.Equals(profileId, "shared_ai", StringComparison.OrdinalIgnoreCase))
        {
            if (!SharedAiTabRole.IsValid(role))
            {
                throw new ArgumentException(
                    $"Role '{role}' không hợp lệ cho Shared AI. Chỉ chấp nhận: {string.Join(", ", SharedAiTabRole.AllRoles)}.",
                    nameof(role));
            }

            var existingTabs = _profileTabs.GetOrAdd(profileId, _ => new ConcurrentDictionary<string, BrowserTabHandle>(StringComparer.OrdinalIgnoreCase));
            if (existingTabs.Values.Any(h => string.Equals(h.Role, role, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Không được phép cấp phát 2 job đồng thời trên cùng một tab.");
            }
        }

        var tabs = _profileTabs.GetOrAdd(profileId, _ => new ConcurrentDictionary<string, BrowserTabHandle>(StringComparer.OrdinalIgnoreCase));

        var newHandle = new BrowserTabHandle(this, profileId, role, jobId);
        if (!tabs.TryAdd(jobId, newHandle))
        {
            throw new InvalidOperationException(
                $"Tác vụ '{jobId}' đã chiếm một tab trong profile '{profileId}'.");
        }

        return Task.FromResult<IBrowserTabHandle>(newHandle);
    }

    public IReadOnlyList<string> GetActiveTabs(string profileId)
    {
        if (_profileTabs.TryGetValue(profileId, out var tabs))
        {
            return tabs.Values.Select(h => h.Role).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return [];
    }

    public int GetActiveJobCount(string profileId)
    {
        if (_profileTabs.TryGetValue(profileId, out var tabs))
        {
            return tabs.Count;
        }
        return 0;
    }

    public bool IsRoleActive(string profileId, string role)
    {
        if (_profileTabs.TryGetValue(profileId, out var tabs))
        {
            return tabs.Values.Any(h => string.Equals(h.Role, role, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    public IReadOnlyList<IBrowserTabHandle> GetAllActiveTabs(string profileId)
    {
        if (_profileTabs.TryGetValue(profileId, out var tabs))
        {
            return tabs.Values.Cast<IBrowserTabHandle>().ToList();
        }
        return [];
    }

    public Task ReleaseTabAsync(string profileId, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        if (_profileTabs.TryGetValue(profileId, out var tabs))
        {
            var matching = tabs.Where(kv => string.Equals(kv.Value.Role, role, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var kv in matching)
            {
                tabs.TryRemove(kv.Key, out _);
            }
        }

        return Task.CompletedTask;
    }

    public Task ReleaseTabAsync(string profileId, string role, string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (_profileTabs.TryGetValue(profileId, out var tabs))
        {
            tabs.TryRemove(jobId, out _);
        }

        return Task.CompletedTask;
    }

    public Task ResetTabContextAsync(string profileId, string role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        // Xóa context nghiệp vụ trong tab pool cho các tab có role tương ứng
        if (_profileTabs.TryGetValue(profileId, out var tabs))
        {
            foreach (var handle in tabs.Values.Where(h => string.Equals(h.Role, role, StringComparison.OrdinalIgnoreCase)))
            {
                handle.IsSideEffectSubmitted = false;
            }
        }

        return Task.CompletedTask;
    }

    private sealed class BrowserTabHandle : IBrowserTabHandle
    {
        private readonly TabPool _pool;
        private int _isDisposed;

        public string ProfileId { get; }
        public string Role { get; }
        public string JobId { get; }
        public bool IsSideEffectSubmitted { get; set; }
        public DateTime AcquiredAtUtc { get; }

        public BrowserTabHandle(TabPool pool, string profileId, string role, string jobId)
        {
            _pool = pool;
            ProfileId = profileId;
            Role = role;
            JobId = jobId;
            AcquiredAtUtc = DateTime.UtcNow;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                await _pool.ReleaseTabAsync(ProfileId, Role, JobId).ConfigureAwait(false);
            }
        }
    }
}
