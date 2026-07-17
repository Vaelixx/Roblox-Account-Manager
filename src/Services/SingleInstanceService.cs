using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace RobloxAccountManager.Services;

/// <summary>
/// Cross-process bridge for the single-instance app. The first instance owns the app
/// (see the mutex in <see cref="App"/>) and runs a named-pipe server; any later instance
/// started with actionable CLI args (e.g. <c>--launch</c>) forwards them here instead of
/// opening a second window. Keeps CLI launches working while the app is already open.
/// </summary>
public static class SingleInstanceService
{
    private const string PipeName = "RobloxAccountManager.Modern.Cli";

    private static CancellationTokenSource? _cts;

    /// <summary>
    /// Starts the background accept loop. <paramref name="onArgs"/> is invoked on the UI
    /// dispatcher with the argv that a secondary instance forwarded.
    /// </summary>
    public static void StartServer(Action<string[]> onArgs)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(onArgs, _cts.Token));
    }

    public static void StopServer()
    {
        try { _cts?.Cancel(); } catch { }
        _cts = null;
    }

    private static async Task AcceptLoop(Action<string[]> onArgs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool errored = false;
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, Encoding.UTF8);
                string payload = await reader.ReadToEndAsync(ct);

                var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(a => a.Trim('\r'))
                                  .Where(a => a.Length > 0)
                                  .ToArray();
                if (args.Length == 0) continue;

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    // Surface the main window so the user sees the forwarded action land.
                    if (Application.Current?.MainWindow is { } w)
                    {
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Activate();
                    }
                    onArgs(args);
                });
            }
            catch (OperationCanceledException) { break; }
            catch { errored = true; }   // transient pipe error — back off and re-arm

            if (errored)
            {
                try { await Task.Delay(200, ct); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Best-effort forward of argv to the running instance. Returns true if a primary
    /// instance accepted the payload; false if none was reachable within the timeout.
    /// </summary>
    public static bool TrySendToPrimary(string[] args, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.Write(string.Join('\n', args));
            return true;
        }
        catch { return false; }   // no primary listening / pipe busy
    }
}
