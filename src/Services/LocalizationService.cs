using System.Collections.Generic;
using System.Linq;

namespace RobloxAccountManager.Services;

/// <summary>
/// Lightweight runtime localization. Rather than swapping satellite assemblies
/// (which needs an app restart), every translatable string is published into the
/// live resource dictionary under a <c>Str.*</c> key, so XAML binds via
/// <c>{DynamicResource Str.Nav.Accounts}</c> and switching language repaints
/// instantly. English is the source language and the fallback for any gap.
/// </summary>
public static class LocalizationService
{
    public static readonly (string Code, string Label)[] Languages =
    {
        ("en", "English"),
        ("de", "Deutsch"),
    };

    public static string LabelFor(string code) =>
        Languages.FirstOrDefault(l => l.Code == code).Label ?? "English";

    public static string CodeFor(string label) =>
        Languages.FirstOrDefault(l => l.Label == label).Code is { } c && c.Length > 0 ? c : "en";

    /// <summary>key → (lang-code → text). English must always be present.</summary>
    static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        // Navigation rail
        ["Nav.Accounts"]  = new() { ["en"] = "Accounts",  ["de"] = "Konten" },
        ["Nav.Servers"]   = new() { ["en"] = "Servers",   ["de"] = "Server" },
        ["Nav.Dashboard"] = new() { ["en"] = "Dashboard", ["de"] = "Übersicht" },
        ["Nav.Friends"]   = new() { ["en"] = "Friends",   ["de"] = "Freunde" },
        ["Nav.Settings"]  = new() { ["en"] = "Settings",  ["de"] = "Einstellungen" },

        // Settings section headers
        ["Settings.Launch"]      = new() { ["en"] = "Launch",       ["de"] = "Starten" },
        ["Settings.Performance"] = new() { ["en"] = "Performance",  ["de"] = "Leistung" },
        ["Settings.LiveData"]    = new() { ["en"] = "Live data",    ["de"] = "Live-Daten" },
        ["Settings.Interface"]   = new() { ["en"] = "Interface",    ["de"] = "Oberfläche" },
        ["Settings.Appearance"]  = new() { ["en"] = "Appearance",   ["de"] = "Erscheinungsbild" },
        ["Settings.Browser"]     = new() { ["en"] = "Browser",      ["de"] = "Browser" },
        ["Settings.Security"]    = new() { ["en"] = "Security",     ["de"] = "Sicherheit" },
        ["Settings.About"]       = new() { ["en"] = "About",        ["de"] = "Über" },

        // Appearance controls
        ["Appearance.Theme"]     = new() { ["en"] = "Theme preset",  ["de"] = "Theme-Vorlage" },
        ["Appearance.ThemeDesc"] = new() { ["en"] = "Base palette. Edit the swatches below to fine-tune any colour.",
                                           ["de"] = "Basis-Palette. Feintuning über die Farbfelder unten." },
        ["Appearance.ViewMode"]  = new() { ["en"] = "Account view",  ["de"] = "Konten-Ansicht" },
        ["Appearance.ViewDesc"]  = new() { ["en"] = "Card shows thumbnails; Compact is a dense list.",
                                           ["de"] = "Karte zeigt Thumbnails; Kompakt ist eine dichte Liste." },
        ["Appearance.Language"]  = new() { ["en"] = "Language",      ["de"] = "Sprache" },
        ["Appearance.Toasts"]    = new() { ["en"] = "In-app notifications",
                                           ["de"] = "In-App-Benachrichtigungen" },
        ["Appearance.Reset"]     = new() { ["en"] = "Reset theme",   ["de"] = "Theme zurücksetzen" },

        // Common verbs
        ["Common.Launch"] = new() { ["en"] = "Launch", ["de"] = "Starten" },
        ["Common.Close"]  = new() { ["en"] = "Close",  ["de"] = "Schließen" },
        ["Common.Save"]   = new() { ["en"] = "Save",   ["de"] = "Speichern" },
        ["Common.Cancel"] = new() { ["en"] = "Cancel", ["de"] = "Abbrechen" },
        ["Common.Add"]    = new() { ["en"] = "Add account", ["de"] = "Konto hinzufügen" },
    };

    public static string Get(string key, string code)
    {
        if (Table.TryGetValue(key, out var m))
            return m.TryGetValue(code, out var t) ? t
                 : m.TryGetValue("en", out var e) ? e : key;
        return key;
    }

    /// <summary>The language code currently applied to the live UI.</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>Raised after <see cref="Apply"/> swaps the active language, so
    /// data-bound labels that can't use <c>{DynamicResource}</c> can refresh.</summary>
    public static event System.Action? Changed;

    /// <summary>Publishes the whole table for <paramref name="code"/> into the live
    /// application resources under <c>Str.*</c> keys.</summary>
    public static void Apply(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) code = "en";
        var app = System.Windows.Application.Current;
        if (app == null) { Current = code; return; }
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Apply(code)); return; }
        Current = code;
        foreach (var kv in Table)
            app.Resources["Str." + kv.Key] = Get(kv.Key, code);
        Changed?.Invoke();
    }
}
