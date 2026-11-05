using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Captail;

/// <summary>Global hotkeys for replay actions and the quick panel.</summary>
[SuppressMessage("Usage", "CA2216:Disposable types should declare finalizer",
    Justification = "HwndSource and keyboard hooks must be released on the UI thread; a finalizer cannot safely dispose them.")]
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    private const int WM_HOTKEY = 0x0312;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WhKeyboardLl = 13;
    private const int SaveHotkeyId = 1;
    private const int ToggleHotkeyId = 2;
    private const int QuickPanelHotkeyId = 3;
    private const int ScreenshotHotkeyId = 4;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const uint VK_F13 = 0x7C;
    private const uint VK_F24 = 0x87;

    private readonly HwndSource _source;
    private readonly LowLevelKeyboardProc _keyboardHookProc;
    private (uint Modifiers, uint Vk)? _saveBinding;
    private (uint Modifiers, uint Vk)? _toggleBinding;
    private (uint Modifiers, uint Vk)? _quickPanelBinding;
    private (uint Modifiers, uint Vk)? _screenshotBinding;
    private nint _keyboardHook;
    private bool _savePressed;
    private bool _togglePressed;
    private bool _quickPanelPressed;
    private bool _screenshotPressed;
    private int _bindingGeneration;

    public event Action? SaveRequested;
    public event Action? ToggleRequested;
    public event Action? QuickPanelRequested;
    public event Action? ScreenshotRequested;

    public HotkeyManager(string saveHotkey, string toggleHotkey,
        string quickPanelHotkey = "Ctrl+Shift+F11", string? screenshotHotkey = null)
    {
        _keyboardHookProc = KeyboardHookCallback;
        _source = new HwndSource(new HwndSourceParameters("CaptailHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            HwndSourceHook = WndProc,
        });
        try
        {
            Rebind(saveHotkey, toggleHotkey, quickPanelHotkey, screenshotHotkey);
        }
        catch
        {
            UnregisterCurrent();
            _source.Dispose();
            throw;
        }
    }

    public void Rebind(string saveHotkey, string toggleHotkey,
        string quickPanelHotkey = "Ctrl+Shift+F11", string? screenshotHotkey = null)
    {
        var newSave = Parse(saveHotkey);
        var newToggle = Parse(toggleHotkey);
        var newQuickPanel = Parse(quickPanelHotkey);
        (uint Modifiers, uint Vk)? newScreenshot = screenshotHotkey is null
            ? null : Parse(screenshotHotkey);
        if (newSave == newToggle || newSave == newQuickPanel ||
            newToggle == newQuickPanel || newScreenshot == newSave ||
            newScreenshot == newToggle || newScreenshot == newQuickPanel)
            throw new InvalidOperationException(
                Localization.Text("L.Hotkey.MustDiffer"));

        if (_saveBinding == newSave && _toggleBinding == newToggle &&
            _quickPanelBinding == newQuickPanel && _screenshotBinding == newScreenshot)
            return;

        var oldSave = _saveBinding;
        var oldToggle = _toggleBinding;
        var oldQuickPanel = _quickPanelBinding;
        var oldScreenshot = _screenshotBinding;
        UnregisterCurrent();

        try
        {
            if (!RegisterBinding(SaveHotkeyId, newSave))
                throw new InvalidOperationException(
                    Localization.Format("L.Hotkey.Occupied", saveHotkey));
            _saveBinding = newSave;

            if (!RegisterBinding(ToggleHotkeyId, newToggle))
                throw new InvalidOperationException(
                    Localization.Format("L.Hotkey.Occupied", toggleHotkey));

            _toggleBinding = newToggle;
            if (!RegisterBinding(QuickPanelHotkeyId, newQuickPanel))
                throw new InvalidOperationException(
                    Localization.Format("L.Hotkey.Occupied", quickPanelHotkey));
            _quickPanelBinding = newQuickPanel;
            if (newScreenshot is { } screenshot)
            {
                if (!RegisterBinding(ScreenshotHotkeyId, screenshot))
                    throw new InvalidOperationException(
                        Localization.Format("L.Hotkey.Occupied", screenshotHotkey!));
                _screenshotBinding = screenshot;
            }
            EnsureKeyboardHook();
        }
        catch
        {
            UnregisterCurrent();
            Restore(oldSave, oldToggle, oldQuickPanel, oldScreenshot);
            throw;
        }
    }

    public static bool IsValid(string hotkey)
    {
        try
        {
            _ = Parse(hotkey);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool AreDistinct(string first, string second)
    {
        try
        {
            return Parse(first) != Parse(second);
        }
        catch
        {
            return false;
        }
    }

    private void Restore((uint Modifiers, uint Vk)? save, (uint Modifiers, uint Vk)? toggle,
        (uint Modifiers, uint Vk)? quickPanel, (uint Modifiers, uint Vk)? screenshot)
    {
        if (save is { } saveBinding &&
            RegisterBinding(SaveHotkeyId, saveBinding))
        {
            _saveBinding = saveBinding;
        }

        if (toggle is { } toggleBinding &&
            RegisterBinding(ToggleHotkeyId, toggleBinding))
        {
            _toggleBinding = toggleBinding;
        }
        if (quickPanel is { } quickPanelBinding &&
            RegisterBinding(QuickPanelHotkeyId, quickPanelBinding))
            _quickPanelBinding = quickPanelBinding;
        if (screenshot is { } screenshotBinding &&
            RegisterBinding(ScreenshotHotkeyId, screenshotBinding))
            _screenshotBinding = screenshotBinding;

        try
        {
            EnsureKeyboardHook();
        }
        catch (Exception exception)
        {
            Log.Write($"Could not restore global keyboard hook: {exception.Message}");
        }
    }

    private void UnregisterCurrent()
    {
        _bindingGeneration++;
        if (_saveBinding is { } save && !IsExtendedFunctionKey(save.Vk))
            UnregisterHotKey(_source.Handle, SaveHotkeyId);
        if (_toggleBinding is { } toggle && !IsExtendedFunctionKey(toggle.Vk))
            UnregisterHotKey(_source.Handle, ToggleHotkeyId);
        if (_quickPanelBinding is { } quickPanel && !IsExtendedFunctionKey(quickPanel.Vk))
            UnregisterHotKey(_source.Handle, QuickPanelHotkeyId);
        if (_screenshotBinding is { } screenshot && !IsExtendedFunctionKey(screenshot.Vk))
            UnregisterHotKey(_source.Handle, ScreenshotHotkeyId);
        if (_keyboardHook != 0)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }
        _saveBinding = null;
        _toggleBinding = null;
        _quickPanelBinding = null;
        _screenshotBinding = null;
        _savePressed = false;
        _togglePressed = false;
        _quickPanelPressed = false;
        _screenshotPressed = false;
    }

    private bool RegisterBinding(int id, (uint Modifiers, uint Vk) binding) =>
        IsExtendedFunctionKey(binding.Vk) ||
        RegisterHotKey(_source.Handle, id,
            binding.Modifiers | (id is QuickPanelHotkeyId or ScreenshotHotkeyId ? MOD_NOREPEAT : 0), binding.Vk);

    private void EnsureKeyboardHook()
    {
        bool needed =
            (_saveBinding is { } save && IsExtendedFunctionKey(save.Vk)) ||
            (_toggleBinding is { } toggle && IsExtendedFunctionKey(toggle.Vk)) ||
            _quickPanelBinding.HasValue ||
            _screenshotBinding.HasValue;
        if (!needed || _keyboardHook != 0)
            return;

        _keyboardHook = SetWindowsHookEx(
            WhKeyboardLl,
            _keyboardHookProc,
            GetModuleHandle(null),
            0);
        if (_keyboardHook == 0)
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not install global keyboard hook.");
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && lParam != 0)
        {
            int message = unchecked((int)wParam);
            bool isDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool isUp = message is WM_KEYUP or WM_SYSKEYUP;
            if (isDown || isUp)
            {
                var data = Marshal.PtrToStructure<LowLevelKeyboardData>(lParam);
                ProcessLowLevelKey(data.VirtualKey, ReadModifierMask(), isDown);
            }
        }

        // Captail observes the key. It never blocks games or other applications.
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool ProcessLowLevelKey(uint virtualKey, uint modifiers, bool isDown)
    {
        bool handled = false;
        if (_screenshotBinding is { } screenshot && virtualKey == screenshot.Vk)
        {
            bool wasPressed = _screenshotPressed;
            _screenshotPressed = isDown;
            if (isDown && !wasPressed && modifiers == screenshot.Modifiers)
            {
                ScreenshotRequested?.Invoke();
                handled = true;
            }
        }
        if (_saveBinding is { } save &&
            IsExtendedFunctionKey(save.Vk) &&
            virtualKey == save.Vk)
        {
            if (!isDown)
            {
                _savePressed = false;
            }
            else if (!_savePressed && modifiers == save.Modifiers)
            {
                _savePressed = true;
                SaveRequested?.Invoke();
                handled = true;
            }
        }

        if (_toggleBinding is { } toggle &&
            IsExtendedFunctionKey(toggle.Vk) &&
            virtualKey == toggle.Vk)
        {
            if (!isDown)
            {
                _togglePressed = false;
            }
            else if (!_togglePressed && modifiers == toggle.Modifiers)
            {
                _togglePressed = true;
                ToggleRequested?.Invoke();
                handled = true;
            }
        }

        if (_quickPanelBinding is { } quickPanel && virtualKey == quickPanel.Vk)
        {
            bool wasPressed = _quickPanelPressed;
            _quickPanelPressed = isDown;
            if (isDown && !wasPressed && modifiers == quickPanel.Modifiers)
            {
                QueueQuickPanelRequest(quickPanel);
                handled = true;
            }
        }

        return handled;
    }

    private void QueueQuickPanelRequest((uint Modifiers, uint Vk) binding)
    {
        int generation = _bindingGeneration;
        nint foreground = GetForegroundWindow();
        if (_source.Dispatcher.HasShutdownStarted || _source.Dispatcher.HasShutdownFinished)
            return;

        // Return from WH_KEYBOARD_LL before creating or activating WPF windows.
        // Windows can silently remove a hook whose callback takes too long.
        _source.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (generation != _bindingGeneration || _quickPanelBinding != binding)
                return;
            Log.Event(DiagnosticLevel.Info, "Hotkeys", "quick-panel.request",
                new { route = "keyboard-hook", binding.Vk, binding.Modifiers,
                    foreground = $"0x{foreground:X}" });
            QuickPanelRequested?.Invoke();
        });
    }

    private static uint ReadModifierMask()
    {
        uint modifiers = 0;
        if (IsKeyDown(VK_CONTROL))
            modifiers |= MOD_CONTROL;
        if (IsKeyDown(VK_SHIFT))
            modifiers |= MOD_SHIFT;
        if (IsKeyDown(VK_MENU))
            modifiers |= MOD_ALT;
        return modifiers;
    }

    private static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsExtendedFunctionKey(uint virtualKey) =>
        virtualKey is >= VK_F13 and <= VK_F24;

#if DEBUG
    internal static bool RunExtendedFunctionKeyQa(out string details)
    {
        int saveCount = 0;
        int toggleCount = 0;
        int quickPanelCount = 0;
        using var manager = new HotkeyManager("F23", "Ctrl+F24", "Shift+F22");
        manager.SaveRequested += () => saveCount++;
        manager.ToggleRequested += () => toggleCount++;
        manager.QuickPanelRequested += () => quickPanelCount++;

        manager.ProcessLowLevelKey(0x86, 0, isDown: true);
        manager.ProcessLowLevelKey(0x86, 0, isDown: true);
        manager.ProcessLowLevelKey(0x86, 0, isDown: false);
        manager.ProcessLowLevelKey(0x86, MOD_SHIFT, isDown: true);
        manager.ProcessLowLevelKey(0x86, MOD_SHIFT, isDown: false);
        manager.ProcessLowLevelKey(0x86, 0, isDown: true);
        manager.ProcessLowLevelKey(0x86, 0, isDown: false);

        manager.ProcessLowLevelKey(0x87, 0, isDown: true);
        manager.ProcessLowLevelKey(0x87, 0, isDown: false);
        manager.ProcessLowLevelKey(0x87, MOD_CONTROL, isDown: true);
        manager.ProcessLowLevelKey(0x87, MOD_CONTROL, isDown: false);

        manager.ProcessLowLevelKey(0x85, MOD_SHIFT, isDown: true);
        manager.ProcessLowLevelKey(0x85, MOD_SHIFT, isDown: true);
        manager.ProcessLowLevelKey(0x85, MOD_SHIFT, isDown: false);

        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);

        bool passed = manager._keyboardHook != 0 &&
                      manager._saveBinding?.Vk == 0x86 &&
                      manager._toggleBinding?.Vk == 0x87 &&
                      manager._quickPanelBinding?.Vk == 0x85 &&
                      saveCount == 2 && toggleCount == 1 && quickPanelCount == 1;
        details = $"hook={manager._keyboardHook != 0}, " +
                  $"saveVk=0x{manager._saveBinding?.Vk:X2}, " +
                  $"toggleVk=0x{manager._toggleBinding?.Vk:X2}, " +
                  $"saveCount={saveCount}, toggleCount={toggleCount}, " +
                  $"quickPanelCount={quickPanelCount}";
        return passed;
    }
#endif

    private static (uint Modifiers, uint Vk) Parse(string hotkey)
    {
        uint modifiers = 0;
        uint vk = 0;
        int keyCount = 0;
        foreach (string rawPart in hotkey.Split('+'))
        {
            string part = rawPart.Trim();
            if (part.Length == 0)
                throw new FormatException(
                    Localization.Format("L.Hotkey.ParseError", hotkey));
            switch (part.ToUpperInvariant())
            {
                case "CTRL": modifiers |= MOD_CONTROL; break;
                case "SHIFT": modifiers |= MOD_SHIFT; break;
                case "ALT": modifiers |= MOD_ALT; break;
                default:
                    keyCount++;
                    var key = Enum.Parse<Key>(NormalizeKeyName(part), ignoreCase: true);
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        if (vk == 0 || keyCount != 1)
            throw new FormatException(
                Localization.Format("L.Hotkey.ParseError", hotkey));
        return (modifiers, vk);
    }

    private static string NormalizeKeyName(string name) => name.Length == 1 && char.IsDigit(name[0])
        ? "D" + name
        : name;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY)
            return 0;

        if (wParam == SaveHotkeyId)
            SaveRequested?.Invoke();
        else if (wParam == ToggleHotkeyId)
            ToggleRequested?.Invoke();
        else if (wParam == QuickPanelHotkeyId)
        {
            // Registration reserves the combination. The keyboard hook is the
            // sole dispatch route, so delayed WM_HOTKEY cannot close the panel.
        }
        else if (wParam == ScreenshotHotkeyId)
        {
            // The low-level hook already dispatched this press, including in fullscreen games.
        }
        else
            return 0;

        handled = true;
        return 0;
    }

    public void Dispose()
    {
        UnregisterCurrent();
        _source.Dispose();
    }

    private delegate nint LowLevelKeyboardProc(
        int code,
        nint wParam,
        nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardData
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nint ExtraInfo;
    }
}
