using System.Windows.Input;
using System.Windows.Interop;
using static ScreenRecorder.App.Services.NativeMethods;

namespace ScreenRecorder.App.Services;

/// <summary>System-wide shortcuts that work while another app has focus (the main window is minimized while recording).</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyService()
    {
        // HWND_MESSAGE (-3): an invisible, message-only window that just receives the hotkey messages.
        _window = new HwndSource(new HwndSourceParameters("ScreenRecorderHotkeys") { ParentWindow = new IntPtr(-3) });
        _window.AddHook(WndProc);
    }

    /// <summary>Registers Ctrl+Shift+<paramref name="key"/>. Returns false if another app already owns that shortcut.</summary>
    public bool Register(Key key, Action action)
    {
        var id = _nextId++;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_window.Handle, id, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, vk)) return false;
        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}
