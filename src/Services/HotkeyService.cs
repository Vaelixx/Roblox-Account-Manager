using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Registers system-wide hotkeys (Win32 RegisterHotKey) and dispatches each press to
/// a named power-tool action. A hidden <see cref="NativeWindow"/> owns the hotkeys so
/// their WM_HOTKEY messages ride the WPF UI-thread pump — meaning handlers already run
/// on the Dispatcher and can touch the view-model directly.
/// </summary>
public static class HotkeyService
{
    private const int  WM_HOTKEY   = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;   // collapse held-down chords into one event

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private static MessageWindow? _window;
    private static Action<string>? _dispatch;
    private static readonly Dictionary<int, string> _actions = new();   // hotkey id -> action name
    private static int _nextId = 1;

    /// <summary>Wires the action dispatcher (invoked on the UI thread) exactly once.</summary>
    public static void Init(Action<string> dispatch) => _dispatch = dispatch;

    /// <summary>
    /// Re-reads settings and (re)registers every enabled binding. Safe to call repeatedly;
    /// it always clears the previous registrations first. Marshals to the UI thread because
    /// the message window's pump is the Dispatcher.
    /// </summary>
    public static void Apply()
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;   // headless / shutting down

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(Apply));
            return;
        }

        _window ??= CreateWindow();
        var hWnd = _window.Handle;

        // Drop the old set before laying down the new one.
        foreach (var id in _actions.Keys) UnregisterHotKey(hWnd, id);
        _actions.Clear();

        var seen = new HashSet<(uint, uint)>();
        foreach (var b in SettingsService.Current.Hotkeys)
        {
            if (!b.Enabled || b.Key == 0 || string.IsNullOrEmpty(b.Action)) continue;
            if (!seen.Add((b.Modifiers, b.Key))) continue;   // ignore a duplicate chord

            int id = _nextId++;
            if (RegisterHotKey(hWnd, id, b.Modifiers | MOD_NOREPEAT, b.Key))
                _actions[id] = b.Action;
            // A false return means another app already owns the chord — skip it quietly.
        }
    }

    /// <summary>Unregisters everything and tears the hidden window down (called at shutdown).</summary>
    public static void Stop()
    {
        if (_window == null) return;
        var hWnd = _window.Handle;
        foreach (var id in _actions.Keys) UnregisterHotKey(hWnd, id);
        _actions.Clear();
        // DestroyHandle (not ReleaseHandle) actually destroys the underlying HWND; a plain
        // ReleaseHandle only detaches the NativeWindow wrapper and leaks the message window.
        _window.DestroyHandle();
        _window = null;
    }

    private static MessageWindow CreateWindow()
    {
        var w = new MessageWindow();
        w.HotkeyPressed += id =>
        {
            if (_actions.TryGetValue(id, out var action))
                _dispatch?.Invoke(action);
        };
        return w;
    }

    /// <summary>Hidden helper window: never shown, just a home for the hotkey messages.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        public event Action<int>? HotkeyPressed;

        public MessageWindow() => CreateHandle(new CreateParams());

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
                HotkeyPressed?.Invoke(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }
}
