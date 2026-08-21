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
public sealed class MpvHost : HwndHost
{
    private const string HostWindowClass = "CaptailMpvHostWindow";
    private const int ErrorClassAlreadyExists = 1410;
    private const int BlackBrush = 4;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private static readonly object HostClassLock = new();
    private static readonly NativeWindowProcedure HostWindowProcedure = HostWindowProc;
    private static bool _hostClassRegistered;

    private readonly object _stateLock = new();
    private nint _hostHandle;
    private nint _mpvHandle;
    private CancellationTokenSource? _eventCancellation;
    private Task? _eventTask;
    private TaskCompletionSource<bool>? _fileLoadedCompletion;
    private TaskCompletionSource<bool>? _fileStoppedCompletion;
    private bool _fileLoaded;
    private bool _disposed;
    private double _lastPosition;
    private int _videoTrackId;
    private int[] _audioTrackIds = [];
    private (int Width, int Height) _requestedSize;

    public bool IsReady => !_disposed && _fileLoaded && _mpvHandle != 0;

    public bool IsBuffering => IsReady &&
        (string.Equals(
             MpvNative.GetPropertyStringValue(_mpvHandle, "paused-for-cache"),
             "yes",
             StringComparison.Ordinal) ||
         string.Equals(
             MpvNative.GetPropertyStringValue(_mpvHandle, "seeking"),
             "yes",
             StringComparison.Ordinal));

#if DEBUG
    internal int DetectedAudioTrackCount => _audioTrackIds.Length;

    internal bool TryValidateVideoOutput(out string details)
    {
        string Value(string name) =>
            MpvNative.GetPropertyStringValue(_mpvHandle, name) ?? "<none>";
        string videoId = Value("vid");
        string codec = Value("video-codec");
        string hardwareDecoder = Value("hwdec-current");
        string videoOutput = Value("current-vo");
        bool hasWidth = TryGetInt64("video-out-params/w", out long width) && width > 0;
        bool hasHeight = TryGetInt64("video-out-params/h", out long height) && height > 0;
        details =
            $"vid={videoId}, codec={codec}, hwdec={hardwareDecoder}, " +
            $"vo={videoOutput}, size={width}x{height}";
        return videoId != "no" && codec != "<none>" &&
               videoOutput != "<none>" && hasWidth && hasHeight;
    }
#endif

    public double PositionSeconds
    {
        get
        {
            if (TryGetDouble("time-pos", out double position) &&
                double.IsFinite(position) && position >= 0)
            {
                _lastPosition = position;
            }
            return _lastPosition;
        }
    }

    internal bool TryValidateGeometry(out string details)
    {
        if (_hostHandle == 0 || !GetClientRect(_hostHandle, out NativeRect host))
        {
            details = "preview host is not ready";
            return false;
        }

        int hostWidth = host.Right - host.Left;
        int hostHeight = host.Bottom - host.Top;
        nint videoWindow = FindWindowExW(_hostHandle, 0, null, null);
        if (videoWindow != 0 && GetClientRect(videoWindow, out NativeRect video))
        {
            int videoWidth = video.Right - video.Left;
            int videoHeight = video.Bottom - video.Top;
            details =
                $"requested={_requestedSize.Width}x{_requestedSize.Height}, " +
                $"host={hostWidth}x{hostHeight}, video={videoWidth}x{videoHeight}";
            return IsReady && hostWidth > 0 && hostHeight > 0 &&
                   videoWidth == hostWidth && videoHeight == hostHeight;
        }

        details =
            $"requested={_requestedSize.Width}x{_requestedSize.Height}, " +
            $"host={hostWidth}x{hostHeight}, direct-render=yes";
        return IsReady && hostWidth > 0 && hostHeight > 0;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureHostWindowClass();
        _hostHandle = CreateWindowExW(
            0,
            HostWindowClass,
            "",
            WsChild | WsVisible,
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

        try
        {
            InitializePlayer();
        }
        catch
        {
            DestroyWindow(_hostHandle);
            _hostHandle = 0;
            throw;
        }
        return new HandleRef(this, _hostHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Shutdown();
        if (hwnd.Handle != 0)
            DestroyWindow(hwnd.Handle);
        _hostHandle = 0;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeVideoWindow();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, ResizeVideoWindow);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, ResizeVideoWindow);
    }

    public async Task LoadAsync(
        string path,
        TimeSpan start,
        IReadOnlyList<int> audioTrackIds,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path))
            throw new FileNotFoundException("Replay file is unavailable.", path);
        EnsureInitialized();

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            _fileLoaded = false;
            _fileLoadedCompletion = completion;
        }

        SetProperty("pause", "yes");
        SetProperty("start", Seconds(start));
        Command("loadfile", path, "replace");

        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_fileLoadedCompletion, completion))
                    _fileLoadedCompletion = null;
            }
            throw;
        }

        RefreshTrackIds();
        if (_videoTrackId <= 0)
            throw new InvalidOperationException("Replay does not contain a video track.");
        SetProperty("vid", _videoTrackId.ToString(CultureInfo.InvariantCulture));
        SetAudioTracks(audioTrackIds);
        Seek(start.TotalSeconds, exact: true);
        Pause();
        ResizeVideoWindow();
        await WaitForVideoOutputAsync(cancellationToken);
    }

    public void Play()
    {
        if (!IsReady)
            return;
        SetProperty("pause", "no");
    }

    public void Pause()
    {
        if (!IsReady)
            return;
        _lastPosition = PositionSeconds;
        SetProperty("pause", "yes");
    }

    public void SetPlaybackSpeed(double speed)
    {
        if (!IsReady)
            return;
        double normalized = Math.Clamp(speed, 0.25, 2.0);
        SetProperty(
            "speed",
            normalized.ToString("0.##", CultureInfo.InvariantCulture));
    }

    public void Seek(double positionSeconds, bool exact)
    {
        if (!IsReady)
            return;
        double position = Math.Max(0, positionSeconds);
        _lastPosition = position;
        Command(
            "seek",
            position.ToString("0.###", CultureInfo.InvariantCulture),
            exact ? "absolute+exact" : "absolute+keyframes");
    }

    public void SetAudioTracks(IReadOnlyList<int> audioTrackOrdinals)
    {
        if (!IsReady)
            return;

        int[] ids = audioTrackOrdinals
            .Where(ordinal => ordinal > 0)
            .Select(ordinal => ordinal <= _audioTrackIds.Length
                ? _audioTrackIds[ordinal - 1]
                : ordinal)
            .Distinct()
            .Order()
            .ToArray();
        SetProperty("lavfi-complex", "");
        if (ids.Length == 0)
        {
            SetProperty("aid", "no");
            return;
        }
        if (ids.Length == 1)
        {
            SetProperty("aid", ids[0].ToString(CultureInfo.InvariantCulture));
            return;
        }

        SetProperty("aid", "no");
        string inputs = string.Concat(ids.Select(id => $"[aid{id}]"));
        string graph =
            $"{inputs}amix=inputs={ids.Length}:normalize=0:dropout_transition=0," +
            "alimiter=limit=0.95:level=disabled[ao]";
        SetProperty("lavfi-complex", graph);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsReady)
            return;
        _lastPosition = PositionSeconds;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
            _fileStoppedCompletion = completion;
        Command("stop");
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_fileStoppedCompletion, completion))
                    _fileStoppedCompletion = null;
            }
        }
    }

    public void Shutdown()
    {
        if (_disposed)
            return;
        _disposed = true;

        CancellationTokenSource? cancellation = _eventCancellation;
        Task? eventTask = _eventTask;
        cancellation?.Cancel();
        if (_mpvHandle != 0)
            MpvNative.Wakeup(_mpvHandle);
        try
        {
            eventTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Event loop is being cancelled during normal window teardown.
        }

        lock (_stateLock)
        {
            _fileLoaded = false;
            _fileLoadedCompletion?.TrySetCanceled();
            _fileLoadedCompletion = null;
            _fileStoppedCompletion?.TrySetCanceled();
            _fileStoppedCompletion = null;
        }
        if (_mpvHandle != 0)
        {
            MpvNative.TerminateDestroy(_mpvHandle);
            _mpvHandle = 0;
        }
        cancellation?.Dispose();
        _eventCancellation = null;
        _eventTask = null;
    }

    private void InitializePlayer()
    {
        _mpvHandle = MpvNative.Create();
        if (_mpvHandle == 0)
            throw new InvalidOperationException("Could not create libmpv playback context.");

        try
        {
            SetOption("wid", _hostHandle.ToInt64().ToString(CultureInfo.InvariantCulture));
            SetOption("config", "no");
            SetOption("load-scripts", "no");
            SetOption("terminal", "no");
            SetOption("msg-level", "all=no");
            SetOption("input-default-bindings", "no");
            SetOption("input-vo-keyboard", "no");
            SetOption("input-cursor", "no");
            SetOption("osc", "no");
            SetOption("idle", "yes");
            SetOption("keep-open", "yes");
            SetOption("force-window", "no");
            SetOption("profile", "fast");
            SetOption("vo", "gpu-next");
            SetOption("gpu-api", "d3d11");
            SetOption("gpu-context", "d3d11");
            SetOption("hwdec", "auto-safe");
            SetOption("video-sync", "audio");
            SetOption("interpolation", "no");
            SetOption("hr-seek", "yes");
            SetOption("hr-seek-framedrop", "yes");
            SetOption("track-auto-selection", "no");
            SetOption("audio-exclusive", "no");
            SetOption("audio-client-name", "Captail");
            SetOption("background-color", "#070A0C");

            int result = MpvNative.Initialize(_mpvHandle);
            ThrowOnError(result, "Could not initialize libmpv");
            _eventCancellation = new CancellationTokenSource();
            _eventTask = Task.Factory.StartNew(
                () => ProcessEvents(_eventCancellation.Token),
                _eventCancellation.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
        catch
        {
            MpvNative.TerminateDestroy(_mpvHandle);
            _mpvHandle = 0;
            throw;
        }
    }

    private void ProcessEvents(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _mpvHandle != 0)
        {
            nint eventPointer = MpvNative.WaitEvent(_mpvHandle, 0.25);
            if (eventPointer == 0)
                continue;
            MpvEvent playerEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            switch (playerEvent.EventId)
            {
                case MpvEventId.FileLoaded:
                    lock (_stateLock)
                    {
                        _fileLoaded = true;
                        _fileLoadedCompletion?.TrySetResult(true);
                        _fileLoadedCompletion = null;
                    }
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, ResizeVideoWindow);
                    break;

                case MpvEventId.EndFile:
                    HandleEndFile(playerEvent.Data);
                    break;

                case MpvEventId.VideoReconfig:
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, ResizeVideoWindow);
                    break;

                case MpvEventId.Shutdown:
                    return;
            }
        }
    }

    private void HandleEndFile(nint data)
    {
        MpvEventEndFile end = data == 0
            ? default
            : Marshal.PtrToStructure<MpvEventEndFile>(data);
        lock (_stateLock)
        {
            _fileLoaded = false;
            _fileStoppedCompletion?.TrySetResult(true);
            _fileStoppedCompletion = null;
            if (end.Reason == MpvEndFileReason.Error)
            {
                _fileLoadedCompletion?.TrySetException(
                    new InvalidOperationException(
                        $"libmpv could not load replay: {MpvNative.ErrorText(end.Error)}"));
            }
            _fileLoadedCompletion = null;
        }
    }

    private void SetOption(string name, string value) =>
        ThrowOnError(MpvNative.SetOptionString(_mpvHandle, name, value), $"libmpv option '{name}'");

    private void SetProperty(string name, string value) =>
        ThrowOnError(MpvNative.SetPropertyString(_mpvHandle, name, value), $"libmpv property '{name}'");

    private bool TryGetDouble(string name, out double value)
    {
        value = 0;
        return _mpvHandle != 0 && !_disposed &&
               MpvNative.GetPropertyDouble(
                   _mpvHandle,
                   name,
                   MpvFormat.Double,
                   out value) >= 0;
    }

    private void RefreshTrackIds()
    {
        if (!TryGetInt64("track-list/count", out long count) || count <= 0)
        {
            _videoTrackId = 0;
            _audioTrackIds = [];
            return;
        }

        _videoTrackId = 0;
        var ids = new List<int>();
        for (int index = 0; index < count; index++)
        {
            string prefix = $"track-list/{index}";
            string? type = MpvNative.GetPropertyStringValue(_mpvHandle, $"{prefix}/type");
            if (!TryGetInt64($"{prefix}/id", out long id) ||
                id is <= 0 or > int.MaxValue)
            {
                continue;
            }
            if (string.Equals(type, "video", StringComparison.Ordinal) &&
                _videoTrackId == 0)
            {
                _videoTrackId = (int)id;
            }
            else if (string.Equals(type, "audio", StringComparison.Ordinal))
            {
                ids.Add((int)id);
            }
        }
        _audioTrackIds = ids.ToArray();
    }

    private bool TryGetInt64(string name, out long value)
    {
        value = 0;
        return _mpvHandle != 0 && !_disposed &&
               MpvNative.GetPropertyInt64(
                   _mpvHandle,
                   name,
                   MpvFormat.Int64,
                   out value) >= 0;
    }

    private async Task WaitForVideoOutputAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            bool hasWidth = TryGetInt64("video-out-params/w", out long width) && width > 0;
            bool hasHeight = TryGetInt64("video-out-params/h", out long height) && height > 0;
            if (hasWidth && hasHeight)
                return;
            await Task.Delay(50, cancellationToken);
        }
        throw new TimeoutException("libmpv video output did not initialize in time.");
    }

    private void Command(params string[] arguments)
    {
        EnsureInitialized();
        nint[] pointers = new nint[arguments.Length + 1];
        GCHandle pinned = default;
        try
        {
            for (int index = 0; index < arguments.Length; index++)
                pointers[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            pinned = GCHandle.Alloc(pointers, GCHandleType.Pinned);
            ThrowOnError(
                MpvNative.Command(_mpvHandle, pinned.AddrOfPinnedObject()),
                $"libmpv command '{arguments.FirstOrDefault()}'");
        }
        finally
        {
            if (pinned.IsAllocated)
                pinned.Free();
            foreach (nint pointer in pointers)
            {
                if (pointer != 0)
                    Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mpvHandle == 0)
            throw new InvalidOperationException("Embedded preview player is not initialized.");
    }

    private static void ThrowOnError(int result, string operation)
    {
        if (result < 0)
            throw new InvalidOperationException($"{operation}: {MpvNative.ErrorText(result)}");
    }

    private void ResizeVideoWindow()
    {
        if (_hostHandle == 0)
            return;
        (int width, int height) = HostClientSize();
        _requestedSize = (width, height);
        nint videoWindow = FindWindowExW(_hostHandle, 0, null, null);
        if (videoWindow != 0)
        {
            SetWindowPos(
                videoWindow,
                0,
                0,
                0,
                width,
                height,
                SwpNoZOrder | SwpNoActivate);
        }
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
                    $"Could not register preview host window class ({error}).");
            }
            _hostClassRegistered = true;
        }
    }

    private static nint HostWindowProc(nint window, uint message, nint wParam, nint lParam) =>
        DefWindowProcW(window, message, wParam, lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public NativeWindowProcedure? WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public string? MenuName;
        public string? ClassName;
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEvent
    {
        public readonly MpvEventId EventId;
        public readonly int Error;
        public readonly ulong ReplyUserData;
        public readonly nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MpvEventEndFile
    {
        public readonly MpvEndFileReason Reason;
        public readonly int Error;
        public readonly long PlaylistEntryId;
        public readonly long PlaylistInsertId;
        public readonly int PlaylistInsertCount;
    }

    private enum MpvEventId
    {
        None = 0,
        Shutdown = 1,
        EndFile = 7,
        FileLoaded = 8,
        VideoReconfig = 17,
    }

    private enum MpvEndFileReason
    {
        Eof = 0,
        Stop = 2,
        Quit = 3,
        Error = 4,
        Redirect = 5,
    }

    private enum MpvFormat
    {
        Int64 = 4,
        Double = 5,
    }

    private static class MpvNative
    {
        private const string Library = "libmpv-2.dll";

        [DllImport(Library, EntryPoint = "mpv_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint Create();

        [DllImport(Library, EntryPoint = "mpv_initialize", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Initialize(nint handle);

        [DllImport(Library, EntryPoint = "mpv_terminate_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void TerminateDestroy(nint handle);

        [DllImport(Library, EntryPoint = "mpv_set_option_string", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetOptionString(
            nint handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Library, EntryPoint = "mpv_set_property_string", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetPropertyString(
            nint handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Library, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetPropertyDouble(
            nint handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            MpvFormat format,
            out double value);

        [DllImport(Library, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetPropertyInt64(
            nint handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            MpvFormat format,
            out long value);

        [DllImport(Library, EntryPoint = "mpv_get_property_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint GetPropertyString(
            nint handle,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Library, EntryPoint = "mpv_free", CallingConvention = CallingConvention.Cdecl)]
        private static extern void Free(nint data);

        internal static string? GetPropertyStringValue(nint handle, string name)
        {
            nint value = GetPropertyString(handle, name);
            if (value == 0)
                return null;
            try
            {
                return Marshal.PtrToStringUTF8(value);
            }
            finally
            {
                Free(value);
            }
        }

        [DllImport(Library, EntryPoint = "mpv_command", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Command(nint handle, nint arguments);

        [DllImport(Library, EntryPoint = "mpv_wait_event", CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint WaitEvent(nint handle, double timeout);

        [DllImport(Library, EntryPoint = "mpv_wakeup", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Wakeup(nint handle);

        [DllImport(Library, EntryPoint = "mpv_error_string", CallingConvention = CallingConvention.Cdecl)]
        private static extern nint ErrorString(int error);

        internal static string ErrorText(int error) =>
            Marshal.PtrToStringUTF8(ErrorString(error)) ?? $"error {error}";
    }

    private delegate nint NativeWindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WindowClassEx windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int objectIndex);
}
