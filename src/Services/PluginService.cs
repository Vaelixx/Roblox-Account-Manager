using System.Reflection;
using RobloxAccountManager.Models;
using RobloxAccountManager.Plugins;

namespace RobloxAccountManager.Services;

/// <summary>
/// Discovers and loads plugin DLLs from the <c>plugins</c> folder next to the executable.
/// Each <see cref="IRamPlugin"/> type is instantiated and handed a shared <see cref="IPluginHost"/>.
/// All plugin entry points are wrapped so a throwing or hanging plugin is isolated from the app
/// and from other plugins. The app feeds lifecycle events in via the Raise* methods.
/// </summary>
public static class PluginService
{
    private static readonly object _gate = new();
    private static readonly List<LoadedPlugin> _plugins = new();
    private static readonly Host _host = new();
    private static Func<IReadOnlyList<Account>>? _accounts;

    /// <summary>Loaded plugins for display (name + version + source path + ok/error state).</summary>
    public static IReadOnlyList<LoadedPlugin> Plugins { get { lock (_gate) return _plugins.ToList(); } }

    public sealed class LoadedPlugin
    {
        public required string Name { get; init; }
        public required string Version { get; init; }
        public required string Path { get; init; }
        public string? Error { get; init; }
        public IRamPlugin? Instance { get; init; }
        public bool Ok => Error == null;
    }

    /// <summary>Wires the account provider the host uses to resolve launch/close targets.</summary>
    public static void Init(Func<IReadOnlyList<Account>> accountsProvider) => _accounts = accountsProvider;

    /// <summary>Folder scanned for plugin DLLs (created on first load if missing).</summary>
    public static string PluginDir => System.IO.Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Scans <see cref="PluginDir"/>, loads every DLL, and calls OnLoad on each plugin type.</summary>
    public static void Load()
    {
        lock (_gate)
        {
            if (_plugins.Count > 0) return; // already loaded

            string dir = PluginDir;
            try { System.IO.Directory.CreateDirectory(dir); } catch { return; }

            string[] dlls;
            try { dlls = System.IO.Directory.GetFiles(dir, "*.dll"); }
            catch { return; }

            foreach (var dll in dlls)
                LoadFile(dll);
        }
    }

    private static void LoadFile(string dll)
    {
        Assembly asm;
        try { asm = Assembly.LoadFrom(dll); }
        catch (Exception ex) { RecordFailure(dll, ex); return; }

        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
        catch (Exception ex) { RecordFailure(dll, ex); return; }

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IRamPlugin).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) == null) continue;

            try
            {
                var plugin = (IRamPlugin)Activator.CreateInstance(type)!;
                plugin.OnLoad(_host); // isolated below
                _plugins.Add(new LoadedPlugin
                {
                    Name = Safe(() => plugin.Name) ?? type.Name,
                    Version = Safe(() => plugin.Version) ?? "?",
                    Path = dll,
                    Instance = plugin
                });
            }
            catch (Exception ex)
            {
                _plugins.Add(new LoadedPlugin
                {
                    Name = type.Name,
                    Version = "?",
                    Path = dll,
                    Error = ex.Message
                });
            }
        }
    }

    private static void RecordFailure(string dll, Exception ex) => _plugins.Add(new LoadedPlugin
    {
        Name = System.IO.Path.GetFileNameWithoutExtension(dll),
        Version = "?",
        Path = dll,
        Error = ex.Message
    });

    /// <summary>Calls OnUnload on every loaded plugin (each isolated).</summary>
    public static void Unload()
    {
        lock (_gate)
        {
            foreach (var p in _plugins)
                if (p.Instance != null) try { p.Instance.OnUnload(); } catch { }
            _plugins.Clear();
        }
    }

    // ---- event fan-out (called by the app) ----

    public static void RaiseLaunched(Account acc, long placeId, string? jobId)
        => _host.FireLaunched(new PluginEvent(acc.UserId, acc.Username, placeId, jobId));

    public static void RaiseClosed(long userId, string username)
        => _host.FireClosed(new PluginEvent(userId, username, 0, null));

    public static void RaiseCrashed(Account acc, long placeId, string? jobId)
        => _host.FireCrashed(new PluginEvent(acc.UserId, acc.Username, placeId, jobId));

    private static T? Safe<T>(Func<T> f) { try { return f(); } catch { return default; } }

    // ---- host implementation ----

    private sealed class Host : IPluginHost
    {
        public string AppVersion => AppInfo.Number;

        public IReadOnlyList<Account> GetAccounts() => _accounts?.Invoke() ?? Array.Empty<Account>();

        public async Task<bool> LaunchAsync(long userId, long placeId = 0, string? jobId = null)
        {
            var acc = GetAccounts().FirstOrDefault(a => a.UserId == userId);
            if (acc == null) return false;
            if (placeId == 0) placeId = SettingsService.Current.DefaultPlaceId;
            var r = await LauncherService.LaunchAsync(acc, placeId, string.IsNullOrWhiteSpace(jobId) ? null : jobId);
            return r.Success;
        }

        public int Close(long userId)
        {
            int killed = 0;
            foreach (var t in ProcessRegistry.ForUser(userId).ToList())
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(t.Pid);
                    p.Kill();
                    ProcessRegistry.Forget(t.Pid);
                    killed++;
                }
                catch { }
            }
            return killed;
        }

        public void Log(string message) => System.Diagnostics.Debug.WriteLine($"[plugin] {message}");

        public event Action<PluginEvent>? AccountLaunched;
        public event Action<PluginEvent>? AccountClosed;
        public event Action<PluginEvent>? AccountCrashed;

        // Each subscriber isolated: a throwing plugin handler can't break fan-out to the others.
        public void FireLaunched(PluginEvent e) => Fan(AccountLaunched, e);
        public void FireClosed(PluginEvent e)   => Fan(AccountClosed, e);
        public void FireCrashed(PluginEvent e)  => Fan(AccountCrashed, e);

        private static void Fan(Action<PluginEvent>? evt, PluginEvent e)
        {
            if (evt == null) return;
            foreach (var d in evt.GetInvocationList().Cast<Action<PluginEvent>>())
                try { d(e); } catch { }
        }
    }
}
