using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Cmux.Core.IPC;

/// <summary>
/// Named pipe server for cmux CLI/API communication.
/// Windows equivalent of the Unix domain socket used by cmux on macOS.
/// Pipe name: \\.\pipe\cmux (or \\.\pipe\cmux-{tag} for tagged instances).
/// </summary>
public sealed class NamedPipeServer : IDisposable
{
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public string PipeName => _pipeName;

    /// <summary>
    /// Invoked when a command is received. Args: (command, args dictionary).
    /// Returns the response JSON string.
    /// </summary>
    public Func<string, Dictionary<string, string>, Task<string>>? OnCommand { get; set; }

    public NamedPipeServer(string? tag = null)
    {
        _pipeName = string.IsNullOrEmpty(tag) ? "cmux" : $"cmux-{tag}";
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                // Handle each connection on its own task
                _ = Task.Run(() => HandleConnection(pipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Pipe error, retry
                await Task.Delay(100, ct);
            }
        }
    }

    private async Task HandleConnection(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                var requestLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(requestLine)) return;

                // Parse: COMMAND key1=value1 key2=value2 ...
                var parts = requestLine.Split(' ', 2);
                var command = parts[0].ToUpperInvariant();
                var args = new Dictionary<string, string>();

                if (parts.Length > 1)
                {
                    ParseArgs(parts[1], args);
                }

                string response;
                if (OnCommand != null)
                {
                    response = await OnCommand(command, args);
                }
                else
                {
                    response = JsonSerializer.Serialize(new { error = "No handler registered" });
                }

                await writer.WriteLineAsync(response);
            }
        }
        catch (IOException)
        {
            // Client disconnected
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
        }
    }

    private static void ParseArgs(string argsString, Dictionary<string, string> args)
    {
        // Support both key=value and JSON formats
        var trimmed = argsString.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var json = JsonSerializer.Deserialize<Dictionary<string, string>>(trimmed);
                if (json != null)
                {
                    foreach (var kvp in json)
                        args[kvp.Key] = kvp.Value;
                    return;
                }
            }
            catch
            {
                // Fall through to key=value parsing
            }
        }

        foreach (var part in SplitRespectingQuotes(trimmed))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                var key = part[..eq];
                var value = part[(eq + 1)..].Trim('"', '\'');
                args[key] = value;
            }
            else
            {
                // Positional argument
                args.TryAdd("_arg" + args.Count, part);
            }
        }
    }

    private static IEnumerable<string> SplitRespectingQuotes(string input)
    {
        var current = new StringBuilder();
        bool inQuote = false;
        char quoteChar = '\0';

        foreach (var c in input)
        {
            if (inQuote)
            {
                if (c == quoteChar)
                {
                    inQuote = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (c == ' ')
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listenTask?.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}

/// <summary>
/// Client for connecting to the cmux named pipe server (used by the CLI).
/// </summary>
public static class NamedPipeClient
{
    public static async Task<string> SendCommand(string command, Dictionary<string, string>? args = null, string? tag = null, int timeoutMs = 5000)
    {
        var pipeName = string.IsNullOrEmpty(tag) ? "cmux" : $"cmux-{tag}";

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(timeoutMs);

        await pipe.ConnectAsync(cts.Token);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        var sb = new StringBuilder(command);
        if (args != null)
        {
            foreach (var kvp in args)
            {
                var value = kvp.Value.Contains(' ') ? $"\"{kvp.Value}\"" : kvp.Value;
                sb.Append($" {kvp.Key}={value}");
            }
        }

        await writer.WriteLineAsync(sb.ToString());

        var response = await reader.ReadLineAsync(cts.Token);
        return response ?? "";
    }
}
