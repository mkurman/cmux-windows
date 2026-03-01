using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cmux.Core.IPC;
using static Cmux.Core.IPC.DaemonClient;

namespace Cmux.Daemon;

public sealed class DaemonPipeServer
{
    private const string PipeName = "cmux-daemon";
    private readonly DaemonSessionManager _sessionManager;
    // Each client gets a channel for thread-safe event delivery
    private readonly ConcurrentDictionary<string, Channel<string>> _clientChannels = new();
    private int _connectedClients;

    public int ConnectedClients => _connectedClients;

    public event Action? ClientConnected;
    public event Action? ClientDisconnected;

    public DaemonPipeServer(DaemonSessionManager sessionManager)
    {
        _sessionManager = sessionManager;

        _sessionManager.RawOutput += (paneId, data) =>
            BroadcastEvent(new DaemonEvent
            {
                Type = DaemonMessageTypes.EventOutput,
                PaneId = paneId,
                Data = Convert.ToBase64String(data),
            });

        _sessionManager.SessionExited += (paneId, exitCode) =>
            BroadcastEvent(new DaemonEvent { Type = DaemonMessageTypes.EventExited, PaneId = paneId, Data = exitCode.ToString() });

        _sessionManager.TitleChanged += (paneId, title) =>
            BroadcastEvent(new DaemonEvent { Type = DaemonMessageTypes.EventTitleChanged, PaneId = paneId, Data = title });

        _sessionManager.CwdChanged += (paneId, dir) =>
            BroadcastEvent(new DaemonEvent { Type = DaemonMessageTypes.EventCwdChanged, PaneId = paneId, Data = dir });

        _sessionManager.BellReceived += paneId =>
            BroadcastEvent(new DaemonEvent { Type = DaemonMessageTypes.EventBell, PaneId = paneId });
    }

    public void Run(CancellationToken ct)
    {
        LogDaemon($"[PipeServer] Listening on \\\\.\\pipe\\{PipeName}");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Use PipeOptions.Asynchronous (overlapped I/O) so reads and writes
                // can proceed concurrently. With PipeOptions.None Windows serializes
                // all I/O on the same handle, causing deadlocks when the reader thread
                // blocks the writer thread (or vice-versa).
                var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                LogDaemon("[PipeServer] Waiting for client connection...");
                pipe.WaitForConnection();
                LogDaemon("[PipeServer] Client connected, spawning handler...");

                var thread = new Thread(() => HandleConnection(pipe, ct))
                {
                    IsBackground = true,
                    Name = $"PipeServer-Client",
                };
                thread.Start();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                LogDaemon($"[PipeServer] Pipe error: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var clientId = Guid.NewGuid().ToString("N")[..8];
        // Single channel for ALL writes (events + responses) — guarantees serial writes to pipe
        var writeChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        Interlocked.Increment(ref _connectedClients);
        _clientChannels[clientId] = writeChannel;
        ClientConnected?.Invoke();
        LogDaemon($"[PipeServer] Client {clientId} connected (total: {_connectedClients}).");

        try
        {
            using (pipe)
            {
                // Writer thread: drains channel and writes raw bytes to pipe.
                var writerThread = new Thread(() =>
                {
                    try
                    {
                        foreach (var json in writeChannel.Reader.ReadAllAsync(ct).ToBlockingEnumerable(ct))
                        {
                            var bytes = Encoding.UTF8.GetBytes(json + "\n");
                            pipe.Write(bytes, 0, bytes.Length);
                        }
                    }
                    catch (IOException) { }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                })
                {
                    IsBackground = true,
                    Name = $"PipeServer-Writer-{clientId}",
                };
                writerThread.Start();

                LogDaemon($"[PipeServer:{clientId}] Entering read loop (IsConnected={pipe.IsConnected})...");

                // Read raw bytes from pipe and parse lines manually.
                // Bypasses StreamReader which has issues reading from named pipes.
                var readBuffer = new byte[65536];
                var lineBuffer = new StringBuilder();

                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = pipe.Read(readBuffer, 0, readBuffer.Length);
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        LogDaemon($"[PipeServer:{clientId}] Read returned 0 (EOF)");
                        break;
                    }

                    lineBuffer.Append(Encoding.UTF8.GetString(readBuffer, 0, bytesRead));

                    // Extract complete lines
                    var accumulated = lineBuffer.ToString();
                    int newlineIndex;
                    while ((newlineIndex = accumulated.IndexOf('\n')) >= 0)
                    {
                        var line = accumulated[..newlineIndex];
                        accumulated = accumulated[(newlineIndex + 1)..];

                        if (line.Length > 0)
                        {
                            var response = ProcessRequest(line);
                            writeChannel.Writer.TryWrite(response);
                        }
                    }
                    lineBuffer.Clear();
                    lineBuffer.Append(accumulated);
                }

                writeChannel.Writer.TryComplete();
                writerThread.Join(TimeSpan.FromSeconds(5));
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        finally
        {
            writeChannel.Writer.TryComplete();
            _clientChannels.TryRemove(clientId, out _);
            Interlocked.Decrement(ref _connectedClients);
            ClientDisconnected?.Invoke();
            LogDaemon($"[PipeServer] Client {clientId} disconnected (remaining: {_connectedClients}, sessions: {_sessionManager.ActiveSessionCount}).");
        }
    }

    private string ProcessRequest(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<DaemonRequest>(requestJson);
            if (request == null)
                return JsonSerializer.Serialize(new DaemonResponse { Success = false, Error = "Invalid request" });

            // Log non-write requests (writes are too frequent)
            if (request.Type != DaemonMessageTypes.SessionWrite)
                LogDaemon($"[PipeServer] Request: {request.Type} pane={request.PaneId}");

            return request.Type switch
            {
                DaemonMessageTypes.SessionCreate => HandleSessionCreate(request),
                DaemonMessageTypes.SessionWrite => HandleSessionWrite(request),
                DaemonMessageTypes.SessionResize => HandleSessionResize(request),
                DaemonMessageTypes.SessionClose => HandleSessionClose(request),
                DaemonMessageTypes.SessionList => HandleSessionList(),
                DaemonMessageTypes.SessionSnapshot => HandleSessionSnapshot(request),
                DaemonMessageTypes.Ping => JsonSerializer.Serialize(new DaemonResponse { Success = true, Data = "pong" }),
                _ => JsonSerializer.Serialize(new DaemonResponse { Success = false, Error = $"Unknown command: {request.Type}" }),
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new DaemonResponse { Success = false, Error = ex.Message });
        }
    }

    private string HandleSessionCreate(DaemonRequest request)
    {
        var paneId = request.PaneId ?? throw new ArgumentException("PaneId required");

        var info = _sessionManager.CreateSession(
            paneId,
            request.Cols ?? 120,
            request.Rows ?? 30,
            request.WorkingDirectory,
            request.Command);

        return JsonSerializer.Serialize(new DaemonResponse { Success = true, Data = JsonSerializer.Serialize(info) });
    }

    private string HandleSessionWrite(DaemonRequest request)
    {
        if (request.PaneId == null) throw new ArgumentException("PaneId required");
        var data = request.Data != null ? Convert.FromBase64String(request.Data) : [];
        _sessionManager.WriteToSession(request.PaneId, data);
        return JsonSerializer.Serialize(new DaemonResponse { Success = true });
    }

    private string HandleSessionResize(DaemonRequest request)
    {
        if (request.PaneId == null) throw new ArgumentException("PaneId required");
        _sessionManager.ResizeSession(request.PaneId, request.Cols ?? 120, request.Rows ?? 30);
        return JsonSerializer.Serialize(new DaemonResponse { Success = true });
    }

    private string HandleSessionClose(DaemonRequest request)
    {
        if (request.PaneId == null) throw new ArgumentException("PaneId required");
        _sessionManager.CloseSession(request.PaneId);
        return JsonSerializer.Serialize(new DaemonResponse { Success = true });
    }

    private string HandleSessionList()
    {
        var sessions = _sessionManager.ListSessions();
        return JsonSerializer.Serialize(new DaemonResponse { Success = true, Data = JsonSerializer.Serialize(sessions) });
    }

    private string HandleSessionSnapshot(DaemonRequest request)
    {
        if (request.PaneId == null) throw new ArgumentException("PaneId required");
        var snapshot = _sessionManager.GetSnapshot(request.PaneId);
        return JsonSerializer.Serialize(new DaemonResponse { Success = true, Data = snapshot });
    }

    private void BroadcastEvent(DaemonEvent evt)
    {
        var json = JsonSerializer.Serialize(evt);
        foreach (var kvp in _clientChannels)
        {
            kvp.Value.Writer.TryWrite(json);
        }
    }
}
