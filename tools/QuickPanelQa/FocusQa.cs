using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Captail;

internal static class FocusQa
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    internal static int Run()
    {
        bool passed = true;
        int saves = 0;
        // Models exclusive-fullscreen applications that minimize on deactivation.
        var game = new Window { Title = "Quick Panel fullscreen focus fixture", Width = 400, Height = 200 };
        var notification = new OverlayNotificationWindow();
        var config = new Config { CaptureMode = "replay", ReplayEnabled = true };
        var panel = new QuickPanelWindow(config, () => true, _ => true,
            _ => Task.FromResult(true), _ => Task.FromResult(true),
            enabled => Task.FromResult(enabled), (_, _, _, _) => Task.FromResult(true),
            () =>
            {
                saves++;
                notification.ShowNotification("✓", "Saved", "Focus QA", OverlayTone.Success, 1000);
            }, () => { }, () => Task.CompletedTask);
        try
        {
            game.Show();
            game.Activate();
            Pump(100);
            nint gameHandle = new WindowInteropHelper(game).Handle;
            Check(GetForegroundWindow() == gameHandle, "fixture owns foreground before panel opens");
            game.Deactivated += (_, _) => game.WindowState = WindowState.Minimized;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                panel.TogglePanel();
                Pump(350);
                Check(game.WindowState != WindowState.Minimized && GetForegroundWindow() == gameHandle,
                    $"open {attempt + 1}: fullscreen fixture stays foreground and unminimized");
                var save = (Button)panel.FindName("SaveReplayButton");
                nint panelHandle = new WindowInteropHelper(panel).Handle;
                Check((GetWindowLong(panelHandle, -20) & 0x08000000) != 0,
                    "panel native WS_EX_NOACTIVATE excludes automatic click activation");
                // Native pre-click activation decision must retain the click (MA_NOACTIVATE).
                Check(SendMessage(panelHandle, 0x0021, panelHandle, new nint((0x0201 << 16) | 1)) == 3,
                    "mouse click does not activate panel or discard input");
                Check(!save.Focusable, "Save Replay does not request WPF keyboard focus");
                save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Pump(100);
                Check(saves == attempt + 1, "Save Replay invokes save exactly once");
                Check(game.WindowState != WindowState.Minimized && GetForegroundWindow() == gameHandle,
                    "save and notification preserve fullscreen foreground");
                panel.TogglePanel();
                Pump(300);
                Check(!panel.IsVisible, "panel hotkey closes non-active panel");
            }
        }
        finally
        {
            panel.ClosePermanently();
            notification.ClosePermanently();
            game.Close();
        }
        Console.WriteLine($"QUICK_PANEL_FOCUS_QA {(passed ? "PASS" : "FAIL")}");
        return passed ? 0 : 1;

        void Check(bool condition, string message)
        {
            passed &= condition;
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {message}");
        }
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
