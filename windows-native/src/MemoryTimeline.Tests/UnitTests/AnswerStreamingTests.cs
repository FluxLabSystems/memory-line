using FluentAssertions;
using MemoryTimeline.Core.Services;
using Xunit;

namespace MemoryTimeline.Tests.UnitTests;

/// <summary>
/// The two helpers that turn a raw streamed grounded answer into display text
/// as it arrives. Both are exercised at EVERY chunk boundary, because a
/// token stream can split a JSON escape or a citation marker anywhere, and
/// the rewriter is checked against the batch post-processing it must match.
/// </summary>
public class AnswerStreamingTests
{
    #region JsonAnswerFieldStreamer

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1000)]
    public void JsonAnswerFieldStreamer_AnyChunking_DecodesTheAnswerFieldOnly(int chunkSize)
    {
        // Arrange: fenced JSON, the answer not the first key, and every escape
        // shape - including a surrogate pair written as two \u escapes.
        const string json =
            "```json\n{\"answeredFromArchive\":true,\"answer\":\"Line one\\nSaid \\\"hi\\\" \\\\ caf\\u00e9 \\ud83d\\ude00 [event:e1].\",\"confidence\":0.5}\n```";
        const string expected = "Line one\nSaid \"hi\" \\ café \U0001F600 [event:e1].";
        var streamer = new JsonAnswerFieldStreamer();

        // Act
        var output = string.Empty;
        for (var i = 0; i < json.Length; i += chunkSize)
        {
            output += streamer.Append(json.Substring(i, Math.Min(chunkSize, json.Length - i)));
        }

        // Assert
        output.Should().Be(expected);
        streamer.FoundAnswerField.Should().BeTrue();
        streamer.Completed.Should().BeTrue();
    }

    [Fact]
    public void JsonAnswerFieldStreamer_PlainText_YieldsNothingAndReportsNoField()
    {
        // Arrange: the model ignored the JSON instruction.
        var streamer = new JsonAnswerFieldStreamer();

        // Act
        var output = streamer.Append("Your archive doesn't ") + streamer.Append("mention that.");

        // Assert: the caller falls back to showing the whole text at the end.
        output.Should().BeEmpty();
        streamer.FoundAnswerField.Should().BeFalse();
        streamer.Completed.Should().BeFalse();
    }

    [Fact]
    public void JsonAnswerFieldStreamer_AnswerAsAValueOrNonString_IsNotMistakenForTheField()
    {
        // Arrange: "answer" appears first as a value, then as a null field,
        // then as the real string field.
        var streamer = new JsonAnswerFieldStreamer();

        // Act
        var output = streamer.Append("{\"question\":\"answer\",\"note\":{\"answer\":null},\"answer\":\"Yes\",\"after\":\"ignored\"}");

        // Assert
        output.Should().Be("Yes");
        streamer.Completed.Should().BeTrue();
    }

    #endregion

    #region CitationMarkerStreamRewriter

    [Theory]
    [InlineData("A [event:e1] b.  C [event:ghost] . D")]
    [InlineData("[event:e1] starts, then [event:e2] and [event:e1] again")]
    [InlineData("Brackets [not a marker] and [event:e1]and tight, [[event:e2]] doubled")]
    [InlineData("Unterminated [event:e1 marker at end")]
    [InlineData("Trailing space after marker [event:e1] ")]
    [InlineData("   leading spaces, comma [event:ghost], then period [event:e2].")]
    [InlineData("Line one.\n\nLine two [event:e1].\nLine three [event:ghost]\n")]
    [InlineData("No markers at all")]
    public void CitationMarkerStreamRewriter_MatchesTheBatchDisplayTextAtEverySplitPoint(string input)
    {
        // Arrange: the batch post-processing is the reference.
        var retrieved = new[] { "e1", "e2" };
        var expected = MemoryQueryService.BuildDisplayAnswer(input, retrieved, out var expectedOrder);

        for (var split = 0; split <= input.Length; split++)
        {
            var rewriter = new CitationMarkerStreamRewriter(retrieved);

            // Act
            var streamed = rewriter.Append(input[..split]) + rewriter.Append(input[split..]) + rewriter.Flush();

            // Assert
            streamed.Should().Be(expected, $"splitting at {split} must not change the display text");
            rewriter.CitationOrder.Should().Equal(expectedOrder);
        }
    }

    [Fact]
    public void CitationMarkerStreamRewriter_HoldsBackAnOpenMarkerUntilItResolves()
    {
        // Arrange
        var rewriter = new CitationMarkerStreamRewriter(new[] { "e1" });

        // Act: the marker arrives in three pieces; nothing from it is shown
        // until the closing bracket, then it appears as [1].
        var first = rewriter.Append("Met her [ev");
        var second = rewriter.Append("ent:e");
        var third = rewriter.Append("1] there.");

        // Assert
        first.Should().Be("Met her");
        second.Should().BeEmpty();
        third.Should().Be(" [1] there.");
        rewriter.Flush().Should().BeEmpty();
    }

    #endregion
}
