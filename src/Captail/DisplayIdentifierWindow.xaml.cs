using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Captail.Interop;

namespace Captail;

public partial class DisplayIdentifierWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private readonly CaptureInterop.MonitorInfo _monitor;

    internal DisplayIdentifierWindow(
        CaptureInterop.MonitorInfo monitor,
        int displayNumber)
    {
        _monitor = monitor;
        InitializeComponent();
        DisplayNumberText.Text = displayNumber.ToString(CultureInfo.InvariantCulture);
        SourceInitialized += (_, _) => ConfigureNativeWindow();
        Loaded += (_, _) => AnimateAndClose();
    }

    private void ConfigureNativeWindow()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        long styles = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtrW(
            hwnd,
            GwlExStyle,
            new nint(styles | WsExTransparent | WsExToolWindow | WsExNoActivate));

        double scale = 1;
        if (GetDpiForMonitor(_monitor.Handle, 0, out uint dpiX, out _) == 0 &&
            dpiX > 0)
        {
            scale = dpiX / 96d;
        }

        int width = Math.Max(220, (int)Math.Round(220 * scale));
        int height = Math.Max(150, (int)Math.Round(150 * scale));
        int left = _monitor.Left + ((_monitor.Width - width) / 2);
        int top = _monitor.Top + ((_monitor.Height - height) / 2);
        SetWindowPos(
            hwnd,
            HwndTopmost,
            left,
            top,
            width,
            height,
            SwpNoActivate | SwpShowWindow);
    }

    private void AnimateAndClose()
    {
        var opacity = new DoubleAnimationUsingKeyFrames();
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3500))));
        opacity.KeyFrames.Add(new EasingDoubleKeyFrame(
            0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3900)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        opacity.Completed += (_, _) => Close();

        var scale = new DoubleAnimation(
            0.9,
            1,
            TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        IdentifierCard.BeginAnimation(OpacityProperty, opacity);
        IdentifierScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
        IdentifierScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleYProperty,
            scale.Clone());
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);
}
