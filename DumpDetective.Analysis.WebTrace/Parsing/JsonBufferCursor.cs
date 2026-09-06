using System.Text.Json;

namespace DumpDetective.Analysis.WebTrace.Parsing;

/// <summary>
/// Feeds a growing byte buffer from a <see cref="Stream"/> to a <see cref="Utf8JsonReader"/>
/// one chunk at a time so arbitrarily large JSON (Chrome traces routinely run 200MB+
/// uncompressed) never has to be buffered whole. This is the standard
/// "refill-and-resume" pattern for <see cref="Utf8JsonReader"/>: a <see cref="Utf8JsonReader"/>
/// is a ref struct that can't itself be stored across a stream read, so instead this
/// cursor owns the byte buffer + <see cref="JsonReaderState"/> and hands out a fresh
/// reader positioned at the right resume point after every refill.
///
/// The buffer only grows when a single token (e.g. a long <c>mappings</c> string) doesn't
/// fit in the current window — for the token shapes this parser actually reads, that's
/// rare. Steady-state memory is one buffer (default 64KB, capped growth), not the file.
/// </summary>
internal sealed class JsonBufferCursor
{
    private byte[] _buffer;
    private int    _length;
    private readonly Stream _stream;
    private JsonReaderState _state;
    private bool _eof;

    public JsonBufferCursor(Stream stream, int initialBufferSize = 64 * 1024)
    {
        _stream = stream;
        _buffer = new byte[initialBufferSize];
        _state  = new JsonReaderState(new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling     = JsonCommentHandling.Skip,
        });
        Fill(keepFrom: 0);
    }

    /// <summary>Creates a reader over the currently buffered bytes, resuming from <see cref="_state"/>.</summary>
    public Utf8JsonReader NewReader() => new(_buffer.AsSpan(0, _length), _eof, _state);

    /// <summary>
    /// Call after a <see cref="Utf8JsonReader"/> operation returns "need more data"
    /// (<c>Read()</c>/<c>TrySkip()</c> returning <see langword="false"/> while not
    /// <see cref="Utf8JsonReader.IsFinalBlock"/>). Preserves the reader's resume state,
    /// discards already-consumed bytes, and pulls in more data (growing the buffer if
    /// the unconsumed remainder already fills it).
    /// </summary>
    public void Advance(in Utf8JsonReader reader)
    {
        _state = reader.CurrentState;
        Fill(keepFrom: checked((int)reader.BytesConsumed));
    }

    private void Fill(int keepFrom)
    {
        int leftover = _length - keepFrom;

        if (leftover == _buffer.Length)
        {
            // The whole buffer was one unconsumed token — grow and try again.
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        if (leftover > 0 && keepFrom > 0)
            Array.Copy(_buffer, keepFrom, _buffer, 0, leftover);

        int total = leftover;
        while (total < _buffer.Length)
        {
            int n = _stream.Read(_buffer, total, _buffer.Length - total);
            if (n == 0) { _eof = true; break; }
            total += n;
        }
        _length = total;
    }
}
