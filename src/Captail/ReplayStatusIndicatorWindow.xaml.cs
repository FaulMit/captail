using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Captail;

internal enum ReplayIndicatorState
{
    Active,
    Recovering,
    Error,
    Saved,
}

internal enum ReplayIndicatorPlacement
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public partial class ReplayStatusIndicatorWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint GwHwndPrev = 3;
    private static readonly nint HwndTopmost = new(-1);

    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _captureAffinityTimer;
    private readonly DispatcherTimer _transientTimer;
    private ReplayIndicatorState? _state;
    private ReplayIndicatorPlacement _placement = ReplayIndicatorPlacement.TopRight;
    private ReplayIndicatorState _resumeState = ReplayIndicatorState.Active;
    private bool _transientActive;
    private bool _allowClose;
    private bool _captureAffinityFailureLogged;
    private bool _topmostFailureLogged;
    private uint? _captureAffinity;
    private nint _lastPositionForegroundWindow;
    private uint _lastForegroundProcessId;
    private bool _lastForegroundIsScreenCapture;
    private bool _gameDetected;
    private bool _firstFrameRendered;
#if DEBUG
    internal bool AllowCaptureForQa { get; set; }
#endif

    internal ReplayStatusIndicatorWindow()
    {
        InitializeComponent();
        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(750),
        };
        _positionTimer.Tick += (_, _) => PositionOnForegroundMonitor();
        _captureAffinityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _captureAffinityTimer.Tick += (_, _) => UpdateCaptureAffinity();
        _transientTimer = new DispatcherTimer();
        _transientTimer.Tick += (_, _) => ResumeAfterTransient();
        SourceInitialized += (_, _) => ConfigureWindow();
        ContentRendered += (_, _) => CompleteFirstFrame();
        Closing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                HideIndicator();
            }
        };
    }

    internal void SetState(ReplayIndicatorState state)
    {
        if (_transientActive && state == _resumeState)
            return;

        _transientTimer.Stop();
        _transientActive = false;
        ApplyState(state);
        ShowIndicator();
    }

    internal void SetPlacement(string placement)
    {
        ReplayIndicatorPlacement normalized = placement switch
        {
            "top-left" => ReplayIndicatorPlacement.TopLeft,
            "bottom-left" => ReplayIndicatorPlacement.BottomLeft,
            "bottom-right" => ReplayIndicatorPlacement.BottomRight,
            _ => ReplayIndicatorPlacement.TopRight,
        };
        if (_placement == normalized)
            return;

        _placement = normalized;
        if (IsVisible)
            PositionOnForegroundMonitor();
    }

    internal void RefreshAccent()
    {
        if (_state is ReplayIndicatorState.Active or ReplayIndicatorState.Saved)
            ApplyState(_state.Value, force: true);
    }

    internal void SetGameDetected(bool gameDetected)
    {
        if (_gameDetected == gameDetected)
            return;

        _gameDetected = gameDetected;
        if (IsVisible)
            PositionOnForegroundMonitor();
    }

    internal void ShowTransient(
        ReplayIndicatorState state,
        ReplayIndicatorState resumeState,
        int durationMilliseconds)
    {
        _resumeState = resumeState;
        _transientActive = true;
        ApplyState(state, force: true);
        ShowIndicator();
        _transientTimer.Stop();
        _transientTimer.Interval = TimeSpan.FromMilliseconds(durationMilliseconds);
        _transientTimer.Start();
    }

    internal void HideIndicator()
    {
        _transientTimer.Stop();
        _positionTimer.Stop();
        _captureAffinityTimer.Stop();
        _transientActive = false;
        _state = null;
        StopAnimations();
        Hide();
    }

    internal void ClosePermanently()
    {
        _allowClose = true;
        _transientTimer.Stop();
        _positionTimer.Stop();
        _captureAffinityTimer.Stop();
        Close();
    }

    private void ShowIndicator()
    {
        if (!IsVisible)
        {
            _lastPositionForegroundWindow = 0;
            Opacity = 0;
            Show();
            BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseOut,
                    },
                });
        }
        PositionOnForegroundMonitor();
        _positionTimer.Start();
        _captureAffinityTimer.Start();
    }

    private void ApplyState(ReplayIndicatorState state, bool force = false)
    {
        if (!force && _state == state)
            return;

        _state = state;
        StopAnimations();
        SavedGlyph.Visibility = Visibility.Collapsed;
        ErrorGlyph.Visibility = Visibility.Collapsed;
        CenterDot.Visibility = Visibility.Visible;
        IndicatorRoot.Opacity = 1;

        Brush accent = state switch
        {
            ReplayIndicatorState.Recovering =>
                new SolidColorBrush(Color.FromRgb(242, 194, 66)),
            ReplayIndicatorState.Error =>
                new SolidColorBrush(Color.FromRgb(255, 95, 99)),
            _ => Application.Current.TryFindResource("AccentBrush") as Brush ??
                 new SolidColorBrush(Color.FromRgb(99, 224, 189)),
        };
        StateRing.Stroke = accent;
        CenterDot.Fill = accent;

        switch (state)
        {
            case ReplayIndicatorState.Active:
                StateRing.StrokeDashArray = new DoubleCollection([7, 3]);
                StartRotation(2200);
                break;
            case ReplayIndicatorState.Recovering:
                StateRing.StrokeDashArray = new DoubleCollection([2, 2.4]);
                StartRotation(1050);
                StateRing.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.38, 1, TimeSpan.FromMilliseconds(420))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                    });
                break;
            case ReplayIndicatorState.Error:
                CenterDot.Visibility = Visibility.Collapsed;
                ErrorGlyph.Visibility = Visibility.Visible;
                StateRing.StrokeDashArray = new DoubleCollection([1.2, 2.2]);
                StartRotation(650);
                IndicatorRoot.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.58, 1, TimeSpan.FromMilliseconds(280))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                    });
                break;
            case ReplayIndicatorState.Saved:
                CenterDot.Visibility = Visibility.Collapsed;
                SavedGlyph.Visibility = Visibility.Visible;
                SavedGlyph.Stroke = accent;
                StateRing.StrokeDashArray = null;
                IndicatorScale.BeginAnimation(
                    ScaleTransform.ScaleXProperty,
                    PulseAnimation());
                IndicatorScale.BeginAnimation(
                    ScaleTransform.ScaleYProperty,
                    PulseAnimation());
                break;
        }
    }

    private static DoubleAnimation PulseAnimation() =>
        new(0.78, 1, TimeSpan.FromMilliseconds(230))
        {
            EasingFunction = new BackEase
            {
                Amplitude = 0.22,
                EasingMode = EasingMode.EaseOut,
            },
        };

    private void StartRotation(int durationMilliseconds)
    {
        RingRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(durationMilliseconds))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
    }

    private void StopAnimations()
    {
        RingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        RingRotation.Angle = 0;
        StateRing.BeginAnimation(OpacityProperty, null);
        StateRing.Opacity = 1;
        IndicatorRoot.BeginAnimation(OpacityProperty, null);
        IndicatorRoot.Opacity = 1;
        IndicatorScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        IndicatorScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        IndicatorScale.ScaleX = 1;
        IndicatorScale.ScaleY = 1;
    }

    private void ResumeAfterTransient()
    {
        _transientTimer.Stop();
        _transientActive = false;
        ApplyState(_resumeState, force: true);
    }

    private void ConfigureWindow()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        Marshal.SetLastPInvokeError(0);
        int styles = GetWindowLong(hwnd, GwlExStyle);
        int error = Marshal.GetLastPInvokeError();
        if (styles == 0 && error != 0)
        {
            Log.Write($"Could not read recording indicator style: Win32 error {error}.");
            return;
        }

        Marshal.SetLastPInvokeError(0);
        int previousStyles = SetWindowLong(
            hwnd,
            GwlExStyle,
            styles | WsExTransparent | WsExToolWindow | WsExNoActivate);
        error = Marshal.GetLastPInvokeError();
        if (previousStyles == 0 && error != 0)
            Log.Write($"Could not make recording indicator click-through: Win32 error {error}.");

        PositionOnForegroundMonitor();
    }

    private void CompleteFirstFrame()
    {
        if (_firstFrameRendered)
            return;

        _firstFrameRendered = true;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (_state is ReplayIndicatorState state)
            ApplyState(state, force: true);
        InvalidateVisual();
        UpdateLayout();
        UpdateCaptureAffinity();
    }

    private void UpdateCaptureAffinity()
    {
        if (!_firstFrameRendered)
            return;

        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
            return;

#if DEBUG
        if (AllowCaptureForQa)
            return;
#endif

        SetCaptureAffinity(
            hwnd,
            IsScreenCaptureForeground() ? WdaNone : WdaExcludeFromCapture);
    }

    private bool IsScreenCaptureForeground()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0)
            return false;

        _ = GetWindowThreadProcessId(foreground, out uint processId);
        if (processId == 0)
            return false;
        if (processId == _lastForegroundProcessId)
            return _lastForegroundIsScreenCapture;

        _lastForegroundProcessId = processId;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string processName = process.ProcessName;
            _lastForegroundIsScreenCapture = processName.Equals(
                    "SnippingTool",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals(
                    "ScreenClippingHost",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals(
                    "ScreenSketch",
                    StringComparison.OrdinalIgnoreCase) ||
                processName.Equals(
                    "SnipAndSketch",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            _lastForegroundIsScreenCapture = false;
        }
        catch (InvalidOperationException)
        {
            _lastForegroundIsScreenCapture = false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _lastForegroundIsScreenCapture = false;
        }

        return _lastForegroundIsScreenCapture;
    }

    private void SetCaptureAffinity(nint hwnd, uint affinity)
    {
        if (_captureAffinity == affinity)
            return;

        if (SetWindowDisplayAffinity(hwnd, affinity))
        {
            _captureAffinity = affinity;
            return;
        }
        if (_captureAffinityFailureLogged)
            return;

        _captureAffinityFailureLogged = true;
        Log.Write(
            $"Could not update recording indicator capture affinity: Win32 error " +
            $"{Marshal.GetLastWin32Error()}.");
    }

    private void PositionOnForegroundMonitor()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
            return;

        nint foreground = GetForegroundWindow();
        bool foregroundChanged =
            foreground != 0 && foreground != _lastPositionForegroundWindow;
        _lastPositionForegroundWindow = foreground;
        nint monitor = MonitorFromWindow(
            foreground != 0 ? foreground : hwnd,
            MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return;

        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi > 0 ? dpi / 96d : 1d;
        int size = (int)Math.Round(30 * scale);
        int inset = (int)Math.Round(16 * scale);
        bool placeRight = _placement is
            ReplayIndicatorPlacement.TopRight or
            ReplayIndicatorPlacement.BottomRight;
        bool placeBottom = _placement is
            ReplayIndicatorPlacement.BottomLeft or
            ReplayIndicatorPlacement.BottomRight;
        // Desktop mode respects taskbar/date. Once Captail has a real game
        // hook, use full monitor bounds so the indicator returns to the
        // selected in-game corner.
        Rect bounds = _gameDetected ? info.Monitor : info.WorkArea;
        int left = placeRight
            ? bounds.Right - size - inset
            : bounds.Left + inset;
        int top = placeBottom
            ? bounds.Bottom - size - inset
            : bounds.Top + inset;
        // A fullscreen/topmost app can enter the z-order after Captail and
        // cover this window while it still has WS_EX_TOPMOST. Reassert the
        // native topmost position only after a foreground transition or when
        // another app fully covers it. Screen capture tools stay above the
        // indicator and can capture it for QA.
        bool screenCaptureForeground = IsScreenCaptureForeground();
        bool raiseAboveForeground =
            !screenCaptureForeground &&
            (foregroundChanged || IsCoveredByHigherWindow(hwnd));
        bool positioned = SetWindowPos(
            hwnd,
            raiseAboveForeground ? HwndTopmost : 0,
            left,
            top,
            size,
            size,
            SwpNoActivate |
            (raiseAboveForeground ? 0 : SwpNoZOrder));
        if (!positioned && raiseAboveForeground && !_topmostFailureLogged)
        {
            _topmostFailureLogged = true;
            Log.Write(
                $"Could not raise recording indicator above foreground window: " +
                $"Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private static bool IsCoveredByHigherWindow(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out Rect indicatorBounds))
            return false;

        for (nint current = GetWindow(hwnd, GwHwndPrev);
             current != 0;
             current = GetWindow(current, GwHwndPrev))
        {
            _ = GetWindowThreadProcessId(current, out uint processId);
            if (processId == Environment.ProcessId ||
                !IsWindowVisible(current) ||
                !GetWindowRect(current, out Rect bounds))
            {
                continue;
            }

            if (bounds.Left <= indicatorBounds.Left &&
                bounds.Top <= indicatorBounds.Top &&
                bounds.Right >= indicatorBounds.Right &&
                bounds.Bottom >= indicatorBounds.Bottom)
            {
                return true;
            }
        }
        return false;
    }

#if DEBUG
    internal bool RunFullscreenIndicatorQa(
        nint fullscreenWindow,
        out string details)
    {
        // Foreground activation can be denied to an unattended QA process.
        // Invalidate only the cached HWND so this call deterministically
        // exercises the same foreground-transition path as the timer.
        _lastPositionForegroundWindow = 0;
        PositionOnForegroundMonitor();
        nint hwnd = new WindowInteropHelper(this).Handle;
        int styles = GetWindowLong(hwnd, GwlExStyle);
        bool aboveFullscreen = IsWindowAbove(hwnd, fullscreenWindow);
        bool passed = hwnd != 0 &&
                      (styles & WsExTopmost) != 0 &&
                      (styles & WsExTransparent) != 0 &&
                      (styles & WsExNoActivate) != 0 &&
                      _positionTimer.IsEnabled &&
                      aboveFullscreen;
        details = $"hwnd={hwnd != 0}, topmost={(styles & WsExTopmost) != 0}, " +
                  $"transparent={(styles & WsExTransparent) != 0}, " +
                  $"noActivate={(styles & WsExNoActivate) != 0}, " +
                  $"timer={_positionTimer.IsEnabled}, " +
                  $"aboveFullscreen={aboveFullscreen}";
        return passed;
    }

    private static bool IsWindowAbove(nint candidate, nint reference)
    {
        for (nint current = GetWindow(reference, GwHwndPrev);
             current != 0;
             current = GetWindow(current, GwHwndPrev))
        {
            if (current == candidate)
                return true;
        }
        return false;
    }
#endif

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint hwnd,
        out uint processId);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);
}
