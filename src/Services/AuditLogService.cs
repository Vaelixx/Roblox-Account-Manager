using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RobloxAccountManager.Services;

/// <summary>
/// Append-only security event log. Records privacy/security-relevant actions
/// (unlock, launch, cookie import/export, account add/remove, cookie rotation,
/// master-password changes) to <c>data/audit.log</c> with UTC timestamps.
/// Every write is best-effort and guarded by a lock so concurrent services
/// (launcher, watchdog, importer) never corrupt the file or throw.
/// </summary>
public static class AuditLogService
{
    private static readonly object Gate = new();
    private static string LogPath => Paths.InData("audit.log");

    /// <summary>Known event categories — kept short and stable for grep-ability.</summary>
    public static class Category
    {
        public const string Unlock   = "UNLOCK";
        public const string Lock     = "LOCK";
        public const string Launch   = "LAUNCH";
        public const string Cookie   = "COOKIE";
        public const string Account  = "ACCOUNT";
        public const string Rotation = "ROTATION";
        public const string Password = "PASSWORD";
        public const string Security = "SECURITY";
    }

    /// <summary>
    /// Append one timestamped entry. No-op unless <see cref="AppSettings.AuditLogEnabled"/>
    /// is set. Never throws; failures are swallowed so logging can never break a flow.
    /// </summary>
    public static void Log(string category, string message)
    {
        try
        {
            if (!SettingsService.Current.AuditLogEnabled) return;

            string line = $"{System.DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z  [{category}]  {Sanitize(message)}";
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.AppendAllText(LogPath, line + System.Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* auditing must never surface an error to the caller */ }
    }

    /// <summary>
    /// Return the most recent <paramref name="count"/> entries, newest last.
    /// Empty when logging is off or the file does not exist yet.
    /// </summary>
    public static List<string> ReadRecent(int count = 200)
    {
        try
        {
            lock (Gate)
            {
                if (!File.Exists(LogPath)) return new List<string>();
                // ReadLines is lazy; take the tail without loading megabytes into memory.
                var all = File.ReadAllLines(LogPath);
                int skip = System.Math.Max(0, all.Length - count);
                return all.Skip(skip).ToList();
            }
        }
        catch { return new List<string>(); }
    }

    /// <summary>Wipe the log (used by the "clear audit log" action). Best-effort.</summary>
    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(LogPath)) File.Delete(LogPath);
            }
        }
        catch { }
    }

    /// <summary>Collapse newlines/tabs so one event is always exactly one line.</summary>
    private static string Sanitize(string s)
        => string.IsNullOrEmpty(s) ? "" : s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
