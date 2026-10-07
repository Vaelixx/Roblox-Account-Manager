using System.IO;

namespace RobloxAccountManager.Services;

/// <summary>
/// Moves the manager's data into the per-user folder the first time it has to fall back there while
/// a <c>data</c> folder still sits next to the exe — the case where that folder can't be written to
/// (an install under Program Files, a read-only copy). Without this the accounts, settings and
/// presets would seem to have vanished, although they are all still in the old folder.
///
/// Nothing is ever deleted: the old folder is left exactly as it was (the manager can't write
/// there anyway, which is why it moved), and a target that already has data is never touched.
/// The copy lands in a temporary folder first and only becomes the real one once every file has
/// been checked, so an interrupted copy can't leave a half-filled data folder behind.
///
/// The move is for good. A marker in the per-user folder keeps the manager there even when the exe
/// folder can be written to again (Controlled Folder Access allowing the app, a start as
/// administrator): going back would bring up the old copy and hide everything changed since.
/// </summary>
public static class DataFolderMigration
{
    // Temporary browser profiles are deleted on every start anyway, and the private browser (hundreds of
    // MB, downloaded again in a click) would make the first start after the move look hung.
    private static readonly string[] Skip = { "browser", "cloakbrowser", "chromium" };

    /// <summary>
    /// File in the per-user data folder listing the data folders next to an exe that moved there,
    /// one path per line.
    /// </summary>
    public const string MovedMarker = ".moved-from-exe-folder";

    /// <summary>What happened, for the diagnostics log once logging is up (it can't log itself: the log lives in the data folder).</summary>
    public static string? LastResult { get; private set; }

    /// <summary>
    /// The data folder to use: <paramref name="exeFolder"/>, the one next to the exe, when it can be
    /// written to; otherwise <paramref name="perUser"/>, with the old data brought along the first
    /// time, and from then on for good.
    /// </summary>
    public static string Choose(string exeFolder, string perUser) => Choose(exeFolder, perUser, CanWrite);

    internal static string Choose(string exeFolder, string perUser, Func<string, bool> canWrite)
    {
        string marker = Path.Combine(perUser, MovedMarker);
        if (MovedFrom(marker).Contains(exeFolder, StringComparer.OrdinalIgnoreCase)) return perUser;
        if (canWrite(exeFolder)) return exeFolder;

        bool perUserHadData = HasEntries(perUser);
        bool copied = CopyIfNeeded(exeFolder, perUser);
        if (copied || !HasFilesToCopy(exeFolder))
        {
            try
            {
                Directory.CreateDirectory(perUser);
                File.AppendAllText(marker, exeFolder + Environment.NewLine);
            }
            catch { /* without the marker the next start simply decides again */ }

            LastResult = copied
                ? LastResult + " It stays the data folder from now on, even if the old one can be written to again."
                : $"The folder next to the exe ({exeFolder}) can't be written to, so the data lives in {perUser} from now on.";
        }
        else if (perUserHadData)
        {
            // Two sets of data and nothing that says which is newer: use the one that can be written
            // to, but don't make it stick, so the data next to the exe wins again once it can.
            LastResult = $"The folder next to the exe ({exeFolder}) can't be written to, and {perUser} already had data, which is used instead. "
                       + "The data next to the exe was not copied; the manager goes back to it once that folder can be written to.";
        }
        return perUser;
    }

    /// <summary>
    /// Whether files can be created in <paramref name="dir"/> (created when missing). The write is
    /// the proof: the test file is deleted again, but a failed delete doesn't count against the
    /// folder — a virus scanner often holds a new file for a moment. A sharing violation or a busy
    /// disk gets two quick retries; access denied gets none.
    /// </summary>
    public static bool CanWrite(string dir)
    {
        // Unique, so a leftover from an earlier start can never be in the way.
        string probe = Path.Combine(dir, $".wtest-{Guid.NewGuid():N}");
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(probe, "");
                break;
            }
            catch (IOException) when (attempt < 3) { Thread.Sleep(200); }
            catch { return false; }
        }

        try { File.Delete(probe); } catch { }
        try
        {
            // Probes an earlier start could not delete; the age keeps one being written right now safe.
            DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (string stale in Directory.EnumerateFiles(dir, ".wtest*"))
                try { if (File.GetLastWriteTimeUtc(stale) < cutoff) File.Delete(stale); } catch { }
        }
        catch { }
        return true;
    }

    /// <summary>Copies <paramref name="from"/> into <paramref name="to"/> when <paramref name="to"/> has no data yet. Returns true when it copied.</summary>
    public static bool CopyIfNeeded(string from, string to)
    {
        string staging = to + ".migrating";
        try
        {
            if (!Directory.Exists(from) || !Directory.EnumerateFileSystemEntries(from).Any()) return false;
            if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return false;   // never overwrite

            if (Directory.Exists(staging)) Directory.Delete(staging, true);   // our own leftover from an interrupted copy
            var files = FilesToCopy(from).ToList();
            if (files.Count == 0) return false;   // only temporary browser profiles: nothing is missing

            foreach (string file in files)
            {
                string dest = Path.Combine(staging, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);

                // A copy keeps the read-only flag of a read-only source (an ISO, a DVD), and every
                // later save over settings.json or accounts.dat would then fail.
                var attributes = File.GetAttributes(dest);
                if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(dest, attributes & ~FileAttributes.ReadOnly);
            }

            // Every file there, with the same size, before the copy counts.
            foreach (string file in files)
            {
                var copy = new FileInfo(Path.Combine(staging, Path.GetRelativePath(from, file)));
                if (!copy.Exists || copy.Length != new FileInfo(file).Length)
                    throw new IOException($"{Path.GetRelativePath(from, file)} did not copy completely");
            }

            if (Directory.Exists(to)) Directory.Delete(to);   // empty (checked above)
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(staging, to);
            LastResult = $"Copied {files.Count} file(s) from the data folder next to the exe, which can't be written to, into {to}. The old folder was left unchanged.";
            return true;
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            LastResult = $"Could not copy the old data folder ({ex.GetType().Name}: {ex.Message}). It is unchanged; copy it by hand if accounts are missing.";
            return false;
        }
    }

    private static IEnumerable<string> FilesToCopy(string from)
        => Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
            .Where(f => !Skip.Any(s => Path.GetRelativePath(from, f).StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

    // Unreadable counts as yes: then nothing is known to be safe to leave behind.
    private static bool HasFilesToCopy(string from)
    {
        try { return Directory.Exists(from) && FilesToCopy(from).Any(); }
        catch { return true; }
    }

    private static bool HasEntries(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    private static IEnumerable<string> MovedFrom(string marker)
    {
        try { return File.Exists(marker) ? File.ReadAllLines(marker).Select(l => l.Trim()) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}
