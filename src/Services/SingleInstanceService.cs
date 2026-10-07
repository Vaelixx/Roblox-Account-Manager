using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace RobloxAccountManager.Services;

/// <summary>
/// Cross-process bridge for the single-instance app. The first instance owns the app
/// (see the mutex in <see cref="App"/>) and runs a named-pipe server; any later instance
/// forwards its request here instead of opening a second window: CLI args (e.g. <c>--launch</c>)
/// run in the live instance, and a plain second start brings its window back.
/// </summary>
/// <remarks>
/// A request ends with <see cref="EndOfRequest"/> and is answered with <see cref="Ack"/>, so the
/// sender knows the running copy understood it. Versions before 2.2 neither end nor answer
/// requests: their senders close the pipe after writing, and their servers only accept writes.
/// </remarks>
public static class SingleInstanceService
{
    private const string PipeName = "RobloxAccountManager.Modern.Cli";

    /// <summary>Sent by a second start without arguments: the running instance shows its window.</summary>
    public const string ShowFlag = "--show";

    private const byte EndOfRequest = 0;
    private const byte Ack = 6;

    // Bounded read: a forwarded command line is a few hundred bytes, never megabytes.
    private const int MaxRequestBytes = 8192;

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
                // CurrentUserOnly: only processes running as this Windows user can connect, so another
                // account on a shared PC cannot drive launches through the pipe.
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct);

                // A sender that connects and then says nothing must not block the next one for good.
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(5));

                var request = new List<byte>();
                var chunk = new byte[1024];
                bool ended = false;
                while (request.Count < MaxRequestBytes)
                {
                    int n = await server.ReadAsync(chunk, readTimeout.Token);
                    if (n == 0) break;   // the sender closed the pipe: an older version, it never waits for an answer
                    int end = Array.IndexOf(chunk, EndOfRequest, 0, n);
                    request.AddRange(chunk.Take(end >= 0 ? end : n));
                    if (end >= 0) { ended = true; break; }
                }

                var args = Encoding.UTF8.GetString(request.ToArray())
                                  .Split('\n', StringSplitOptions.RemoveEmptyEntries)
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

                if (ended)
                {
                    try
                    {
                        server.WriteByte(Ack);
                        server.Flush();
                        server.WaitForPipeDrain();
                    }
                    catch { /* the sender stopped waiting; the request is handled either way */ }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { errored = true; }   // transient pipe error or a silent sender — back off and re-arm

            if (errored)
            {
                try { await Task.Delay(200, ct); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Forwards argv to the running instance. Returns true once a running instance confirmed it.
    /// </summary>
    /// <param name="legacyFallback">
    /// Also hand the request to a running version older than 2.2, which takes it without confirming.
    /// Right for <c>--launch</c>, which those versions run; wrong for <see cref="ShowFlag"/>, which
    /// they ignore — the caller should then tell the user where the running copy is.
    /// </param>
    public static bool TrySendToPrimary(string[] args, bool legacyFallback, int timeoutMs = 1500)
    {
        byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            client.Write(payload);
            client.WriteByte(EndOfRequest);
            client.Flush();

            using var wait = new CancellationTokenSource(timeoutMs);
            var answer = new byte[1];
            int n = client.ReadAsync(answer, wait.Token).AsTask().GetAwaiter().GetResult();
            return n == 1 && answer[0] == Ack;
        }
        catch (UnauthorizedAccessException)
        {
            // An older running version: its pipe only takes writes, so asking for an answer is refused.
        }
        catch { return false; }   // no primary listening, busy, or no answer in time

        if (!legacyFallback) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch { return false; }
    }
}
