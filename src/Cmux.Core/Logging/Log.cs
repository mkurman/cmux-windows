using System.Collections.Concurrent;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Cmux.Core.Logging;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public sealed record LogEntry(DateTime TimestampUtc, LogLevel Level, string Category, string Message);

/// <summary>
/// Single entry point for diagnostic logging across the cmux app.
///
/// Serilog is the underlying engine; this class is a small façade so call sites
/// don't have to think about Serilog's API surface, and so the gating rules
/// (<see cref="Enabled"/>, <see cref="MinLevel"/>) stay unit-testable as pure
/// logic. Configuration (file sink, in-memory sink for the diagnostics view)
/// happens once at app startup via <see cref="Configure"/>.
/// </summary>
public static class Log
{
    private const int RingCapacity = 5000;
    private static readonly ConcurrentQueue<LogEntry> _ring = new();
    private static int _ringCount;
    private static ILogger? _serilog;

    public static bool Enabled { get; set; } = true;
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>Optional sink for unit tests / non-Serilog mirrors.</summary>
    public static Action<LogEntry>? FileSink { get; set; }

    public static event Action<LogEntry>? EntryAdded;

    /// <summary>
    /// Wires Serilog with a rolling-file sink at <paramref name="filePath"/>.
    /// Idempotent — safe to call from app startup. If you don't call it, log
    /// calls still populate the ring buffer + raise <see cref="EntryAdded"/>
    /// but won't hit disk.
    /// </summary>
    public static void Configure(string filePath)
    {
        if (_serilog != null) return;

        try
        {
            _serilog = new LoggerConfiguration()
                .MinimumLevel.Verbose() // gate happens upstream in ShouldEmit
                .WriteTo.File(
                    path: filePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}",
                    shared: true)
                .CreateLogger();
        }
        catch
        {
            _serilog = null;
        }
    }

    public static bool ShouldEmit(LogLevel level, bool enabled, LogLevel minLevel)
    {
        if (!enabled) return false;
        return (int)level >= (int)minLevel;
    }

    public static void Debug(string category, string message) => Emit(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Emit(LogLevel.Info, category, message);
    public static void Warn(string category, string message) => Emit(LogLevel.Warn, category, message);
    public static void Error(string category, string message) => Emit(LogLevel.Error, category, message);

    public static void Emit(LogLevel level, string category, string message)
    {
        if (!ShouldEmit(level, Enabled, MinLevel)) return;

        var entry = new LogEntry(DateTime.UtcNow, level, category, message);
        _ring.Enqueue(entry);
        if (Interlocked.Increment(ref _ringCount) > RingCapacity)
        {
            if (_ring.TryDequeue(out _))
                Interlocked.Decrement(ref _ringCount);
        }

        try
        {
            _serilog?.Write(ToSerilogLevel(level), "[{Category}] {Message}", category, message);
        }
        catch { /* never let logging crash the app */ }

        try { FileSink?.Invoke(entry); } catch { }
        try { EntryAdded?.Invoke(entry); } catch { }
    }

    private static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Info => LogEventLevel.Information,
        LogLevel.Warn => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };

    public static IReadOnlyList<LogEntry> Snapshot() => [.. _ring];

    public static void Clear()
    {
        while (_ring.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _ringCount, 0);
    }

    public static int Count => Volatile.Read(ref _ringCount);

    /// <summary>Flushes the underlying Serilog sink. Call on app shutdown.</summary>
    public static void Shutdown()
    {
        try { (_serilog as IDisposable)?.Dispose(); } catch { }
        _serilog = null;
    }
}
