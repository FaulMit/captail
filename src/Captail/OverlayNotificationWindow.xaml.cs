using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Captail;

public enum OverlayTone
{
    Success,
    Neutral,
    Warning,
    Error,
}

public partial class OverlayNotificationWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint GwHwndPrev = 3;
    private static readonly nint HwndTopmost = new(-1);

    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _topmostTimer;
    private readonly TranslateTransform _translate = new();
    private bool _allowClose;
    private bool _topmostFailureLogged;
    private nint _targetMonitor;
    // Bumped on every ShowNotification so a stale hide/fade from a previous
    // notification cannot dismiss the one currently on screen.
    private long _sequence;

    public OverlayNotificationWindow()
    {
        InitializeComponent();
        Card.RenderTransform = _translate;
        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) => HideAnimated();
        _topmostTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _topmostTimer.Tick += (_, _) => EnsureNativeTopmost(moveToTarget: false);
        SourceInitialized += (_, _) => MakeClickThrough();
        Closing += (_, e) =>
        {
            _hideTimer.Stop();
            _topmostTimer.Stop();
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public void ShowNotification(
        string glyph,
        string title,
        string detail,
        OverlayTone tone,
        int durationMilliseconds = 3200)
    {
        if (tone is OverlayTone.Success or OverlayTone.Neutral)
        {
            IconText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentBrush");
            IconRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
            LifeBar.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "AccentBrush");
        }
        else
        {
            Brush accent = new SolidColorBrush(tone == OverlayTone.Warning
                ? Color.FromRgb(224, 179, 99)
                : Color.FromRgb(224, 130, 99));
            IconText.Foreground = accent;
            IconRing.Stroke = accent;
            LifeBar.Background = accent;
        }

        _sequence++;

        IconText.Text = glyph;
        TitleText.Text = title;
        DetailText.Text = detail;

        _hideTimer.Stop();
        _topmostFailureLogged = false;
        _hideTimer.Interval = TimeSpan.FromMilliseconds(durationMilliseconds);
        _hideTimer.Start();

        Opacity = 0;
        if (!IsVisible)
            Show();
        _targetMonitor = ResolveTargetMonitor();
        EnsureNativeTopmost(moveToTarget: true);
        _topmostTimer.Start();

        _translate.X = 26;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        _translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(26, 0, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        LifeScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(durationMilliseconds)));
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    private void HideAnimated()
    {
        _hideTimer.Stop();
        long token = _sequence;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            // A newer notification may have appeared during the fade — don't hide it.
            if (token == _sequence)
            {
                _topmostTimer.Stop();
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, fade);
        _translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, 18, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            });
    }

    private void MakeClickThrough()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        Marshal.SetLastPInvokeError(0);
        int styles = GetWindowLong(hwnd, GwlExStyle);
        int error = Marshal.GetLastPInvokeError();
        if (styles == 0 && error != 0)
        {
            Log.Write($"Could not read overlay window style: Win32 error {error}.");
            return;
        }

        Marshal.SetLastPInvokeError(0);
        int previousStyles = SetWindowLong(hwnd, GwlExStyle,
            styles | WsExTransparent | WsExToolWindow | WsExNoActivate);
        error = Marshal.GetLastPInvokeError();
        if (previousStyles == 0 && error != 0)
            Log.Write($"Could not make overlay click-through: Win32 error {error}.");
    }

    private nint ResolveTargetMonitor()
    {
        nint foreground = GetForegroundWindow();
        nint hwnd = new WindowInteropHelper(this).Handle;
        return MonitorFromWindow(
            foreground != 0 ? foreground : hwnd,
            MonitorDefaultToNearest);
    }

    private void EnsureNativeTopmost(bool moveToTarget)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
            return;

        int x = 0;
        int y = 0;
        uint flags = SwpNoSize | SwpNoActivate | SwpShowWindow;
        if (!moveToTarget ||
            _targetMonitor == 0 ||
            !TryGetOverlayPosition(hwnd, _targetMonitor, out x, out y))
        {
            flags |= SwpNoMove;
        }

        if (!SetWindowPos(hwnd, HwndTopmost, x, y, 0, 0, flags) &&
            !_topmostFailureLogged)
        {
            _topmostFailureLogged = true;
            Log.Write(
                $"Could not raise overlay above fullscreen window: " +
                $"Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private static bool TryGetOverlayPosition(
        nint hwnd,
        nint monitor,
        out int x,
        out int y)
    {
        x = 0;
        y = 0;
        var info = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (!GetMonitorInfo(monitor, ref info) ||
            !GetWindowRect(hwnd, out NativeRect window))
        {
            return false;
        }

        int width = Math.Max(1, window.Right - window.Left);
        // Window has 12 px transparent shadow inset. This leaves visible card
        // 24 px from monitor's top-right work-area edge.
        x = info.Work.Right - width - 12;
        y = info.Work.Top + 12;
        return true;
    }

#if DEBUG
    internal bool RunFullscreenOverlayQa(
        nint fullscreenWindow,
        out string details)
    {
        ShowNotification("✓", "Fullscreen QA", "Overlay z-order", OverlayTone.Success, 60_000);
        nint hwnd = new WindowInteropHelper(this).Handle;
        int styles = GetWindowLong(hwnd, GwlExStyle);
        bool aboveFullscreen = IsWindowAbove(hwnd, fullscreenWindow);
        bool passed = hwnd != 0 &&
                      (styles & WsExTopmost) != 0 &&
                      (styles & WsExTransparent) != 0 &&
                      (styles & WsExNoActivate) != 0 &&
                      _topmostTimer.IsEnabled &&
                      _targetMonitor != 0 &&
                      aboveFullscreen;
        details = $"hwnd={hwnd != 0}, topmost={(styles & WsExTopmost) != 0}, " +
                  $"transparent={(styles & WsExTransparent) != 0}, " +
                  $"noActivate={(styles & WsExNoActivate) != 0}, " +
                  $"timer={_topmostTimer.IsEnabled}, monitor={_targetMonitor != 0}, " +
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

#if DEBUG
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);
#endif

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
