using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Runtime theme engine. The XAML palette (<c>Themes/Colors.xaml</c>) is the
/// baseline; this service shadows individual <see cref="Color"/> +
/// <see cref="SolidColorBrush"/> resources in the top-level Application
/// dictionary so every <c>DynamicResource</c> in the UI re-resolves instantly.
/// Presets and a user "Custom" override live in settings and are re-applied on
/// startup. Removing an override simply falls back to the XAML baseline.
/// </summary>
public static class ThemeService
{
    /// <summary>Palette keys the editor exposes, in display order, grouped.</summary>
    public static readonly (string Key, string Label, string Group)[] Editable =
    {
        ("Canvas",         "Window canvas",     "Background"),
        ("Surface",        "Surface",           "Background"),
        ("SurfaceAlt",     "Surface (alt)",     "Background"),
        ("Elevated",       "Elevated",          "Background"),
        ("ElevatedHover",  "Elevated (hover)",  "Background"),
        ("Hairline",       "Hairline",          "Border"),
        ("HairlineStrong", "Hairline (strong)", "Border"),
        ("TextPrimary",    "Text primary",      "Text"),
        ("TextSecondary",  "Text secondary",    "Text"),
        ("TextMuted",      "Text muted",        "Text"),
        ("Accent",         "Accent",            "Accent"),
        ("AccentHover",    "Accent (hover)",    "Accent"),
        ("AccentPressed",  "Accent (pressed)",  "Accent"),
        ("OnAccent",       "On-accent text",    "Accent"),
        ("Success",        "Success",           "Status"),
        ("Warning",        "Warning",           "Status"),
        ("Danger",         "Danger",            "Status"),
        ("Info",           "Info",              "Status"),
    };

    /// <summary>Built-in presets: name → (key → hex). Only keys that differ from
    /// the XAML baseline need to be listed; an empty map is the untouched default.</summary>
    public static readonly Dictionary<string, Dictionary<string, string>> Presets = new()
    {
        ["Avallon (mono)"] = new(),
        ["Indigo"] = new()
        {
            ["Accent"] = "#7B61FF", ["AccentHover"] = "#8F78FF",
            ["AccentPressed"] = "#6A50E8", ["OnAccent"] = "#0A0A0C",
        },
        ["Emerald"] = new()
        {
            ["Accent"] = "#34D399", ["AccentHover"] = "#4FE3AC",
            ["AccentPressed"] = "#25B583", ["OnAccent"] = "#05130D",
        },
        ["Amber"] = new()
        {
            ["Accent"] = "#F5B841", ["AccentHover"] = "#FFC85A",
            ["AccentPressed"] = "#E0A62E", ["OnAccent"] = "#1A1204",
        },
        ["Rose"] = new()
        {
            ["Accent"] = "#FB7185", ["AccentHover"] = "#FF8A9B",
            ["AccentPressed"] = "#E85D71", ["OnAccent"] = "#1C0709",
        },
        ["Ocean"] = new()
        {
            ["Canvas"] = "#080B12", ["CanvasTop"] = "#0D1220", ["CanvasBottom"] = "#05070C",
            ["Surface"] = "#0E1420", ["SurfaceAlt"] = "#121A28",
            ["Accent"] = "#38BDF8", ["AccentHover"] = "#5CCCFB",
            ["AccentPressed"] = "#2AA5DD", ["OnAccent"] = "#04121C",
        },
    };

    /// <summary>Resolves the effective palette for a settings object: the named
    /// preset merged with the user's per-key custom overrides (custom wins).</summary>
    public static Dictionary<string, string> Resolve(AppSettings s)
    {
        var map = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(s.ThemeName) && Presets.TryGetValue(s.ThemeName, out var preset))
            foreach (var kv in preset) map[kv.Key] = kv.Value;
        if (s.CustomTheme != null)
            foreach (var kv in s.CustomTheme) map[kv.Key] = kv.Value;
        return map;
    }

    /// <summary>Applies a settings object's theme to the live resource dictionary.</summary>
    public static void Apply(AppSettings s) => Apply(Resolve(s));

    /// <summary>Overrides the given colour keys (and their derived brushes) on the
    /// top-level Application dictionary. Any editable key that is <i>not</i> in the
    /// map is removed, so the XAML baseline shows through again.</summary>
    public static void Apply(IReadOnlyDictionary<string, string> overrides)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Apply(overrides)); return; }

        var res = app.Resources;

        // Drop previously-injected keys that are no longer overridden so switching
        // presets never leaves a stale colour behind.
        foreach (var (key, _, _) in Editable)
        {
            if (overrides.ContainsKey(key)) continue;
            res.Remove(key);
            res.Remove(key + "Brush");
        }
        if (!overrides.ContainsKey("CanvasTop"))    res.Remove("CanvasTop");
        if (!overrides.ContainsKey("CanvasBottom")) res.Remove("CanvasBottom");

        foreach (var kv in overrides)
        {
            if (!TryColor(kv.Value, out var c)) continue;
            res[kv.Key] = c;
            res[kv.Key + "Brush"] = new SolidColorBrush(c);
        }

        RebuildBackdrop(res, overrides);
    }

    /// <summary>The window backdrop is a gradient the XAML baked from StaticResource,
    /// so it won't react to a live canvas change — rebuild it when a canvas colour moved.</summary>
    static void RebuildBackdrop(ResourceDictionary res, IReadOnlyDictionary<string, string> ov)
    {
        if (!ov.ContainsKey("Canvas") && !ov.ContainsKey("CanvasTop") && !ov.ContainsKey("CanvasBottom"))
            return;
        var top    = ResolveColor(res, ov, "CanvasTop",    "#111114");
        var mid    = ResolveColor(res, ov, "Canvas",       "#0A0A0C");
        var bottom = ResolveColor(res, ov, "CanvasBottom", "#070709");
        var g = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        g.GradientStops.Add(new GradientStop(top, 0));
        g.GradientStops.Add(new GradientStop(mid, 0.35));
        g.GradientStops.Add(new GradientStop(bottom, 1));
        g.Freeze();
        res["WindowBackdropBrush"] = g;
    }

    static Color ResolveColor(ResourceDictionary res, IReadOnlyDictionary<string, string> ov,
                              string key, string fallback)
    {
        if (ov.TryGetValue(key, out var hex) && TryColor(hex, out var c)) return c;
        if (res[key] is Color rc) return rc;
        TryColor(fallback, out var f);
        return f;
    }

    /// <summary>Parses "#RRGGBB"/"#AARRGGBB"/named colours; false on garbage.</summary>
    public static bool TryColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        try { color = (Color)ColorConverter.ConvertFromString(hex); return true; }
        catch { return false; }
    }
}
