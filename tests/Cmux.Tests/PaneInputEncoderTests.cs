using Cmux.Core.Terminal;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

public class PaneInputEncoderTests
{
    [Theory]
    [InlineData("enter", "\r")]
    [InlineData("return", "\r")]
    [InlineData("ENTER", "\r")]
    [InlineData("tab", "\t")]
    [InlineData("shift-tab", "\x1b[Z")]
    [InlineData("escape", "\x1b")]
    [InlineData("esc", "\x1b")]
    [InlineData("backspace", "\x7f")]
    [InlineData("space", " ")]
    [InlineData("up", "\x1b[A")]
    [InlineData("down", "\x1b[B")]
    [InlineData("right", "\x1b[C")]
    [InlineData("left", "\x1b[D")]
    [InlineData("home", "\x1b[H")]
    [InlineData("end", "\x1b[F")]
    [InlineData("delete", "\x1b[3~")]
    [InlineData("pageup", "\x1b[5~")]
    [InlineData("pagedown", "\x1b[6~")]
    [InlineData("f1", "\x1bOP")]
    [InlineData("f5", "\x1b[15~")]
    [InlineData("f12", "\x1b[24~")]
    public void TryResolveKey_NamedKeys_ReturnsVtSequence(string name, string expected)
    {
        PaneInputEncoder.TryResolveKey(name, out var sequence).Should().BeTrue();
        sequence.Should().Be(expected);
    }

    [Theory]
    [InlineData("ctrl-a", "\x01")]
    [InlineData("ctrl-c", "\x03")]
    [InlineData("ctrl-d", "\x04")]
    [InlineData("ctrl+c", "\x03")]
    [InlineData("Ctrl-Z", "\x1a")]
    [InlineData("ctrl-space", "\0")]
    public void TryResolveKey_CtrlCombinations_ReturnsControlCode(string name, string expected)
    {
        PaneInputEncoder.TryResolveKey(name, out var sequence).Should().BeTrue();
        sequence.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("ctrl-")]
    [InlineData("ctrl-1")]
    [InlineData("ctrl-enter")]
    public void TryResolveKey_UnknownKeys_ReturnsFalse(string name)
    {
        PaneInputEncoder.TryResolveKey(name, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("line1\r\nline2", "line1\rline2")]
    [InlineData("line1\nline2", "line1\rline2")]
    [InlineData("a\r\nb\nc\r", "a\rb\rc\r")]
    [InlineData("no newlines", "no newlines")]
    [InlineData("", "")]
    public void NormalizeNewlines_ConvertsToCarriageReturn(string input, string expected)
    {
        PaneInputEncoder.NormalizeNewlines(input).Should().Be(expected);
    }

    [Fact]
    public void WrapBracketedPaste_WrapsPayloadInMarkers()
    {
        PaneInputEncoder.WrapBracketedPaste("hello\rworld")
            .Should().Be("\x1b[200~hello\rworld\x1b[201~");
    }

    [Fact]
    public void Base64Payload_RoundTrips()
    {
        var payload = "git status\r\nline two with \"quotes\" and spaces — ünïcödé 🚀";
        var encoded = PaneInputEncoder.EncodeBase64Payload(payload);

        encoded.Should().NotContain(" ").And.NotContain("\n").And.NotContain("\"");
        PaneInputEncoder.TryDecodeBase64Payload(encoded, out var decoded).Should().BeTrue();
        decoded.Should().Be(payload);
    }

    [Fact]
    public void TryDecodeBase64Payload_EmptyString_ReturnsEmptyPayload()
    {
        PaneInputEncoder.TryDecodeBase64Payload("", out var text).Should().BeTrue();
        text.Should().Be("");
    }

    [Fact]
    public void TryDecodeBase64Payload_InvalidBase64_ReturnsFalse()
    {
        PaneInputEncoder.TryDecodeBase64Payload("not base64!!", out _).Should().BeFalse();
    }
}
