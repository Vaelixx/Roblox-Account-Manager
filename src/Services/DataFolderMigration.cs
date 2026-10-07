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
/// </summary>
public static class DataFolderMigration
{
    // Temporary browser profiles are deleted on every start anyway; copying them is pointless.
    private static readonly string[] Skip = { "browser" };

    /// <summary>What happened, for the diagnostics log once logging is up (it can't log itself: the log lives in the data folder).</summary>
    public static string? LastResult { get; private set; }

    /// <summary>Copies <paramref name="from"/> into <paramref name="to"/> when <paramref name="to"/> has no data yet. Returns true when it copied.</summary>
    public static bool CopyIfNeeded(string from, string to)
    {
        string staging = to + ".migrating";
        try
        {
            if (!Directory.Exists(from) || !Directory.EnumerateFileSystemEntries(from).Any()) return false;
            if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) return false;   // never overwrite

            if (Directory.Exists(staging)) Directory.Delete(staging, true);   // our own leftover from an interrupted copy
            var files = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
                .Where(f => !Skip.Any(s => Path.GetRelativePath(from, f).StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            foreach (string file in files)
            {
                string dest = Path.Combine(staging, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
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
}
