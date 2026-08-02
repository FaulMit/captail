using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Captail;

[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "WPF HwndHost owns the native window and calls DestroyWindowCore on the UI thread.")]
public sealed class FfplayHost : HwndHost
{
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsPopup = unchecked((long)0x80000000);
    private const string HostWindowClass = "CaptailPreviewHostWindow";
    private const int ErrorClassAlreadyExists = 1410;
    private const int BlackBrush = 4;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private static readonly object HostClassLock = new();
    private static readonly NativeWindowProcedure HostWindowProcedure = HostWindowProc;
    private static bool _hostClassRegistered;

    private readonly string _ffplayPath = Path.Combine(
        AppContext.BaseDirectory,
        "ffmpeg",
        "ffplay.exe");
    private readonly List<Process> _audioPlayers = [];
    private readonly List<Task> _drainTasks = [];
    private nint _hostHandle;
    private nint _playerHandle;
    private Process? _player;
    private CancellationTokenSource? _attachCts;
    private (int Width, int Height) _requestedSize;

    public bool IsReady => _playerHandle != 0 && _player is { HasExited: false };

    internal bool TryValidateGeometry(out string details)
    {
        if (_hostHandle == 0 || _playerHandle == 0 ||
            !GetClientRect(_hostHandle, out NativeRect host) ||
            !GetClientRect(_playerHandle, out NativeRect player))
        {
            details = "preview window is not ready";
            return false;
        }

        int hostWidth = host.Right - host.Left;
        int hostHeight = host.Bottom - host.Top;
        int playerWidth = player.Right - player.Left;
        int playerHeight = player.Bottom - player.Top;
        details =
            $"requested={_requestedSize.Width}x{_requestedSize.Height}, " +
            $"host={hostWidth}x{hostHeight}, player={playerWidth}x{playerHeight}";
        return hostWidth > 0 && hostHeight > 0 &&
               playerWidth == hostWidth && playerHeight == hostHeight;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureHostWindowClass();
        // Keep native host hidden until FFplay is attached and sized. Otherwise
        // Windows paints STATIC's default light background for one frame.
        _hostHandle = CreateWindowExW(
            0,
            HostWindowClass,
            "",
            (uint)WsChild,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            0,
            0,
            0);
        if (_hostHandle == 0)
            throw new InvalidOperationException("Could not create embedded preview host.");
        return new HandleRef(this, _hostHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Stop();
        if (hwnd.Handle != 0)
            DestroyWindow(hwnd.Handle);
        _hostHandle = 0;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizePlayer();
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            () => ResizePlayer(frameChanged: true));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            () => ResizePlayer(frameChanged: true));
    }

    public async Task<TimeSpan> PlayAsync(
        string path,
        TimeSpan start,
        TimeSpan duration,
        IReadOnlyList<int>? audioStreamIndices = null,
        CancellationToken cancellationToken = default)
    {
        Stop();
        if (!File.Exists(_ffplayPath))
            throw new FileNotFoundException("Bundled FFplay runtime is unavailable.", _ffplayPath);

        string title = $"CaptailPreview_{Guid.NewGuid():N}";
        (int previewWidth, int previewHeight) = HostClientSize();
        _requestedSize = (previewWidth, previewHeight);
        var startInfo = CreateBaseStartInfo();
        AddArguments(
            startInfo,
            [
                "-noborder", "-autoexit", "-an",
                "-x", previewWidth.ToString(CultureInfo.InvariantCulture),
                "-y", previewHeight.ToString(CultureInfo.InvariantCulture),
                "-left", "-32000", "-top", "-32000",
                "-window_title", title,
                "-ss", Seconds(start),
                "-t", Seconds(TimeSpan.FromSeconds(Math.Max(0.1, duration.TotalSeconds))),
                path,
            ]);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var startupClock = Stopwatch.StartNew();
        if (!process.Start())
            throw new InvalidOperationException("Could not start bundled preview player.");
        _player = process;
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        _drainTasks.Add(process.StandardOutput.ReadToEndAsync(cancellationToken));
        _attachCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            nint handle = 0;
            for (int attempt = 0; attempt < 100 && !process.HasExited; attempt++)
            {
                _attachCts.Token.ThrowIfCancellationRequested();
                process.Refresh();
                handle = process.MainWindowHandle;
                if (handle == 0)
                    handle = FindWindowW(null, title);
                if (handle != 0 && _hostHandle != 0)
                    break;
                await Task.Delay(20, _attachCts.Token);
            }

            if (handle == 0 || process.HasExited)
            {
                string error = await stderrTask;
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? "Preview player did not create a video window."
                        : error.Trim());
            }

            ShowWindow(handle, SwHide);
            SetParent(handle, _hostHandle);
            long style = GetWindowLongPtrW(handle, GwlStyle).ToInt64();
            style &= ~(WsPopup | WsCaption | WsThickFrame | WsVisible);
            style |= WsChild;
            SetWindowLongPtrW(handle, GwlStyle, new nint(style));
            _playerHandle = handle;
            ResizePlayer(frameChanged: true);

            // Give SDL time to replace its initial blank surface while window is
            // still off-screen and hidden. This removes visible startup flash.
            await Task.Delay(80, _attachCts.Token);
            ResizePlayer(frameChanged: true);

            TimeSpan startupDelay = startupClock.Elapsed;
            StartAudioPlayers(
                path,
                start + startupDelay,
                duration - startupDelay,
                audioStreamIndices ?? [],
                cancellationToken);

            ShowWindow(_hostHandle, SwShowNoActivate);
            ShowWindow(_playerHandle, SwShowNoActivate);
            ResizePlayer(frameChanged: true);
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                () => ResizePlayer(frameChanged: true));
            return startupDelay;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        _attachCts?.Cancel();
        _attachCts?.Dispose();
        _attachCts = null;
        if (_hostHandle != 0)
            ShowWindow(_hostHandle, SwHide);
        _playerHandle = 0;

        StopProcess(_player);
        _player = null;
        foreach (Process process in _audioPlayers)
            StopProcess(process);
        _audioPlayers.Clear();
        _drainTasks.Clear();
    }

    private void StartAudioPlayers(
        string path,
        TimeSpan start,
        TimeSpan duration,
        IReadOnlyList<int> streamIndices,
        CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero)
            return;

        foreach (int streamIndex in streamIndices.Distinct())
        {
            var startInfo = CreateBaseStartInfo();
            AddArguments(
                startInfo,
                [
                    "-nodisp", "-autoexit", "-vn",
                    "-ast", streamIndex.ToString(CultureInfo.InvariantCulture),
                    "-ss", Seconds(start),
                    "-t", Seconds(duration),
                    path,
                ]);
            Process? process = new() { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    continue;
                _audioPlayers.Add(process);
                _drainTasks.Add(process.StandardOutput.ReadToEndAsync(cancellationToken));
                _drainTasks.Add(process.StandardError.ReadToEndAsync(cancellationToken));
                process = null;
            }
            finally
            {
                process?.Dispose();
            }
        }
    }

    private ProcessStartInfo CreateBaseStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffplayPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        AddArguments(startInfo, ["-hide_banner", "-loglevel", "error", "-nostats"]);
        return startInfo;
    }

    private static void AddArguments(ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
    }

    private void ResizePlayer(bool frameChanged = false)
    {
        if (_playerHandle == 0)
            return;
        (int width, int height) = HostClientSize();
        SetWindowPos(
            _playerHandle,
            0,
            0,
            0,
            width,
            height,
            SwpNoZOrder | SwpNoActivate |
            (frameChanged ? SwpFrameChanged : 0));
        PostMessageW(
            _playerHandle,
            0x0005,
            0,
            new nint(((height & 0xFFFF) << 16) | (width & 0xFFFF)));
    }

    private (int Width, int Height) HostClientSize()
    {
        if (_hostHandle != 0 && GetClientRect(_hostHandle, out NativeRect rect))
        {
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width > 1 && height > 1)
                return (width, height);
        }
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return (
            Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)));
    }

    private static string Seconds(TimeSpan value) =>
        Math.Max(0, value.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    private static void StopProcess(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch
        {
            // Player already exited.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void EnsureHostWindowClass()
    {
        if (_hostClassRegistered)
            return;
        lock (HostClassLock)
        {
            if (_hostClassRegistered)
                return;
            var windowClass = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = HostWindowProcedure,
                Instance = GetModuleHandleW(null),
                BackgroundBrush = GetStockObject(BlackBrush),
                ClassName = HostWindowClass,
            };
            ushort atom = RegisterClassExW(ref windowClass);
            int error = Marshal.GetLastWin32Error();
            if (atom == 0 && error != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException(
                    $"Could not register embedded preview host ({error}).");
            }
            _hostClassRegistered = true;
        }
    }

    private static nint HostWindowProc(
        nint window,
        uint message,
        nint wParam,
        nint lParam) =>
        DefWindowProcW(window, message, wParam, lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint NativeWindowProcedure(
        nint window,
        uint message,
        nint wParam,
        nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public NativeWindowProcedure WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(
        nint window,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int objectIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(
        nint window,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string? className, string? windowName);
}
