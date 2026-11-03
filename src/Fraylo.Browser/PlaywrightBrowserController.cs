using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Playwright;
using Fraylo.Browser.Abstractions;
using Fraylo.Browser.Models;
using Fraylo.Contracts.Browser;

namespace Fraylo.Browser;

/// <summary>
/// Quản lý vòng đời tiến trình Playwright Chromium persistent context trên userDataDir của profile.
/// Tích hợp chặt chẽ với IProfileOwner để chiếm và giải phóng lock .titoai_lock,
/// cấu hình chế độ headless/visible và xử lý lỗi driver Playwright có cấu trúc, không crash ngầm.
/// </summary>
public sealed class PlaywrightBrowserController : IBrowserProcessController
{
    private readonly IProfileRegistry _profileRegistry;
    private readonly IProfileOwner _profileOwner;
    private readonly Func<Task<IPlaywright>>? _playwrightFactory;
    private readonly Func<string, BrowserTypeLaunchPersistentContextOptions, Task<IBrowserContext>>? _customPersistentLauncher;

    private readonly ConcurrentDictionary<string, PlaywrightBrowserContextHandle> _activeContexts = new();
    private readonly ConcurrentDictionary<string, ExternalLoginWindow> _loginWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _profileLaunchCloseLocks = new(StringComparer.OrdinalIgnoreCase);
    private IPlaywright? _playwright;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _disposed;

    public event EventHandler<string>? ContextClosed;

    public PlaywrightBrowserController(
        IProfileRegistry profileRegistry,
        IProfileOwner profileOwner,
        Func<Task<IPlaywright>>? playwrightFactory = null,
        Func<string, BrowserTypeLaunchPersistentContextOptions, Task<IBrowserContext>>? customPersistentLauncher = null)
    {
        _profileRegistry = profileRegistry ?? throw new ArgumentNullException(nameof(profileRegistry));
        _profileOwner = profileOwner ?? throw new ArgumentNullException(nameof(profileOwner));
        _playwrightFactory = playwrightFactory;
        _customPersistentLauncher = customPersistentLauncher;
    }

    public bool IsRunning(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return _activeContexts.TryGetValue(profileId, out var handle) && !handle.IsDisposed;
    }

    public bool IsLoginWindowOpen(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return _loginWindows.ContainsKey(profileId);
    }

    public IBrowserContextHandle? TryGetContext(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        return _activeContexts.TryGetValue(profileId, out var handle) && !handle.IsDisposed ? handle : null;
    }

    public async Task PrewarmAsync(CancellationToken cancellationToken = default)
    {
        if (_playwright != null || _disposed || _customPersistentLauncher != null) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_playwright == null && !_disposed && _customPersistentLauncher == null)
            {
                _playwright = _playwrightFactory != null
                    ? await _playwrightFactory()
                    : await Playwright.CreateAsync();
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public Task<IBrowserContextHandle> LaunchPersistentContextAsync(
        string profileId,
        bool isHeadless,
        CancellationToken cancellationToken = default)
    {
        return LaunchPersistentContextAsync(profileId, isHeadless, initialUrl: null, cancellationToken);
    }

    public async Task<IBrowserContextHandle> LaunchPersistentContextAsync(
        string profileId,
        bool isHeadless,
        string? initialUrl,
        CancellationToken cancellationToken = default)
        => await LaunchContextAsync(profileId, isHeadless, initialUrl, notifyLoginClose: !isHeadless, cancellationToken);

    public Task<IBrowserContextHandle> LaunchVisibleAutomationContextAsync(
        string profileId,
        CancellationToken cancellationToken = default) =>
        LaunchContextAsync(profileId, isHeadless: false, initialUrl: null, notifyLoginClose: false, cancellationToken);

    // Chrome thường (không gắn cờ automation) để người dùng đăng nhập; đóng hẳn trước khi tác vụ Playwright dùng lại profile.
    public async Task LaunchLoginWindowAsync(
        string profileId,
        string? initialUrl,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var profileLock = _profileLaunchCloseLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await profileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning(profileId) || _loginWindows.ContainsKey(profileId))
            {
                throw new InvalidOperationException($"Hồ sơ trình duyệt '{profileId}' hiện đang được mở.");
            }

            var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Không tìm thấy hồ sơ trình duyệt '{profileId}' trong hệ thống.");
            string userDataDir = _profileRegistry.GetUserDataDir(profileId, profile.Kind);
            Directory.CreateDirectory(userDataDir);

            string chromePath = FindChromeExecutable()
                ?? throw new InvalidOperationException("Chưa tìm thấy Google Chrome đã cài trên máy. Hãy cài Chrome rồi thử lại.");

            // Giữ .titoai_lock suốt lúc Chrome mở để tác vụ Playwright không chiếm cùng userDataDir
            var ownerLock = await _profileOwner.AcquireOwnerLockAsync(
                profileId,
                $"ChromeLogin-{Guid.NewGuid():N}",
                TimeSpan.FromSeconds(5),
                cancellationToken);

            Process? process = null;
            try
            {
                var startInfo = new ProcessStartInfo(chromePath) { UseShellExecute = false };
                startInfo.ArgumentList.Add($"--user-data-dir={userDataDir}");
                startInfo.ArgumentList.Add("--no-first-run");
                startInfo.ArgumentList.Add("--no-default-browser-check");
                // Chrome nhớ vị trí cửa sổ trong profile: sau lần chạy ẩn (-32000) cửa sổ đăng nhập sẽ mở ngoài màn hình nếu không ép lại.
                startInfo.ArgumentList.Add("--window-position=80,60");
                startInfo.ArgumentList.Add("--window-size=1280,900");
                if (!string.IsNullOrWhiteSpace(initialUrl))
                {
                    startInfo.ArgumentList.Add(initialUrl);
                }

                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Không khởi chạy được Google Chrome.");

                // shortcut: Chrome đã mở cùng userDataDir sẽ chuyển yêu cầu cho tiến trình đó rồi thoát ngay; chỉ phát hiện bằng thời gian thoát sớm
                await Task.Delay(HandoffCheckDelay, cancellationToken);
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Hồ sơ '{profileId}' đang được mở bởi một cửa sổ Chrome khác. Hãy đóng cửa sổ đó rồi thử lại.");
                }
            }
            catch
            {
                if (process != null) await StopProcessAsync(process);
                await ownerLock.DisposeAsync();
                throw;
            }

            var window = new ExternalLoginWindow(process, ownerLock);
            _loginWindows[profileId] = window;
            _ = WatchLoginWindowAsync(profileId, window);

            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                operationalState: BrowserOperationalState.Running,
                visibilityState: BrowserVisibilityState.VisibleActive,
                cancellationToken: cancellationToken);
        }
        finally
        {
            profileLock.Release();
        }
    }

    private async Task<IBrowserContextHandle> LaunchContextAsync(
        string profileId,
        bool isHeadless,
        string? initialUrl,
        bool notifyLoginClose,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var profileLock = _profileLaunchCloseLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await profileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning(profileId))
            {
                throw new InvalidOperationException($"Hồ sơ trình duyệt '{profileId}' hiện đang có tiến trình Playwright hoạt động.");
            }

            var profile = await _profileRegistry.GetProfileAsync(profileId, cancellationToken);
            if (profile == null)
            {
                throw new InvalidOperationException($"Không tìm thấy hồ sơ trình duyệt '{profileId}' trong hệ thống.");
            }

            string userDataDir = _profileRegistry.GetUserDataDir(profileId, profile.Kind);
            Directory.CreateDirectory(userDataDir);

            // Chiếm Single-Instance Lock (.titoai_lock) trước khi khởi chạy Playwright persistent context
            var ownerLock = await _profileOwner.AcquireOwnerLockAsync(
                profileId,
                $"PlaywrightProcess-{Guid.NewGuid():N}",
                TimeSpan.FromSeconds(5),
                cancellationToken);

            PlaywrightBrowserContextHandle? handle = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var launchOptions = new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = isHeadless,
                    Channel = "chrome",
                    // Playwright tắt sandbox mặc định; bật lại để giữ sandbox của Chrome.
                    ChromiumSandbox = true
                };
                // Cloudflare của ChatGPT/Gemini chặn cờ automation mặc định; TikTok giữ nguyên, không che.
                if (profile.Kind != BrowserProfileKind.TikTok)
                {
                    launchOptions.Args = ["--disable-blink-features=AutomationControlled"];
                    if (isHeadless)
                    {
                        // Chrome headless bị Cloudflare/ChatGPT nhận diện (không ra ô nhập); chạy có cửa sổ thật.
                        // Đẩy cửa sổ ra ngoài màn hình để ẩn.
                        launchOptions.Headless = false;
                        launchOptions.Args = [.. launchOptions.Args, "--window-position=-32000,-32000", "--window-size=1280,900"];
                    }
                    else
                    {
                        // Cửa sổ hiện phải ép về trong màn hình vì Chrome nhớ vị trí ẩn (-32000) của lần chạy trước.
                        launchOptions.Args = [.. launchOptions.Args, "--window-position=80,60", "--window-size=1280,900"];
                    }
                }

            IBrowserContext browserContext;

            if (_customPersistentLauncher != null)
            {
                browserContext = await _customPersistentLauncher(userDataDir, launchOptions);
            }
            else
            {
                await _initLock.WaitAsync(cancellationToken);
                try
                {
                    if (_playwright == null)
                    {
                        _playwright = _playwrightFactory != null
                            ? await _playwrightFactory()
                            : await Playwright.CreateAsync();
                    }
                }
                finally
                {
                    _initLock.Release();
                }

                browserContext = await _playwright.Chromium.LaunchPersistentContextAsync(
                    userDataDir,
                    launchOptions);
            }

            handle = new PlaywrightBrowserContextHandle(
                this,
                profileId,
                browserContext,
                ownerLock,
                isHeadless,
                notifyLoginClose);

            _activeContexts[profileId] = handle;

            // Bắt sự kiện Close trên Persistent Browser Context
            browserContext.Close += (_, _) =>
            {
                _ = HandleContextClosedAsync(profileId, handle);
            };

            // Bắt sự kiện Close trên từng Page để xử lý an toàn khi toàn bộ tab bị đóng
            void HookPage(IPage? page)
            {
                if (page == null) return;
                page.Close += (_, _) =>
                {
                    try
                    {
                        if (browserContext.Pages != null && browserContext.Pages.All(p => p == null || p.IsClosed))
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await browserContext.CloseAsync();
                                }
                                catch
                                {
                                }
                            });
                        }
                    }
                    catch
                    {
                    }
                };
            }

            try
            {
                if (browserContext.Pages != null)
                {
                    foreach (var p in browserContext.Pages)
                    {
                        HookPage(p);
                    }
                }
                browserContext.Page += (_, p) => HookPage(p);
            }
            catch
            {
                // Bỏ qua lỗi hook pages nếu mock context không hỗ trợ Pages hoặc Page event
            }

            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                operationalState: BrowserOperationalState.Running,
                visibilityState: isHeadless ? BrowserVisibilityState.Hidden : BrowserVisibilityState.VisibleActive,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrWhiteSpace(initialUrl))
            {
                try
                {
                    IPage? page = null;
                    try
                    {
                        if (browserContext.Pages != null)
                        {
                            page = browserContext.Pages.FirstOrDefault(p => p != null && !p.IsClosed && p.Url == "about:blank")
                                   ?? browserContext.Pages.FirstOrDefault(p => p != null && !p.IsClosed);
                        }
                    }
                    catch
                    {
                        // Bỏ qua lỗi truy cập collection nếu là mock proxy
                    }

                    if (page == null)
                    {
                        try
                        {
                            page = await browserContext.NewPageAsync();
                        }
                        catch
                        {
                            // Mock proxy không hỗ trợ NewPageAsync
                        }
                    }

                    if (page != null)
                    {
                        await page.GotoAsync(initialUrl, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.Commit,
                            Timeout = 15000
                        });

                        try
                        {
                            await page.BringToFrontAsync();
                        }
                        catch
                        {
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Kiểm tra nếu cửa sổ bị người dùng đóng trong lúc đang tải trang
                    bool isClosed = handle.IsDisposed
                        || !IsRunning(profileId)
                        || ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                        || ex.GetType().Name.Contains("TargetClosed", StringComparison.OrdinalIgnoreCase);

                    if (isClosed)
                    {
                        await handle.DisposeAsync();
                        RemoveActiveContext(profileId, handle);

                        throw new OperationCanceledException(
                            $"Phiên trình duyệt cho profile '{profileId}' đã bị đóng trước khi hoàn tất tải trang.", ex);
                    }

                    // Lỗi timeout mạng nhẹ không làm fail quá trình mở browser; Chromium sẽ tiếp tục tải trang
                }
            }

            return handle;
        }
        catch (PlaywrightException ex)
        {
            // Bắt lỗi Playwright (ví dụ driver/chromium chưa được cài đặt)
            // Giải phóng lock .titoai_lock ngay lập tức để không rò rỉ tài nguyên
            if (handle != null)
            {
                RemoveActiveContext(profileId, handle);
                await handle.DisposeAsync();
            }
            else
            {
                RemoveActiveContext(profileId);
                await ownerLock.DisposeAsync();
            }

            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                operationalState: BrowserOperationalState.Error,
                reasonCode: "playwright_driver_error",
                cancellationToken: cancellationToken);

            throw new InvalidOperationException(
                $"Khởi chạy Playwright Chromium thất bại cho profile '{profileId}'. " +
                $"Driver hoặc browser binary có thể chưa được cài đặt: {ex.Message}",
                ex);
        }
        catch (Exception)
        {
            // Đảm bảo luôn giải phóng lock nếu có bất kỳ ngoại lệ nào xảy ra khi khởi chạy
            if (handle != null)
            {
                RemoveActiveContext(profileId, handle);
                await handle.DisposeAsync();
            }
            else
            {
                RemoveActiveContext(profileId);
                await ownerLock.DisposeAsync();
            }
            throw;
        }
    }
    finally
    {
        profileLock.Release();
    }
}

    public async Task NavigateAsync(
        string profileId,
        string url,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        if (_activeContexts.TryGetValue(profileId, out var handle) && !handle.IsDisposed)
        {
            if (handle.Context.Pages != null)
            {
                try
                {
                    IPage? page = null;
                    try
                    {
                        page = handle.Context.Pages.FirstOrDefault(p => p != null && !p.IsClosed && p.Url == "about:blank")
                               ?? handle.Context.Pages.FirstOrDefault(p => p != null && !p.IsClosed);
                    }
                    catch
                    {
                    }

                    if (page == null)
                    {
                        try
                        {
                            page = await handle.Context.NewPageAsync();
                        }
                        catch
                        {
                        }
                    }

                    if (page != null)
                    {
                        await page.GotoAsync(url, new PageGotoOptions
                        {
                            WaitUntil = WaitUntilState.Commit,
                            Timeout = 15000
                        });

                        try
                        {
                            await page.BringToFrontAsync();
                        }
                        catch
                        {
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }

    public async Task CloseContextAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        var profileLock = _profileLaunchCloseLocks.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await profileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loginWindows.TryRemove(profileId, out var loginWindow))
            {
                await StopProcessAsync(loginWindow.Process);
                await ReleaseLoginWindowAsync(profileId, loginWindow);
                return;
            }

            if (_activeContexts.TryRemove(profileId, out var handle))
            {
                await handle.DisposeAsync();

                await _profileRegistry.UpdateProfileStateAsync(
                    profileId,
                    operationalState: BrowserOperationalState.Stopped,
                    visibilityState: BrowserVisibilityState.Hidden,
                    reasonCode: handle.NotifyLoginClose ? BrowserReasonCode.AuthClosed : null,
                    cancellationToken: cancellationToken);

                try
                {
                    if (handle.NotifyLoginClose) ContextClosed?.Invoke(this, profileId);
                }
                catch
                {
                }
            }
        }
        finally
        {
            profileLock.Release();
        }
    }

    private async Task HandleContextClosedAsync(string profileId, PlaywrightBrowserContextHandle handle)
    {
        // 1. Luôn dispose handle để giải phóng owner lock (.titoai_lock)
        try
        {
            await handle.DisposeAsync();
        }
        catch
        {
        }

        // 2. Kiểm tra xem handle này có còn là active handle hiện tại của profileId không
        // Nếu profileId đã mở phiên mới, handle này là stale từ phiên cũ -> KHÔNG phát event hoặc cập nhật DB
        if (_activeContexts.TryGetValue(profileId, out var currentHandle) && !ReferenceEquals(currentHandle, handle))
        {
            return;
        }

        RemoveActiveContext(profileId, handle);

        // 3. Cập nhật ProfileRegistry về Stopped/Hidden trước khi phát sự kiện
        try
        {
            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                operationalState: BrowserOperationalState.Stopped,
                visibilityState: BrowserVisibilityState.Hidden,
                reasonCode: handle.NotifyLoginClose ? BrowserReasonCode.AuthClosed : null);
        }
        catch
        {
        }

        // 4. Phát sự kiện ContextClosed ra ngoài sau khi DB đã cập nhật xong
        try
        {
            if (handle.NotifyLoginClose) ContextClosed?.Invoke(this, profileId);
        }
        catch
        {
        }
    }

    private async Task WatchLoginWindowAsync(string profileId, ExternalLoginWindow window)
    {
        try
        {
            await window.Process.WaitForExitAsync();
        }
        catch
        {
        }

        // CloseContextAsync có thể đã gỡ cửa sổ này và tự xử lý
        if (_loginWindows.TryRemove(new KeyValuePair<string, ExternalLoginWindow>(profileId, window)))
        {
            await ReleaseLoginWindowAsync(profileId, window);
        }
    }

    private async Task ReleaseLoginWindowAsync(string profileId, ExternalLoginWindow window)
    {
        window.Process.Dispose();
        await window.OwnerLock.DisposeAsync();

        try
        {
            await _profileRegistry.UpdateProfileStateAsync(
                profileId,
                operationalState: BrowserOperationalState.Stopped,
                visibilityState: BrowserVisibilityState.Hidden,
                reasonCode: BrowserReasonCode.AuthClosed);
        }
        catch
        {
        }

        try
        {
            ContextClosed?.Invoke(this, profileId);
        }
        catch
        {
        }
    }

    private static async Task StopProcessAsync(Process process)
    {
        if (process.HasExited) return;

        try
        {
            process.CloseMainWindow();
        }
        catch
        {
        }

        using var timeout = new CancellationTokenSource(LoginCloseTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    private static string? FindChromeExecutable() =>
        new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            }
            .Where(root => !string.IsNullOrEmpty(root))
            .Select(root => Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);

    private void RemoveActiveContext(string profileId, PlaywrightBrowserContextHandle? handle = null)
    {
        if (handle == null)
        {
            _activeContexts.TryRemove(profileId, out _);
        }
        else
        {
            var kvp = new KeyValuePair<string, PlaywrightBrowserContextHandle>(profileId, handle);
            ((ICollection<KeyValuePair<string, PlaywrightBrowserContextHandle>>)_activeContexts).Remove(kvp);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var handle in _activeContexts.Values)
        {
            try
            {
                await handle.DisposeAsync();
            }
            catch
            {
                // Bỏ qua lỗi dọn dẹp khi dispose
            }
        }
        _activeContexts.Clear();

        if (_playwright != null)
        {
            try
            {
                _playwright.Dispose();
            }
            catch { }
            _playwright = null;
        }

        _initLock.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static readonly TimeSpan HandoffCheckDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LoginCloseTimeout = TimeSpan.FromSeconds(5);

    private sealed record ExternalLoginWindow(Process Process, IProfileOwnerLock OwnerLock);

    private sealed class PlaywrightBrowserContextHandle : IBrowserContextHandle
    {
        private readonly PlaywrightBrowserController _controller;
        private int _isDisposed;

        public Guid SessionId { get; } = Guid.NewGuid();
        public string ProfileId { get; }
        public IBrowserContext Context { get; }
        public IProfileOwnerLock OwnerLock { get; }
        public bool IsHeadless { get; }
        public bool NotifyLoginClose { get; }
        public bool IsDisposed => Volatile.Read(ref _isDisposed) == 1;

        public PlaywrightBrowserContextHandle(
            PlaywrightBrowserController controller,
            string profileId,
            IBrowserContext context,
            IProfileOwnerLock ownerLock,
            bool isHeadless,
            bool notifyLoginClose)
        {
            _controller = controller;
            ProfileId = profileId;
            Context = context;
            OwnerLock = ownerLock;
            IsHeadless = isHeadless;
            NotifyLoginClose = notifyLoginClose;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                _controller.RemoveActiveContext(ProfileId, this);

                try
                {
                    await Context.CloseAsync();
                }
                catch
                {
                    // Bỏ qua lỗi đóng ngữ cảnh nếu browser đã tắt
                }

                try
                {
                    await OwnerLock.DisposeAsync();
                }
                catch
                {
                    // Bỏ qua lỗi giải phóng file lock
                }
            }
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
