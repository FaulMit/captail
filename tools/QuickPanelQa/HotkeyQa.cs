using System.Reflection;
using System.Windows.Threading;
using Captail;

internal static class HotkeyQa
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run()
    {
        int requests = 0;
        bool passed = true;
        using var manager = new HotkeyManager("Ctrl+Alt+Shift+F9", "Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F");
        manager.QuickPanelRequested += () => requests++;
        var key = typeof(HotkeyManager).GetMethod("ProcessLowLevelKey", Private)!;
        var windowMessage = typeof(HotkeyManager).GetMethod("WndProc", Private)!;
        Check(typeof(HotkeyManager).GetField("_keyboardHook", Private)!.GetValue(manager) is nint hook && hook != 0,
            "ordinary quick-panel binding installs keyboard hook");

        Press(0x46, 7, true);
        Check(requests == 0, "keyboard hook defers panel work until callback returns");
        Pump();
        Check(requests == 1, "ordinary key invokes panel without WM_HOTKEY delivery");
        Message();
        Press(0x46, 7, true);
        Pump();
        Check(requests == 1, "ordinary key opens panel once across hook, WM_HOTKEY, and repeat");
        Press(0x46, 0, false);
        Message();
        Pump();
        Check(requests == 1, "late WM_HOTKEY after release cannot toggle panel again");

        Press(0x46, 6, true);
        Press(0x46, 7, true);
        Pump();
        Check(requests == 1, "wrong modifiers and modifier change during hold do not trigger panel");
        Press(0x46, 0, false);
        Press(0x46, 7, true);
        Press(0x46, 0, false);
        Pump();
        Check(requests == 2, "fresh press after release opens panel again");

        Press(0x46, 7, true);
        manager.Rebind("Ctrl+Alt+Shift+F9", "Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+G");
        Pump();
        Check(requests == 2, "rebind cancels queued action for previous binding");
        Message();
        Press(0x47, 7, true);
        Press(0x47, 0, false);
        Pump();
        Check(requests == 3, "new ordinary binding uses keyboard route");

        manager.Rebind("Ctrl+Alt+Shift+F9", "Ctrl+Alt+Shift+F10", "Ctrl+Shift+F");
        Press(0x46, 6, true);
        Press(0x46, 0, false);
        Pump();
        Check(requests == 4, "reported Ctrl+Shift+F binding works without WM_HOTKEY");

        manager.Rebind("Ctrl+Alt+Shift+F9", "Ctrl+Alt+Shift+F10", "Shift+F22");
        Press(0x85, 4, true);
        Press(0x85, 4, true);
        Press(0x85, 0, false);
        Pump();
        Check(requests == 5, "F13-F24 quick-panel binding keeps single-press behavior");
        Press(0x85, 4, true);
        manager.Dispose();
        Pump();
        Check(requests == 5, "disposal cancels queued panel action");
        var legacy = typeof(HotkeyManager).GetMethod("RunExtendedFunctionKeyQa",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (legacy is not null)
        {
            object?[] details = [null];
            Check(legacy.Invoke(null, details) is true,
                $"legacy extended-key QA remains valid: {details[0]}");
        }
        Console.WriteLine($"QUICK_PANEL_HOTKEY_QA {(passed ? "PASS" : "FAIL")}");
        return passed ? 0 : 1;

        void Press(uint virtualKey, uint modifiers, bool down) => key.Invoke(manager, [virtualKey, modifiers, down]);
        void Message() => windowMessage.Invoke(manager, [nint.Zero, 0x0312, new nint(3), nint.Zero, false]);
        void Check(bool condition, string message)
        {
            passed &= condition;
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {message}");
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
