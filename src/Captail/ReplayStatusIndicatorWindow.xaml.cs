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
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint WdaNone = 0x00000000;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _captureAffinityTimer;
    private readonly DispatcherTimer _transientTimer;
    private ReplayIndicatorState? _state;
    private ReplayIndicatorPlacement _placement = ReplayIndicatorPlacement.TopRight;
    private ReplayIndicatorState _resumeState = ReplayIndicatorState.Active;
    private bool _transientActive;
    private bool _allowClose;
    private bool _captureAffinityFailureLogged;
    private uint? _captureAffinity;
    private uint _lastForegroundProcessId;
    private bool _lastForegroundIsScreenCapture;
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

        Color accent = state switch
        {
            ReplayIndicatorState.Recovering => Color.FromRgb(242, 194, 66),
            ReplayIndicatorState.Error => Color.FromRgb(255, 95, 99),
            _ => Color.FromRgb(99, 224, 189),
        };
        var brush = new SolidColorBrush(accent);
        StateRing.Stroke = brush;
        CenterDot.Fill = brush;

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
                SavedGlyph.Stroke = brush;
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

#if DEBUG
        if (!AllowCaptureForQa)
#endif
            SetCaptureAffinity(hwnd, WdaExcludeFromCapture);
        PositionOnForegroundMonitor();
    }

    private void UpdateCaptureAffinity()
    {
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
        int left = placeRight
            ? info.WorkArea.Right - size - inset
            : info.WorkArea.Left + inset;
        int top = placeBottom
            ? info.WorkArea.Bottom - size - inset
            : info.WorkArea.Top + inset;
        // Preserve current topmost-band order. Raising the window on every
        // timer tick would cover newer system overlays such as Snipping Tool.
        SetWindowPos(
            hwnd,
            0,
            left,
            top,
            size,
            size,
            SwpNoActivate | SwpNoZOrder);
    }

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
}
