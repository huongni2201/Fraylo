using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fraylo.Application.Accounts;
using Fraylo.Contracts.Accounts;
using Fraylo.Contracts.Jobs;
using Fraylo.Contracts.Providers;

namespace Fraylo.Application.Providers;

/// <summary>
/// Quản lý danh mục các adapter AI Provider đã đăng ký và cung cấp tra cứu capabilities chuẩn
/// theo execution-contract-v1.1 §1.2, §3.4.
/// Cho phép đăng ký adapter tùy biến hoặc fake adapter trong môi trường kiểm thử.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly ConcurrentDictionary<string, IProviderAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Đăng ký một adapter mới vào hệ thống.
    /// Ném ngoại lệ InvalidOperationException nếu providerKey đã được đăng ký trước đó.
    /// </summary>
    public void RegisterAdapter(IProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (string.IsNullOrWhiteSpace(adapter.ProviderKey))
        {
            throw new ArgumentException("ProviderKey của adapter không được để trống.", nameof(adapter));
        }

        var key = adapter.ProviderKey.Trim();
        if (!_adapters.TryAdd(key, adapter))
        {
            throw new InvalidOperationException($"Nhà cung cấp '{key}' đã được đăng ký trước đó trong hệ thống.");
        }
    }

    /// <summary>
    /// Đăng ký hoặc ghi đè một adapter vào hệ thống (thường dùng cho test mock/fake).
    /// </summary>
    public void RegisterOrUpdateAdapter(IProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (string.IsNullOrWhiteSpace(adapter.ProviderKey))
        {
            throw new ArgumentException("ProviderKey của adapter không được để trống.", nameof(adapter));
        }

        var key = adapter.ProviderKey.Trim();
        _adapters[key] = adapter;
    }

    /// <summary>
    /// Hủy đăng ký một adapter khỏi hệ thống.
    /// </summary>
    public bool UnregisterAdapter(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return false;
        }

        return _adapters.TryRemove(providerKey.Trim(), out _);
    }

    /// <summary>
    /// Kiểm tra xem providerKey đã có adapter đăng ký hay chưa.
    /// </summary>
    public bool HasAdapter(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return false;
        }

        return _adapters.ContainsKey(providerKey.Trim());
    }

    /// <summary>
    /// Lấy adapter đã đăng ký theo providerKey. Ném KeyNotFoundException nếu không tìm thấy.
    /// </summary>
    public IProviderAdapter GetAdapter(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new ArgumentException("ProviderKey không được để trống.", nameof(providerKey));
        }

        if (_adapters.TryGetValue(providerKey.Trim(), out var adapter))
        {
            return adapter;
        }

        throw new KeyNotFoundException($"Không tìm thấy adapter cho nhà cung cấp '{providerKey}'.");
    }

    /// <summary>
    /// Thử lấy adapter đã đăng ký.
    /// </summary>
    public bool TryGetAdapter(string providerKey, out IProviderAdapter? adapter)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            adapter = null;
            return false;
        }

        return _adapters.TryGetValue(providerKey.Trim(), out adapter);
    }

    /// <summary>
    /// Lấy danh sách capabilities cho một provider cụ thể hoặc toàn bộ các provider trong hệ thống.
    /// Nếu provider đã đăng ký adapter riêng, ưu tiên lấy từ adapter; nếu chưa, fallback về ProviderCapabilities mặc định.
    /// </summary>
    public IReadOnlyList<ProviderTaskCapabilityDto> GetCapabilities(string? providerKey = null)
    {
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            var key = providerKey.Trim();
            if (_adapters.TryGetValue(key, out var adapter))
            {
                return adapter.GetCapabilities();
            }

            return ProviderCapabilities.GetDefaultCapabilities(key);
        }

        // Nếu providerKey null/empty: Lấy tổng hợp từ tất cả registered adapters + default providers chưa đăng ký
        var result = new List<ProviderTaskCapabilityDto>();
        var handledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Lấy từ registered adapters
        foreach (var pair in _adapters)
        {
            result.AddRange(pair.Value.GetCapabilities());
            handledKeys.Add(pair.Key);
        }

        // 2. Bổ sung các default providers chưa có adapter riêng
        foreach (var defaultKey in ProviderKeys.All)
        {
            if (!handledKeys.Contains(defaultKey))
            {
                result.AddRange(ProviderCapabilities.GetDefaultCapabilities(defaultKey));
            }
        }

        return result;
    }

    /// <summary>
    /// Lấy capability cụ thể theo providerKey và taskType.
    /// </summary>
    public ProviderTaskCapabilityDto? GetCapability(string providerKey, string taskType)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(taskType))
        {
            return null;
        }

        var key = providerKey.Trim();
        var tType = taskType.Trim();

        if (_adapters.TryGetValue(key, out var adapter))
        {
            return adapter.GetCapabilities().FirstOrDefault(c => string.Equals(c.TaskType, tType, StringComparison.OrdinalIgnoreCase));
        }

        return ProviderCapabilities.GetDefaultCapability(key, tType);
    }

    /// <summary>
    /// Kiểm tra xem provider có hỗ trợ automation cho taskType này hay không.
    /// Quy tắc: Chỉ khi capability tồn tại, VerificationStatus == VERIFIED và AutomationSupported == true mới trả về true.
    /// Nếu NOT_VERIFIED, UNSUPPORTED hoặc AutomationSupported == false -> luôn trả về false.
    /// </summary>
    public bool IsAutomationSupported(string providerKey, string taskType)
    {
        var capability = GetCapability(providerKey, taskType);
        if (capability == null)
        {
            return false;
        }

        return (string.Equals(capability.VerificationStatus, ProviderVerificationStatus.Verified, StringComparison.OrdinalIgnoreCase)
            || string.Equals(capability.VerificationStatus, ProviderVerificationStatus.Trial, StringComparison.OrdinalIgnoreCase))
            && capability.AutomationSupported;
    }

    /// <summary>
    /// Kiểm tra loại tác vụ có trong năng lực provider; readiness automation được kiểm tra riêng.
    /// Hỏi trực tiếp adapter capabilities trước (nếu có), fallback về ProviderCapabilities.IsSupportedTask.
    /// </summary>
    public bool IsSupportedTask(string providerKey, string taskType)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(taskType))
        {
            return false;
        }

        var key = providerKey.Trim();
        var tType = taskType.Trim();

        if (_adapters.TryGetValue(key, out var adapter))
        {
            var cap = adapter.GetCapabilities().FirstOrDefault(c => string.Equals(c.TaskType, tType, StringComparison.OrdinalIgnoreCase));
            if (cap != null)
            {
                return !string.Equals(cap.VerificationStatus, ProviderVerificationStatus.Unsupported, StringComparison.OrdinalIgnoreCase);
            }
        }

        return ProviderCapabilities.IsSupportedTask(key, tType);
    }

    /// <summary>
    /// Lấy danh sách thời lượng clip (giây) được hỗ trợ bởi provider theo CLIP_DURATION_STORYBOARD_PLAN.
    /// Hỏi trực tiếp adapter capabilities trước (nếu có), fallback về ProviderCapabilities.GetSupportedDurations.
    /// </summary>
    public IReadOnlyList<int> GetSupportedDurations(string providerKey, string taskType = JobTaskType.VideoRender)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return Array.Empty<int>();
        }

        var key = providerKey.Trim();
        var tType = string.IsNullOrWhiteSpace(taskType) ? JobTaskType.VideoRender : taskType.Trim();

        if (_adapters.TryGetValue(key, out var adapter))
        {
            var cap = adapter.GetCapabilities().FirstOrDefault(c => string.Equals(c.TaskType, tType, StringComparison.OrdinalIgnoreCase));
            if (cap?.SupportedDurations != null && cap.SupportedDurations.Count > 0)
            {
                return cap.SupportedDurations;
            }
        }

        return ProviderCapabilities.GetSupportedDurations(key, tType);
    }

    /// <summary>
    /// Kiểm tra xem một thời lượng cụ thể có được hỗ trợ bởi provider hay không.
    /// </summary>
    public bool IsDurationSupported(string providerKey, int durationSeconds, string taskType = JobTaskType.VideoRender)
    {
        var supported = GetSupportedDurations(providerKey, taskType);
        return supported.Contains(durationSeconds);
    }

    /// <summary>
    /// Truy vấn danh sách tasks capabilities và tóm tắt trạng thái các tài khoản AI toàn cục (provider.list).
    /// </summary>
    public async Task<ProviderListResponseData> GetProviderListAsync(
        ProviderAccountService accountService,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountService);

        // 1. Lấy danh sách capabilities toàn app
        var tasks = GetCapabilities();

        // 2. Lấy danh sách accounts từ ProviderAccountService
        var accounts = await accountService.ListAccountsAsync(new ProviderAccountListRequest(), ct).ConfigureAwait(false);

        // 3. Chuyển đổi sang ProviderAccountSessionSummaryDto
        var accountSummaries = accounts.Select(a => new ProviderAccountSessionSummaryDto(
            ProviderAccountId: a.Id,
            ProviderKey: a.ProviderKey,
            ProfileId: a.ProfileId,
            DisplayName: a.DisplayName,
            VerifiedIdentity: a.VerifiedIdentity,
            SessionStatus: a.SessionStatus ?? (a.VerifiedIdentity != null ? "Connected" : "Unverified"),
            Enabled: a.Enabled,
            MaxConcurrentJobs: a.MaxConcurrentJobs,
            ActiveJobs: a.ActiveJobCount
        )).ToList();

        return new ProviderListResponseData(tasks, accountSummaries);
    }
}
