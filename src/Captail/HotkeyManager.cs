using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Captail;

/// <summary>Global hotkeys for saving a replay and toggling the replay buffer.</summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int SaveHotkeyId = 1;
    private const int ToggleHotkeyId = 2;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private readonly HwndSource _source;
    private (uint Modifiers, uint Vk)? _saveBinding;
    private (uint Modifiers, uint Vk)? _toggleBinding;

    public event Action? SaveRequested;
    public event Action? ToggleRequested;

    public HotkeyManager(string saveHotkey, string toggleHotkey)
    {
        _source = new HwndSource(new HwndSourceParameters("CaptailHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            HwndSourceHook = WndProc,
        });
        Rebind(saveHotkey, toggleHotkey);
    }

    public void Rebind(string saveHotkey, string toggleHotkey)
    {
        var newSave = Parse(saveHotkey);
        var newToggle = Parse(toggleHotkey);
        if (newSave == newToggle)
            throw new InvalidOperationException(
                Localization.Text("L.Hotkey.MustDiffer"));

        if (_saveBinding == newSave && _toggleBinding == newToggle)
            return;

        var oldSave = _saveBinding;
        var oldToggle = _toggleBinding;
        UnregisterCurrent();

        bool saveRegistered = false;
        try
        {
            if (!RegisterHotKey(_source.Handle, SaveHotkeyId, newSave.Modifiers, newSave.Vk))
                throw new InvalidOperationException(
                    Localization.Format("L.Hotkey.Occupied", saveHotkey));
            saveRegistered = true;

            if (!RegisterHotKey(_source.Handle, ToggleHotkeyId, newToggle.Modifiers, newToggle.Vk))
                throw new InvalidOperationException(
                    Localization.Format("L.Hotkey.Occupied", toggleHotkey));

            _saveBinding = newSave;
            _toggleBinding = newToggle;
        }
        catch
        {
            if (saveRegistered)
                UnregisterHotKey(_source.Handle, SaveHotkeyId);
            UnregisterHotKey(_source.Handle, ToggleHotkeyId);
            _saveBinding = null;
            _toggleBinding = null;
            Restore(oldSave, oldToggle);
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

    private void Restore((uint Modifiers, uint Vk)? save, (uint Modifiers, uint Vk)? toggle)
    {
        if (save is { } saveBinding &&
            RegisterHotKey(_source.Handle, SaveHotkeyId, saveBinding.Modifiers, saveBinding.Vk))
        {
            _saveBinding = saveBinding;
        }

        if (toggle is { } toggleBinding &&
            RegisterHotKey(_source.Handle, ToggleHotkeyId, toggleBinding.Modifiers, toggleBinding.Vk))
        {
            _toggleBinding = toggleBinding;
        }
    }

    private void UnregisterCurrent()
    {
        if (_saveBinding is not null)
            UnregisterHotKey(_source.Handle, SaveHotkeyId);
        if (_toggleBinding is not null)
            UnregisterHotKey(_source.Handle, ToggleHotkeyId);
        _saveBinding = null;
        _toggleBinding = null;
    }

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
}
