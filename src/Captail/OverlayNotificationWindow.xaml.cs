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
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly DispatcherTimer _hideTimer;
    private readonly TranslateTransform _translate = new();
    private bool _allowClose;

    public OverlayNotificationWindow()
    {
        InitializeComponent();
        Card.RenderTransform = _translate;
        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) => HideAnimated();
        SourceInitialized += (_, _) => MakeClickThrough();
        Closing += (_, e) =>
        {
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
        Brush accent = new SolidColorBrush(tone switch
        {
            OverlayTone.Warning => Color.FromRgb(224, 179, 99),
            OverlayTone.Error => Color.FromRgb(224, 130, 99),
            OverlayTone.Neutral => Color.FromRgb(148, 163, 171),
            _ => Color.FromRgb(99, 224, 189),
        });

        IconText.Text = glyph;
        IconText.Foreground = accent;
        IconRing.Stroke = accent;
        LifeBar.Background = accent;
        TitleText.Text = title;
        DetailText.Text = detail;

        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 24;
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(durationMilliseconds);
        _hideTimer.Start();

        if (!IsVisible)
            Show();

        Opacity = 0;
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
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Hide();
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
        int styles = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle,
            styles | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(nint hwnd, int index, int newStyle);
}
