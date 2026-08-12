using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using RaceTrade.Engine.Logging;
using RaceTrade.Web.Security;

namespace RaceTrade.Web.Services;

public sealed class TrayNotificationService : IHostedService, IDisposable
{
    private const uint TrayId = 8420;
    private const uint CallbackMessage = WindowMessages.WmApp + 842;
    private const int DedupeSeconds = 20;
    private const uint MenuOpen = 1001;
    private const uint MenuStartStop = 1002;
    private const uint MenuToggleRaceNotifications = 1003;
    private const uint MenuTestRaceNotification = 1004;
    private const uint MenuQuit = 1005;

    private readonly NotificationSettingsService _settings;
    private readonly WebSecurityOptions _security;
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ConcurrentQueue<TrayNotification> _queue = new();
    private readonly Dictionary<string, DateTimeOffset> _recentRaceNotifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private Thread? _trayThread;
    private ManualResetEventSlim? _ready;
    private WindowProc? _windowProc;
    private string _windowClass = "";
    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _iconOwned;
    private bool _trayAdded;
    private bool _disposed;
    private string _status = "Not started";

    public TrayNotificationService(
        NotificationSettingsService settings,
        WebSecurityOptions security,
        IServiceProvider services,
        IHostApplicationLifetime lifetime)
    {
        _settings = settings;
        _security = security;
        _services = services;
        _lifetime = lifetime;
    }

    public string StatusText => _status;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.Changed += OnSettingsChanged;

        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => ProcessNotifications(_workerCts.Token), CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            SetStatus("Tray icon is Windows-only.");
        }
        else if (!Environment.UserInteractive)
        {
            SetStatus("Tray icon unavailable in this non-interactive session.");
        }
        else if (_settings.Current.TrayIconEnabled)
        {
            EnsureTrayStarted();
        }
        else
        {
            SetStatus("Tray icon disabled in Settings.");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;

        _workerCts?.Cancel();

        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* shutdown should not hang on tray cleanup */ }
        }

        StopTrayThread();
    }

    public void NotifyRace(LogEvent entry)
    {
        if (!IsSupported || entry.Channel != LogChannel.Race)
            return;

        var settings = _settings.Current;
        if (!settings.TrayIconEnabled || !settings.RaceNotificationsEnabled)
            return;

        var status = (entry.Status ?? "").Trim();
        if (!ShouldNotify(status))
            return;

        var release = (entry.Release ?? "").Trim();
        if (string.IsNullOrWhiteSpace(release))
            return;

        var site = (entry.Site ?? "").Trim();
        var section = (entry.Section ?? "").Trim();
        var key = $"{status}|{site}|{section}|{release}";

        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            if (_recentRaceNotifications.TryGetValue(key, out var last) &&
                now - last < TimeSpan.FromSeconds(DedupeSeconds))
            {
                return;
            }

            _recentRaceNotifications[key] = now;

            foreach (var old in _recentRaceNotifications
                         .Where(kv => now - kv.Value > TimeSpan.FromMinutes(5))
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _recentRaceNotifications.Remove(old);
            }
        }

        EnsureTrayStarted();

        var title = status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            ? "Race failed"
            : $"Race {status.ToLowerInvariant()}";

        var details = BuildRaceBody(entry);
        _queue.Enqueue(new TrayNotification(title, details, NotificationIconFor(status)));
    }

    public bool TrySendTestRaceNotification(out string message)
    {
        if (!IsSupported)
        {
            message = StatusText;
            return false;
        }

        var settings = _settings.Current;
        if (!settings.TrayIconEnabled)
        {
            message = "Tray icon is disabled.";
            return false;
        }

        if (!settings.RaceNotificationsEnabled)
        {
            message = "Race notifications are disabled.";
            return false;
        }

        NotifyRace(new LogEvent
        {
            Channel = LogChannel.Race,
            Status = "Racing",
            Site = "SITE",
            Section = "TEST",
            Release = "RaceTrade.Tray.Notification.Test-TEST",
            TargetSite = "SITEA,SITEB"
        });

        message = "Test race notification queued.";
        return true;
    }

    private async Task ProcessNotifications(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                while (_queue.TryDequeue(out var notification))
                    ShowBalloon(notification);

                await Task.Delay(350, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Notifications are convenience UI. They must never disturb racing.
            }
        }
    }

    private void OnSettingsChanged()
    {
        if (!IsSupported) return;

        if (_settings.Current.TrayIconEnabled)
        {
            EnsureTrayStarted();
            RefreshTrayIcon();
        }
        else
        {
            StopTrayThread();
            SetStatus("Tray icon disabled in Settings.", log: true);
        }
    }

    private void EnsureTrayStarted()
    {
        if (!IsSupported || _disposed)
            return;

        lock (_gate)
        {
            if (_trayThread is not null)
                return;

            _ready = new ManualResetEventSlim(false);
            _trayThread = new Thread(TrayThreadMain)
            {
                IsBackground = true,
                Name = "RaceTrade tray"
            };

            if (OperatingSystem.IsWindows())
            {
                try { _trayThread.SetApartmentState(ApartmentState.STA); } catch { }
            }

            _trayThread.Start();
        }

        try { _ready?.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    private void StopTrayThread()
    {
        Thread? thread;
        IntPtr window;

        lock (_gate)
        {
            thread = _trayThread;
            window = _windowHandle;
            _trayThread = null;
        }

        if (window != IntPtr.Zero)
            NativeMethods.PostMessage(window, WindowMessages.WmClose, IntPtr.Zero, IntPtr.Zero);

        if (thread is not null && thread.IsAlive)
        {
            try { thread.Join(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    private void TrayThreadMain()
    {
        try
        {
            WindowProc windowProc = WndProc;
            _windowProc = windowProc;
            _windowClass = "RaceTradeTray_" + Guid.NewGuid().ToString("N");
            var windowProcPointer = Marshal.GetFunctionPointerForDelegate(windowProc);

            var instance = NativeMethods.GetModuleHandle(null);
            var wndClass = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = windowProcPointer,
                hInstance = instance,
                lpszClassName = _windowClass
            };

            if (NativeMethods.RegisterClassEx(ref wndClass) == 0)
            {
                SetStatus($"Tray icon failed: RegisterClassEx error {Marshal.GetLastWin32Error()}.", log: true, error: true);
                _ready?.Set();
                return;
            }

            var window = NativeMethods.CreateWindowEx(
                0,
                _windowClass,
                "RaceTrade Tray",
                0,
                0,
                0,
                0,
                0,
                NativeMethods.HwndMessage,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            lock (_gate) _windowHandle = window;

            if (window == IntPtr.Zero)
            {
                SetStatus($"Tray icon failed: CreateWindowEx error {Marshal.GetLastWin32Error()}.", log: true, error: true);
                _ready?.Set();
                return;
            }

            _iconHandle = LoadTrayIcon();
            AddTrayIcon(window);
            _ready?.Set();

            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Tray icon failed during startup: {ex.GetType().Name}: {ex.Message}", log: true, error: true);
            _ready?.Set();
        }
        finally
        {
            RemoveTrayIcon();

            if (_iconHandle != IntPtr.Zero)
            {
                if (_iconOwned)
                    NativeMethods.DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
                _iconOwned = false;
            }

            lock (_gate)
            {
                if (ReferenceEquals(_trayThread, Thread.CurrentThread))
                    _trayThread = null;

                _windowHandle = IntPtr.Zero;
                _trayAdded = false;
                _ready?.Set();
            }
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xffff;
            if (mouseMessage is WindowMessages.WmMouseMove)
                RefreshTrayIcon();
            else if (mouseMessage is WindowMessages.WmLButtonDoubleClick or WindowMessages.NinSelect or WindowMessages.NinKeySelect)
                OpenWebUi();
            else if (mouseMessage is WindowMessages.WmRButtonUp or WindowMessages.WmContextMenu)
                ShowContextMenu(hWnd);

            return IntPtr.Zero;
        }

        if (message == WindowMessages.WmDestroy)
        {
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu(IntPtr window)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            var notificationsEnabled = _settings.Current.RaceNotificationsEnabled;
            var racer = GetRacer();
            var startStopFlags = MenuFlags.String;
            if (racer?.IsBusy == true)
                startStopFlags |= MenuFlags.Disabled | MenuFlags.Grayed;

            NativeMethods.AppendMenu(menu, MenuFlags.String, new UIntPtr(MenuOpen), "Open RaceTrade");
            NativeMethods.AppendMenu(menu, MenuFlags.Separator, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, startStopFlags, new UIntPtr(MenuStartStop), racer?.IsRunning == true ? "Stop trader" : "Start trader");
            NativeMethods.AppendMenu(menu, MenuFlags.String | (notificationsEnabled ? MenuFlags.Checked : MenuFlags.None), new UIntPtr(MenuToggleRaceNotifications), "Race notifications");
            NativeMethods.AppendMenu(menu, MenuFlags.String, new UIntPtr(MenuTestRaceNotification), "Test race notification");
            NativeMethods.AppendMenu(menu, MenuFlags.Separator, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, MenuFlags.String, new UIntPtr(MenuQuit), "Quit RaceTrade");

            if (!NativeMethods.GetCursorPos(out var point))
                return;

            NativeMethods.SetForegroundWindow(window);

            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                TrackPopupMenuFlags.RightButton | TrackPopupMenuFlags.NoNotify | TrackPopupMenuFlags.ReturnCommand,
                point.X,
                point.Y,
                window,
                IntPtr.Zero);

            if (command != 0)
                HandleTrayCommand(command);

            NativeMethods.PostMessage(window, WindowMessages.WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private void HandleTrayCommand(uint command)
    {
        switch (command)
        {
            case MenuOpen:
                OpenWebUi();
                break;
            case MenuStartStop:
                _ = Task.Run(ToggleTraderFromTray);
                break;
            case MenuToggleRaceNotifications:
                ToggleRaceNotifications();
                break;
            case MenuTestRaceNotification:
                TrySendTestRaceNotification(out _);
                break;
            case MenuQuit:
                _ = Task.Run(QuitFromTray);
                break;
        }
    }

    private async Task ToggleTraderFromTray()
    {
        var racer = GetRacer();
        if (racer is null || racer.IsBusy)
            return;

        try
        {
            await racer.ToggleAsync();
            RefreshTrayIcon();
        }
        catch (Exception ex)
        {
            LogManager.Error($"Tray command failed: {ex.Message}");
        }
    }

    private void ToggleRaceNotifications()
    {
        try
        {
            var current = _settings.Current;
            _settings.Save(current with { RaceNotificationsEnabled = !current.RaceNotificationsEnabled });
            RefreshTrayIcon();
        }
        catch (Exception ex)
        {
            LogManager.Error($"Tray notification setting failed: {ex.Message}");
        }
    }

    private async Task QuitFromTray()
    {
        try
        {
            LogManager.Info("Quit requested from tray.");
            var chat = GetChat();
            if (chat is not null)
                await chat.StopAsync();

            var racer = GetRacer();
            if (racer?.IsRunning == true)
                await racer.ToggleAsync();
        }
        catch (Exception ex)
        {
            LogManager.Error($"Tray quit cleanup failed: {ex.Message}");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private void AddTrayIcon(IntPtr window)
    {
        var data = CreateNotifyIconData(window);
        data.uFlags = NotifyIconFlags.Message | NotifyIconFlags.Icon | NotifyIconFlags.Tip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _iconHandle;
        data.szTip = BuildTooltip();

        if (NativeMethods.ShellNotifyIcon(NotifyIconMessage.Add, ref data))
        {
            _trayAdded = true;
            SetStatus("Tray icon active. Windows may place it under hidden icons.", log: true);
        }
        else
        {
            SetStatus($"Tray icon failed: Shell_NotifyIcon error {Marshal.GetLastWin32Error()}.", log: true, error: true);
        }
    }

    private void RemoveTrayIcon()
    {
        lock (_gate)
        {
            if (!_trayAdded || _windowHandle == IntPtr.Zero)
                return;

            var data = CreateNotifyIconData(_windowHandle);
            NativeMethods.ShellNotifyIcon(NotifyIconMessage.Delete, ref data);
            _trayAdded = false;
        }
    }

    private void ShowBalloon(TrayNotification notification)
    {
        lock (_gate)
        {
            if (!_trayAdded || _windowHandle == IntPtr.Zero)
                return;

            var data = CreateNotifyIconData(_windowHandle);
            data.uFlags = NotifyIconFlags.Info | NotifyIconFlags.Tip;
            data.szTip = BuildTooltip();
            data.szInfoTitle = Truncate(notification.Title, 63);
            data.szInfo = Truncate(notification.Body, 255);
            data.dwInfoFlags = notification.Icon | BalloonIconFlags.RespectQuietTime;

            NativeMethods.ShellNotifyIcon(NotifyIconMessage.Modify, ref data);
        }
    }

    private void RefreshTrayIcon()
    {
        lock (_gate)
        {
            if (!_trayAdded || _windowHandle == IntPtr.Zero)
                return;

            var data = CreateNotifyIconData(_windowHandle);
            data.uFlags = NotifyIconFlags.Tip;
            data.szTip = BuildTooltip();

            NativeMethods.ShellNotifyIcon(NotifyIconMessage.Modify, ref data);
        }
    }

    private IntPtr LoadTrayIcon()
    {
        var exeIcon = LoadIconFromExecutable();
        if (exeIcon != IntPtr.Zero)
        {
            _iconOwned = true;
            return exeIcon;
        }

        var iconPath = ResolveIconPath();
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var loaded = NativeMethods.LoadImage(
                IntPtr.Zero,
                iconPath,
                ImageType.Icon,
                0,
                0,
                LoadImageFlags.LoadFromFile | LoadImageFlags.DefaultSize);

            if (loaded != IntPtr.Zero)
            {
                _iconOwned = true;
                return loaded;
            }
        }

        _iconOwned = false;
        return NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IdiApplication);
    }

    private static IntPtr LoadIconFromExecutable()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return IntPtr.Zero;

        var extracted = NativeMethods.ExtractIconEx(exePath, 0, out var largeIcon, out var smallIcon, 1);
        if (extracted == 0)
            return IntPtr.Zero;

        if (smallIcon != IntPtr.Zero)
        {
            if (largeIcon != IntPtr.Zero)
                NativeMethods.DestroyIcon(largeIcon);

            return smallIcon;
        }

        return largeIcon;
    }

    private static string? ResolveIconPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "RaceTrade.ico"),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "wwwroot", "favicon.ico"),
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "Assets", "RaceTrade.ico")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void OpenWebUi() => AppLauncher.OpenBrowser(BuildUrl());

    private string BuildUrl() =>
        $"http://{(_security.BindAddress == "0.0.0.0" ? "localhost" : _security.BindAddress)}:{_security.Port}";

    private string BuildTooltip()
    {
        var version = typeof(TrayNotificationService).Assembly.GetName().Version?.ToString(3) ?? "";
        var racer = GetRacer();
        var trader = racer is null ? "Unknown" : racer.IsBusy ? "Working" : racer.IsRunning ? "Running" : "Stopped";
        var notifications = _settings.Current.RaceNotificationsEnabled ? "On" : "Off";
        return $"RaceTrade v{version} | Trader: {trader}\nRace notifications: {notifications}\nOpen: {BuildUrl()}";
    }

    private RacerState? GetRacer()
    {
        try { return _services.GetService<RacerState>(); }
        catch { return null; }
    }

    private ChatHost? GetChat()
    {
        try { return _services.GetService<ChatHost>(); }
        catch { return null; }
    }

    private static NotifyIconData CreateNotifyIconData(IntPtr window) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = window,
        uID = TrayId,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private static bool ShouldNotify(string status) =>
        status.Equals("Racing", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Failed", StringComparison.OrdinalIgnoreCase);

    private static string BuildRaceBody(LogEvent entry)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(entry.Section))
            parts.Add($"[{entry.Section}]");

        parts.Add(entry.Release ?? "");

        if (!string.IsNullOrWhiteSpace(entry.TargetSite))
            parts.Add($"-> {entry.TargetSite}");

        if (!string.IsNullOrWhiteSpace(entry.Reason))
            parts.Add($"({entry.Reason})");

        return Truncate(string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))), 255);
    }

    private static BalloonIconFlags NotificationIconFor(string status) =>
        status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            ? BalloonIconFlags.Error
            : status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                ? BalloonIconFlags.Info
                : BalloonIconFlags.Warning;

    private static bool IsSupported => OperatingSystem.IsWindows() && Environment.UserInteractive;

    private static string Truncate(string value, int max)
    {
        value = value.Trim();
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
    }

    private void SetStatus(string status, bool log = false, bool error = false)
    {
        _status = status;

        if (!log) return;

        if (error)
            LogManager.Error(status);
        else
            LogManager.Info(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }

        _workerCts?.Dispose();
        _ready?.Dispose();
    }

    private sealed record TrayNotification(string Title, string Body, BalloonIconFlags Icon);

    private static class WindowMessages
    {
        public const uint WmNull = 0x0000;
        public const uint WmClose = 0x0010;
        public const uint WmDestroy = 0x0002;
        public const uint WmMouseMove = 0x0200;
        public const uint WmLButtonDoubleClick = 0x0203;
        public const uint WmRButtonUp = 0x0205;
        public const uint WmContextMenu = 0x007B;
        public const uint WmUser = 0x0400;
        public const uint NinSelect = WmUser;
        public const uint NinKeySelect = WmUser + 1;
        public const uint WmApp = 0x8000;
    }

    private enum NotifyIconMessage : uint
    {
        Add = 0x00000000,
        Modify = 0x00000001,
        Delete = 0x00000002,
        SetVersion = 0x00000004
    }

    [Flags]
    private enum NotifyIconFlags : uint
    {
        Message = 0x00000001,
        Icon = 0x00000002,
        Tip = 0x00000004,
        Info = 0x00000010
    }

    [Flags]
    private enum BalloonIconFlags : uint
    {
        Info = 0x00000001,
        Warning = 0x00000002,
        Error = 0x00000003,
        RespectQuietTime = 0x00000080
    }

    private enum ImageType : uint
    {
        Icon = 1
    }

    [Flags]
    private enum LoadImageFlags : uint
    {
        DefaultSize = 0x00000040,
        LoadFromFile = 0x00000010
    }

    [Flags]
    private enum MenuFlags : uint
    {
        None = 0x00000000,
        String = 0x00000000,
        Grayed = 0x00000001,
        Disabled = 0x00000002,
        Checked = 0x00000008,
        Separator = 0x00000800
    }

    [Flags]
    private enum TrackPopupMenuFlags : uint
    {
        RightButton = 0x00000002,
        NoNotify = 0x00000080,
        ReturnCommand = 0x00000100
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public NotifyIconFlags uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public BalloonIconFlags dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private static class NativeMethods
    {
        public static readonly IntPtr HwndMessage = new(-3);
        public static readonly IntPtr IdiApplication = new(32512);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShellNotifyIcon(NotifyIconMessage dwMessage, ref NotifyIconData lpData);

        [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint ExtractIconEx(
            string lpszFile,
            int nIconIndex,
            out IntPtr phiconLarge,
            out IntPtr phiconSmall,
            uint nIcons);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hWndParent,
            IntPtr hMenu,
            IntPtr hInstance,
            IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetMessage(out Message lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref Message lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref Message lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AppendMenu(IntPtr hMenu, MenuFlags uFlags, UIntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint TrackPopupMenuEx(
            IntPtr hmenu,
            TrackPopupMenuFlags fuFlags,
            int x,
            int y,
            IntPtr hwnd,
            IntPtr lptpm);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadImage(
            IntPtr hinst,
            string lpszName,
            ImageType uType,
            int cxDesired,
            int cyDesired,
            LoadImageFlags fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
