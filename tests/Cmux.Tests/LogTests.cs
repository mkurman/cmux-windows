using Cmux.Core.Logging;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class LogTests
{
    [Theory]
    [InlineData(LogLevel.Debug, true, LogLevel.Debug, true)]
    [InlineData(LogLevel.Info, true, LogLevel.Debug, true)]
    [InlineData(LogLevel.Debug, true, LogLevel.Info, false)]
    [InlineData(LogLevel.Info, true, LogLevel.Info, true)]
    [InlineData(LogLevel.Error, true, LogLevel.Info, true)]
    [InlineData(LogLevel.Error, false, LogLevel.Debug, false)]
    [InlineData(LogLevel.Info, false, LogLevel.Info, false)]
    public void ShouldEmit_GatesByEnabledAndMinLevel(LogLevel level, bool enabled, LogLevel minLevel, bool expected)
    {
        Log.ShouldEmit(level, enabled, minLevel).Should().Be(expected);
    }

    [Fact]
    public void Emit_WhenDisabled_DoesNothing()
    {
        Log.Clear();
        Log.Enabled = false;
        Log.MinLevel = LogLevel.Debug;

        var hits = 0;
        Action<LogEntry> sink = _ => hits++;
        Log.FileSink = sink;

        Log.Info("test", "hello");

        hits.Should().Be(0);
        Log.Count.Should().Be(0);

        Log.FileSink = null;
        Log.Enabled = true;
    }

    [Fact]
    public void Emit_WhenBelowMinLevel_DoesNothing()
    {
        Log.Clear();
        Log.Enabled = true;
        Log.MinLevel = LogLevel.Warn;

        Log.Info("test", "ignored");

        Log.Count.Should().Be(0);

        Log.MinLevel = LogLevel.Info;
    }

    [Fact]
    public void Emit_FillsRingBufferAndRaisesEvent()
    {
        Log.Clear();
        Log.Enabled = true;
        Log.MinLevel = LogLevel.Debug;

        LogEntry? captured = null;
        Action<LogEntry> handler = e => captured = e;
        Log.EntryAdded += handler;

        try
        {
            Log.Warn("MoveTo", "boom");

            Log.Count.Should().Be(1);
            captured.Should().NotBeNull();
            captured!.Level.Should().Be(LogLevel.Warn);
            captured.Category.Should().Be("MoveTo");
            captured.Message.Should().Be("boom");
            Log.Snapshot()[^1].Should().BeEquivalentTo(captured);
        }
        finally
        {
            Log.EntryAdded -= handler;
            Log.MinLevel = LogLevel.Info;
        }
    }

    [Fact]
    public void Emit_RingBufferEvictsOldestPastCapacity()
    {
        Log.Clear();
        Log.Enabled = true;
        Log.MinLevel = LogLevel.Debug;

        // Fill past capacity (5000 default) — use a smaller assertion: just verify
        // the count never exceeds capacity and oldest entries are dropped.
        for (int i = 0; i < 5050; i++)
            Log.Info("bulk", $"msg-{i}");

        Log.Count.Should().BeLessThanOrEqualTo(5000);
        var snap = Log.Snapshot();
        snap.Count.Should().BeLessThanOrEqualTo(5000);
        snap[^1].Message.Should().Be("msg-5049");
        // Oldest entries have been evicted.
        snap.Should().NotContain(e => e.Message == "msg-0");

        Log.Clear();
    }

    [Fact]
    public void FileSinkException_DoesNotCrashEmit()
    {
        Log.Clear();
        Log.Enabled = true;
        Log.MinLevel = LogLevel.Debug;
        Log.FileSink = _ => throw new InvalidOperationException("disk dead");

        var act = () => Log.Info("crash-test", "still survives");

        act.Should().NotThrow();
        Log.Count.Should().Be(1);

        Log.FileSink = null;
    }
}
