using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace RobloxAccountManager.Models;

/// <summary>
/// A single global (system-wide) hotkey mapped to a named power-tool action.
/// Modifiers is a Win32 MOD_* bitmask (Alt=1, Ctrl=2, Shift=4, Win=8); Key is a
/// Win32 virtual-key code. Only <see cref="Action"/> is a stable contract — the
/// UI resolves everything else at runtime, so serialised bindings survive renames.
/// </summary>
public class HotkeyBinding
{
    public string Action { get; set; } = "";
    public uint Modifiers { get; set; }
    public uint Key { get; set; }
    public bool Enabled { get; set; } = false;

    /// <summary>Human label for the action, e.g. "Launch selected".</summary>
    [JsonIgnore]
    public string ActionLabel => ActionLabels.TryGetValue(Action, out var l) ? l : Action;

    /// <summary>Chord as text, e.g. "Ctrl + Alt + L", or "—" when unset.</summary>
    [JsonIgnore]
    public string ChordText => Format(Modifiers, Key);

    /// <summary>The stable action ids this build knows how to dispatch, with labels.</summary>
    public static readonly Dictionary<string, string> ActionLabels = new()
    {
        ["LaunchSelected"]    = "Launch selected accounts",
        ["ServerHopSelected"] = "Server-hop selected accounts",
        ["CloseAllRoblox"]    = "Close all Roblox clients",
        ["FocusManager"]      = "Bring the manager to the front",
    };

    /// <summary>Formats a modifier bitmask + virtual-key code into a readable chord.</summary>
    public static string Format(uint modifiers, uint key)
    {
        if (key == 0) return "—";
        var sb = new StringBuilder();
        if ((modifiers & 2) != 0) sb.Append("Ctrl + ");
        if ((modifiers & 1) != 0) sb.Append("Alt + ");
        if ((modifiers & 4) != 0) sb.Append("Shift + ");
        if ((modifiers & 8) != 0) sb.Append("Win + ");
        sb.Append(KeyName(key));
        return sb.ToString();
    }

    /// <summary>Best-effort name for a Win32 virtual-key code.</summary>
    public static string KeyName(uint vk)
    {
        // Letters and digits map straight to ASCII.
        if (vk >= 0x30 && vk <= 0x5A) return ((char)vk).ToString();
        return vk switch
        {
            0x70 => "F1",  0x71 => "F2",  0x72 => "F3",  0x73 => "F4",
            0x74 => "F5",  0x75 => "F6",  0x76 => "F7",  0x77 => "F8",
            0x78 => "F9",  0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
            0x7C => "F13", 0x7D => "F14", 0x7E => "F15",
            0x20 => "Space",
            0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End",
            0x21 => "PageUp", 0x22 => "PageDown",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
            0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]",
            0xDC => "\\", 0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".", 0xBF => "/",
            _ => $"VK{vk:X2}",
        };
    }
}
