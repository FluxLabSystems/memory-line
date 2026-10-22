using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryTimeline.Core.Services;

/// <summary>
/// Pulls the value of the first <c>"answer"</c> string field out of a JSON
/// object as the object's text arrives, so a grounded answer can be shown
/// while the model is still writing the structure around it. Feed the raw
/// model text to <see cref="Append"/> in any chunking; each call returns the
/// decoded answer text that became available (JSON escapes resolved, split
/// escape sequences buffered across calls). Text before the field, the other
/// fields, and anything after the closing quote produce nothing.
/// (Internal, but visible to the test project via InternalsVisibleTo.)
/// </summary>
internal sealed class JsonAnswerFieldStreamer
{
    private enum State
    {
        SeekingKey,
        SeekingColon,
        SeekingQuote,
        InString,
        Escape,
        Unicode,
        Done
    }

    private const string Key = "\"answer\"";

    private readonly StringBuilder _recent = new();
    private readonly StringBuilder _unicode = new();
    private State _state = State.SeekingKey;

    /// <summary>True once the answer field's string value has started (or finished).</summary>
    public bool FoundAnswerField => _state >= State.InString;

    /// <summary>True once the answer field's closing quote was seen.</summary>
    public bool Completed => _state == State.Done;

    /// <summary>Consumes the next piece of raw text and returns the answer text it completed.</summary>
    public string Append(string text)
    {
        var output = new StringBuilder();
        foreach (var c in text)
        {
            Process(c, output);
        }

        return output.ToString();
    }

    private void Process(char c, StringBuilder output)
    {
        switch (_state)
        {
            case State.SeekingKey:
                _recent.Append(c);
                if (_recent.Length > Key.Length)
                {
                    _recent.Remove(0, _recent.Length - Key.Length);
                }

                if (_recent.Length == Key.Length && _recent.ToString() == Key)
                {
                    _state = State.SeekingColon;
                    _recent.Clear();
                }
                break;

            case State.SeekingColon:
                if (c == ':')
                {
                    _state = State.SeekingQuote;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    // "answer" was a value, not a key; keep looking.
                    _state = State.SeekingKey;
                    _recent.Append(c);
                }
                break;

            case State.SeekingQuote:
                if (c == '"')
                {
                    _state = State.InString;
                }
                else if (!char.IsWhiteSpace(c))
                {
                    // "answer": null / a number — not a string field; keep looking.
                    _state = State.SeekingKey;
                    _recent.Append(c);
                }
                break;

            case State.InString:
                if (c == '"')
                {
                    _state = State.Done;
                }
                else if (c == '\\')
                {
                    _state = State.Escape;
                }
                else
                {
                    output.Append(c);
                }
                break;

            case State.Escape:
                _state = State.InString;
                switch (c)
                {
                    case 'n': output.Append('\n'); break;
                    case 't': output.Append('\t'); break;
                    case 'r': output.Append('\r'); break;
                    case 'b': output.Append('\b'); break;
                    case 'f': output.Append('\f'); break;
                    case 'u':
                        _state = State.Unicode;
                        _unicode.Clear();
                        break;
                    default:
                        // \" \\ \/ (and anything unknown, verbatim)
                        output.Append(c);
                        break;
                }
                break;

            case State.Unicode:
                _unicode.Append(c);
                if (_unicode.Length == 4)
                {
                    if (int.TryParse(_unicode.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    {
                        output.Append((char)code);
                    }

                    _state = State.InString;
                }
                break;

            case State.Done:
                break;
        }
    }
}

/// <summary>
/// Rewrites <c>[event:ID]</c> citation markers in streamed answer text the
/// way <see cref="MemoryQueryService"/> rewrites a finished answer: a marker
/// whose id was retrieved becomes <c>[n]</c> (numbered by first appearance),
/// any other marker is stripped, runs of spaces collapse, and a space before
/// punctuation is dropped. Because a marker can be split across chunks, text
/// from an unresolved <c>[</c> is held back until it is either a complete
/// marker or clearly not one; <see cref="Flush"/> releases whatever is held
/// at the end. Whitespace is likewise held until the next visible character,
/// which is how runs of spaces collapse, a space before punctuation drops,
/// and leading/trailing whitespace never appears — the batch Trim(). 
/// Concatenating every returned piece equals the batch display text for the
/// same input. (Internal, visible to the test project.)
/// </summary>
internal sealed class CitationMarkerStreamRewriter
{
    private const string MarkerPrefix = "[event:";
    private const int MaxHeldLength = 96;
    private static readonly Regex MarkerRegex = new(@"^\[event:([^\]\s]+)\]$", RegexOptions.Compiled);

    private readonly HashSet<string> _retrieved;
    private readonly List<string> _citationOrder = new();
    private readonly StringBuilder _held = new();
    private readonly StringBuilder _pendingWhitespace = new();
    private bool _emittedAny;

    public CitationMarkerStreamRewriter(IEnumerable<string> retrievedEventIds)
    {
        _retrieved = new HashSet<string>(retrievedEventIds, StringComparer.Ordinal);
    }

    /// <summary>Retrieved ids cited so far, in the order their [n] numbers were assigned.</summary>
    public IReadOnlyList<string> CitationOrder => _citationOrder;

    /// <summary>Consumes the next piece of answer text and returns the display text it completed.</summary>
    public string Append(string text)
    {
        var output = new StringBuilder();
        foreach (var c in text)
        {
            Process(c, output);
        }

        return output.ToString();
    }

    /// <summary>Releases any held text at the end of the stream (trailing whitespace is dropped, as the batch Trim() would).</summary>
    public string Flush()
    {
        var output = new StringBuilder();
        ReleaseHeld(output);
        _pendingWhitespace.Clear();
        return output.ToString();
    }

    private void Process(char c, StringBuilder output)
    {
        if (_held.Length > 0)
        {
            if (c == ']')
            {
                _held.Append(c);
                var candidate = _held.ToString();
                _held.Clear();

                var match = MarkerRegex.Match(candidate);
                if (match.Success)
                {
                    EmitMarker(match.Groups[1].Value, output);
                }
                else
                {
                    foreach (var held in candidate)
                    {
                        EmitChar(held, output);
                    }
                }

                return;
            }

            if (char.IsWhiteSpace(c) || _held.Length >= MaxHeldLength || !ContinuesMarker(c))
            {
                ReleaseHeld(output);
                Process(c, output); // c may itself open a new marker
                return;
            }

            _held.Append(c);
            return;
        }

        if (c == '[')
        {
            _held.Append(c);
            return;
        }

        EmitChar(c, output);
    }

    /// <summary>Whether the held text plus <paramref name="c"/> can still become a marker.</summary>
    private bool ContinuesMarker(char c)
    {
        var position = _held.Length;
        return position >= MarkerPrefix.Length || MarkerPrefix[position] == c;
    }

    private void ReleaseHeld(StringBuilder output)
    {
        if (_held.Length == 0)
        {
            return;
        }

        var held = _held.ToString();
        _held.Clear();
        foreach (var c in held)
        {
            EmitChar(c, output);
        }
    }

    private void EmitMarker(string eventId, StringBuilder output)
    {
        if (!_retrieved.Contains(eventId))
        {
            return; // fabricated / unretrieved: stripped entirely
        }

        var index = _citationOrder.IndexOf(eventId);
        if (index < 0)
        {
            _citationOrder.Add(eventId);
            index = _citationOrder.Count - 1;
        }

        // The batch replacement is " [n]": a leading space that collapses
        // with any space already pending.
        EmitChar(' ', output);
        foreach (var c in $"[{index + 1}]")
        {
            EmitChar(c, output);
        }
    }

    private void EmitChar(char c, StringBuilder output)
    {
        if (c == ' ')
        {
            // Runs of spaces collapse to one (the batch " {2,}" rule).
            if (_pendingWhitespace.Length == 0 || _pendingWhitespace[^1] != ' ')
            {
                _pendingWhitespace.Append(' ');
            }

            return;
        }

        if (char.IsWhiteSpace(c))
        {
            // Newlines and tabs are kept verbatim, but still held so that
            // trailing whitespace can be dropped at the end.
            _pendingWhitespace.Append(c);
            return;
        }

        if (IsTrailingPunctuation(c))
        {
            // Spaces directly before punctuation drop (the batch " +([.,!?;:])" rule).
            while (_pendingWhitespace.Length > 0 && _pendingWhitespace[^1] == ' ')
            {
                _pendingWhitespace.Length--;
            }
        }

        if (_pendingWhitespace.Length > 0)
        {
            if (_emittedAny)
            {
                output.Append(_pendingWhitespace); // leading whitespace is never emitted (Trim)
            }

            _pendingWhitespace.Clear();
        }

        output.Append(c);
        _emittedAny = true;
    }

    private static bool IsTrailingPunctuation(char c) => c is '.' or ',' or '!' or '?' or ';' or ':';
}
