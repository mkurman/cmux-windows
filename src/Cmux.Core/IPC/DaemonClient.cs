using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Cmux.Core.IPC;

public sealed class DaemonClient : IDisposable
{
    private const string PipeName = "cmux-daemon";
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private volatile bool _connected; // set after pipe + reader are ready
    private CancellationTokenSource? _listenCts;
    private volatile bool _disposed;

    // Synchronization: only one request at a time, listen loop feeds response back via TCS
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private TaskCompletionSource<DaemonResponse?>? _pendingResponse;

    public bool IsConnected => _pipe?.IsConnected == true && _connected;

    public event Action<string, byte[]>? RawOutputReceived;  // paneId, VT bytes
    public event Action<string, int>? SessionExited;          // paneId, exitCode
    public event Action<string, string>? TitleChanged;        // paneId, title
    public event Action<string, string>? CwdChanged;          // paneId, directory
    public event Action<string>? BellReceived;                // paneId
    public event Action? Connected;
    public event Action? Disconnected;

    /// <summary>
    /// Tries to connect to the daemon pipe synchronously.
    /// Must be called from a background thread (not the UI thread).
    /// Returns true if connected.
    /// </summary>
    public bool TryConnect(int timeoutMs = 300)
    {
        if (IsConnected) return true;

        try
        {
            // Use PipeOptions.Asynchronous (overlapped I/O) so that reads and writes
            // can proceed concurrently on the same handle. With PipeOptions.None, Windows
            // serializes all I/O on non-overlapped handles — the listen loop's blocking
            // Read prevents WriteToPipe from completing, causing a deadlock.
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            LogDaemon($"[Connect] Calling pipe.Connect({timeoutMs})...");
            pipe.Connect(timeoutMs);
            LogDaemon($"[Connect] pipe.Connect returned OK, IsConnected={pipe.IsConnected}");

            _pipe = pipe;
            _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            _connected = true;
            StartListening();
            LogDaemon("[Connect] Pipe connected, reader ready.");
            return true;
        }
        catch (TimeoutException)
        {
            LogDaemon($"[Connect] Timeout after {timeoutMs}ms");
            return false;
        }
        catch (Exception ex)
        {
            LogDaemon($"[Connect] Error: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Fires the Connected event. Call after TryConnect succeeds.</summary>
    public void RaiseConnected() => Connected?.Invoke();

    /// <summary>
    /// Starts the daemon process and waits for it to become available.
    /// Must be called from a background thread.
    /// </summary>
    public bool StartDaemonAndConnect(int maxRetries = 20, int retryDelayMs = 500)
    {
        try
        {
            var exePath = FindDaemonExecutable();
            LogDaemon($"[TryStart] FindDaemonExecutable: {exePath ?? "(null)"}");
            if (exePath == null) return false;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            var proc = Process.Start(psi);
            LogDaemon($"[TryStart] Process.Start: pid={proc?.Id}, exited={proc?.HasExited}");

            if (proc == null)
            {
                LogDaemon("[TryStart] Process.Start returned null");
                return false;
            }

            // Retry connecting until daemon is ready
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                // Try connecting first, then sleep (daemon might be ready already)
                if (TryConnect(1000))
                {
                    LogDaemon($"[TryStart] Connected on attempt {attempt + 1}");
                    return true;
                }

                // Check if daemon process crashed
                if (proc.HasExited)
                {
                    LogDaemon($"[TryStart] Daemon process exited with code {proc.ExitCode}");
                    return false;
                }

                LogDaemon($"[TryStart] Attempt {attempt + 1}/{maxRetries} — not yet connectable, waiting {retryDelayMs}ms...");
                Thread.Sleep(retryDelayMs);
            }

            LogDaemon("[TryStart] All attempts failed");
            return false;
        }
        catch (Exception ex)
        {
            LogDaemon($"[TryStart] Exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string? FindDaemonExecutable()
    {
        var appDir = AppContext.BaseDirectory;
        LogDaemon($"[FindDaemon] AppContext.BaseDirectory: {appDir}");

        // 1. Look next to the current executable (deployed/published scenario)
        var candidate = Path.Combine(appDir, "cmux-daemon.exe");
        if (File.Exists(candidate)) return candidate;

        // 2. Look in sibling project build output (dev build scenario)
        //    appDir is e.g. .../src/Cmux/bin/Debug/net10.0-windows10.0.17763.0/
        //    daemon is at   .../src/Cmux.Daemon/bin/Debug/net10.0-windows/cmux-daemon.exe
        try
        {
            var dir = new DirectoryInfo(appDir);
            while (dir != null && !string.Equals(dir.Name, "src", StringComparison.OrdinalIgnoreCase))
                dir = dir.Parent;

            LogDaemon($"[FindDaemon] Traversed to src dir: {dir?.FullName ?? "(null)"}");

            if (dir != null)
            {
                var daemonBin = Path.Combine(dir.FullName, "Cmux.Daemon", "bin");
                if (Directory.Exists(daemonBin))
                {
                    var config = appDir.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
                    var configDir = Path.Combine(daemonBin, config);
                    if (Directory.Exists(configDir))
                    {
                        foreach (var tfmDir in Directory.GetDirectories(configDir))
                        {
                            candidate = Path.Combine(tfmDir, "cmux-daemon.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                    }

                    foreach (var exe in Directory.GetFiles(daemonBin, "cmux-daemon.exe", SearchOption.AllDirectories))
                        return exe;
                }
            }
        }
        catch
        {
            // Filesystem errors during search — not critical
        }

        return null;
    }

    private void StartListening()
    {
        _listenCts = new CancellationTokenSource();
        // Use a dedicated background thread for reading — not Task.Run —
        // because ReadLine blocks the calling thread.
        var thread = new Thread(() => ListenLoop(_listenCts.Token))
        {
            IsBackground = true,
            Name = "DaemonClient-Listen",
        };
        thread.Start();
    }

    private void ListenLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader != null)
            {
                var line = _reader.ReadLine();
                if (line == null) break;

                // Try to parse as a DaemonResponse first (if a request is pending)
                if (_pendingResponse != null)
                {
                    try
                    {
                        var response = JsonSerializer.Deserialize<DaemonResponse>(line);
                        if (response != null && _pendingResponse != null)
                        {
                            // Responses have Success property; events have Type property
                            // Check if this looks like a response (has Success field)
                            if (line.Contains("\"Success\"", StringComparison.OrdinalIgnoreCase))
                            {
                                var tcs = _pendingResponse;
                                _pendingResponse = null;
                                tcs.TrySetResult(response);
                                continue;
                            }
                        }
                    }
                    catch { }
                }

                // Try as event
                try
                {
                    var evt = JsonSerializer.Deserialize<DaemonEvent>(line);
                    if (evt != null && !string.IsNullOrEmpty(evt.Type))
                    {
                        DispatchEvent(evt);
                    }
                }
                catch { }
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _connected = false; // Prevent new requests from entering SendRequestAsync
            _pendingResponse?.TrySetResult(null);
            Disconnected?.Invoke();
        }
    }

    private void DispatchEvent(DaemonEvent evt)
    {
        switch (evt.Type)
        {
            case DaemonMessageTypes.EventOutput:
                if (evt.PaneId != null && evt.Data != null)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(evt.Data);
                        RawOutputReceived?.Invoke(evt.PaneId, bytes);
                    }
                    catch { }
                }
                break;
            case DaemonMessageTypes.EventExited:
                if (evt.PaneId != null)
                    SessionExited?.Invoke(evt.PaneId, int.TryParse(evt.Data, out var code) ? code : -1);
                break;
            case DaemonMessageTypes.EventTitleChanged:
                if (evt.PaneId != null && evt.Data != null)
                    TitleChanged?.Invoke(evt.PaneId, evt.Data);
                break;
            case DaemonMessageTypes.EventCwdChanged:
                if (evt.PaneId != null && evt.Data != null)
                    CwdChanged?.Invoke(evt.PaneId, evt.Data);
                break;
            case DaemonMessageTypes.EventBell:
                if (evt.PaneId != null)
                    BellReceived?.Invoke(evt.PaneId);
                break;
        }
    }

    /// <summary>
    /// Writes a JSON line directly to the pipe as raw bytes.
    /// This avoids StreamWriter.Flush() → FlushFileBuffers which blocks on synchronous pipes.
    /// Stream.Write() puts data in the pipe buffer immediately without blocking.
    /// </summary>
    private void WriteToPipe(string line)
    {
        if (_pipe == null) return;
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        _pipe.Write(bytes, 0, bytes.Length);
    }

    private async Task<DaemonResponse?> SendRequestAsync(DaemonRequest request)
    {
        if (!IsConnected || _pipe == null)
        {
            LogDaemon($"[SendRequest] Bail: IsConnected={IsConnected}, pipe={(_pipe != null ? "set" : "null")}");
            return null;
        }

        await _requestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<DaemonResponse?>();
            _pendingResponse = tcs;

            var json = JsonSerializer.Serialize(request);
            if (request.Type != DaemonMessageTypes.SessionWrite)
                LogDaemon($"[SendRequest] Writing: {json[..Math.Min(json.Length, 200)]}");

            try
            {
                WriteToPipe(json);
            }
            catch (IOException)
            {
                _connected = false;
                _pendingResponse = null;
                return null;
            }

            // Wait for listen loop to deliver the response (3s timeout)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            timeoutCts.Token.Register(() => tcs.TrySetResult(null));

            var response = await tcs.Task.ConfigureAwait(false);
            LogDaemon($"[SendRequest] Response: Success={response?.Success}, Error={response?.Error}, DataLen={response?.Data?.Length}");
            return response;
        }
        catch (Exception ex)
        {
            LogDaemon($"[SendRequest] Exception: {ex.GetType().Name}: {ex.Message}");
            _pendingResponse = null;
            return null;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public static void LogDaemon(string message)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "daemon-debug.log");
            // Use FileShare.ReadWrite so daemon and client can write concurrently
            // without blocking each other (File.AppendAllText uses FileShare.Read which blocks).
            using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.Write($"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch { }
    }

    public async Task<DaemonSessionInfo?> CreateSessionAsync(string paneId, int cols, int rows, string? workingDirectory = null, string? command = null)
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionCreate,
            PaneId = paneId,
            Cols = cols,
            Rows = rows,
            WorkingDirectory = workingDirectory,
            Command = command,
        });

        if (response?.Success == true && response.Data != null)
            return JsonSerializer.Deserialize<DaemonSessionInfo>(response.Data);
        return null;
    }

    public async Task WriteAsync(string paneId, byte[] data)
    {
        await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionWrite,
            PaneId = paneId,
            Data = Convert.ToBase64String(data),
        });
    }

    public async Task WriteAsync(string paneId, string text)
    {
        await WriteAsync(paneId, Encoding.UTF8.GetBytes(text));
    }

    public async Task ResizeAsync(string paneId, int cols, int rows)
    {
        await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionResize,
            PaneId = paneId,
            Cols = cols,
            Rows = rows,
        });
    }

    public async Task CloseSessionAsync(string paneId)
    {
        await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionClose,
            PaneId = paneId,
        });
    }

    public async Task<List<DaemonSessionInfo>> ListSessionsAsync()
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionList,
        });

        if (response?.Success == true && response.Data != null)
            return JsonSerializer.Deserialize<List<DaemonSessionInfo>>(response.Data) ?? [];
        return [];
    }

    public async Task<string?> GetSnapshotAsync(string paneId)
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.SessionSnapshot,
            PaneId = paneId,
        });

        return response?.Success == true ? response.Data : null;
    }

    public async Task<bool> PingAsync()
    {
        var response = await SendRequestAsync(new DaemonRequest
        {
            Type = DaemonMessageTypes.Ping,
        });
        return response?.Success == true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connected = false;

        _listenCts?.Cancel();
        _reader?.Dispose();
        _pipe?.Dispose();
        _listenCts?.Dispose();
        _requestLock.Dispose();
    }
}
