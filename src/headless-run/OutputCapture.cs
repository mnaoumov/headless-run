using System.Text;

namespace HeadlessRun;

// What the --tray output viewer shows: the tail of every stream, from files the child writes
// and from pipes the launcher passes through. Bounded, so a chatty job cannot grow the launcher.
internal sealed class OutputCapture
{
    private const int MaximumCharacters = 262144;
    private const int ReadBufferBytes = 4096;
    private readonly Lock gate = new();
    private readonly StringBuilder text = new();
    private readonly List<FileTail> tails = [];
    private int version;

    // Follow a file the child writes. Appended runs show only what this run adds.
    public void Follow(string path, bool append)
    {
        var start = append && File.Exists(path) ? new FileInfo(path).Length : 0;
        tails.Add(new FileTail(Path.GetFullPath(path), start, NewSource()));
    }

    // Before the launcher opens the files, so an appended file's current end is where this run starts.
    public void FollowLogs(Options options, bool sharedFile)
    {
        if (options.Output is not null)
        {
            Follow(options.Output, options.Append);
        }

        if (options.Error is not null && !sharedFile)
        {
            Follow(options.Error, options.Append);
        }
    }

    public Source NewSource()
    {
        return new Source(this);
    }

    // Read what the followed files gained since the last poll. Called from the tray thread only.
    public void Poll()
    {
        foreach (var tail in tails)
        {
            tail.Read();
        }
    }

    // A launcher diagnostic, shown in the viewer between the child's own lines.
    public void Note(string message)
    {
        Append(message);
    }

    public (int Version, string Text) Snapshot()
    {
        lock (gate)
        {
            return (version, text.ToString());
        }
    }

    // The edit control wants CRLF: drop every CR and expand LF, which is safe across chunk boundaries.
    private void Append(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return;
        }

        lock (gate)
        {
            foreach (var character in chars)
            {
                if (character == '\n')
                {
                    text.Append("\r\n");
                }
                else if (character != '\r')
                {
                    text.Append(character);
                }
            }

            if (text.Length > MaximumCharacters)
            {
                text.Remove(0, text.Length - MaximumCharacters);
            }

            version++;
        }
    }

    // One stream's decoder, so a UTF-8 sequence split across two reads still decodes.
    internal sealed class Source
    {
        private readonly OutputCapture capture;
        private readonly Decoder decoder = new UTF8Encoding(false).GetDecoder();

        public Source(OutputCapture capture)
        {
            this.capture = capture;
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            var chars = new char[decoder.GetCharCount(bytes, false)];
            var count = decoder.GetChars(bytes, chars, false);
            capture.Append(chars.AsSpan(0, count));
        }

    }

    private sealed class FileTail
    {
        private readonly string path;
        private readonly Source source;
        private long offset;

        public FileTail(string path, long offset, Source source)
        {
            this.path = path;
            this.offset = offset;
            this.source = source;
        }

        public void Read()
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < offset)
                {
                    // Truncated by another writer: start again from the top.
                    offset = 0;
                }

                stream.Seek(offset, SeekOrigin.Begin);
                var buffer = new byte[ReadBufferBytes];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    offset += read;
                    source.Write(buffer.AsSpan(0, read));
                }
            }
            catch (IOException)
            {
                // Not created yet, or locked for a moment: the next poll tries again.
            }
            catch (UnauthorizedAccessException)
            {
                // The same: the viewer shows whatever it can read.
            }
        }
    }
}
