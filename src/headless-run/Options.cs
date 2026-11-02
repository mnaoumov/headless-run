using System.Globalization;
using System.Text;

namespace HeadlessRun;

internal sealed class Options
{
    public string? Output;
    public string? Error;
    public string? Directory;
    public bool Append;
    public bool Detach;
    public bool BelowNormal;
    public bool Help;
    public bool Version;
    public uint Timeout = uint.MaxValue;
    public string Tail = "";
    public Dictionary<string, string> Environment = new(StringComparer.OrdinalIgnoreCase);

    public void Parse(string commandLine)
    {
        int position = 0;
        ReadToken(commandLine, ref position); // launcher executable, never forwarded
        while (position < commandLine.Length)
        {
            SkipSpace(commandLine, ref position);
            if (position == commandLine.Length) break;
            string flag = ReadToken(commandLine, ref position);
            if (flag == "--")
            {
                SkipSpace(commandLine, ref position);
                Tail = commandLine[position..]; // Do not parse or reconstruct the child command line.
                break;
            }
            switch (flag)
            {
                case "--stdout": Output = Value(); break;
                case "--stderr": Error = Value(); break;
                case "--cwd": Directory = Value(); break;
                case "--append": Append = true; break;
                case "--overwrite": Append = false; break;
                case "--detach": Detach = true; break;
                case "--below-normal": BelowNormal = true; break;
                case "--help": Help = true; break;
                case "--version": Version = true; break;
                case "--timeout": Timeout = ParseDuration(Value()); break;
                case "--env":
                    string entry = Value();
                    int equals = entry.IndexOf('=');
                    if (equals <= 0) throw new ArgumentException("--env requires NAME=VALUE with a nonempty name.");
                    Environment[entry[..equals]] = entry[(equals + 1)..];
                    break;
                default: throw new ArgumentException($"Unknown option '{flag}'. Use --help.");
            }

            string Value()
            {
                SkipSpace(commandLine, ref position);
                if (position == commandLine.Length) throw new ArgumentException($"Missing value for {flag}.");
                string value = ReadToken(commandLine, ref position);
                if (value.Length == 0 && flag != "--env") throw new ArgumentException($"Empty value for {flag}.");
                return value;
            }
        }
        if (Help || Version) return;
        if (Tail.Length == 0) throw new ArgumentException("Expected -- followed by a program and its arguments.");
        if (Detach && Timeout != uint.MaxValue) throw new ArgumentException("--detach cannot be combined with --timeout.");
    }

    private static uint ParseDuration(string value)
    {
        string suffix = value.EndsWith("ms", StringComparison.Ordinal) ? "ms" : value.Length > 0 ? value[^1..] : "";
        double multiplier = suffix switch { "ms" => 1, "s" => 1000, "m" => 60000, "h" => 3600000, _ => 0 };
        if (multiplier == 0 || !double.TryParse(value[..^suffix.Length], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out double amount) || !double.IsFinite(amount) || amount <= 0 ||
            amount * multiplier > uint.MaxValue - 1)
            throw new ArgumentException("--timeout requires a positive duration such as 250ms, 30s, 2m or 1h (at most 4294967294ms).");
        return (uint)Math.Ceiling(amount * multiplier);
    }

    private static void SkipSpace(string text, ref int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t') position++;
    }

    // Windows quote/backslash rules, used ONLY for our own option prefix.
    private static string ReadToken(string text, ref int position)
    {
        SkipSpace(text, ref position);
        StringBuilder value = new();
        bool quoted = false;
        while (position < text.Length && (quoted || text[position] is not (' ' or '\t')))
        {
            int slashes = 0;
            while (position < text.Length && text[position] == '\\') { slashes++; position++; }
            if (position < text.Length && text[position] == '"')
            {
                value.Append('\\', slashes / 2);
                if ((slashes & 1) != 0) value.Append('"');
                else if (quoted && position + 1 < text.Length && text[position + 1] == '"')
                { value.Append('"'); position++; }
                else quoted = !quoted;
                position++;
            }
            else
            {
                value.Append('\\', slashes);
                if (position < text.Length && (quoted || text[position] is not (' ' or '\t'))) value.Append(text[position++]);
            }
        }
        if (quoted) throw new ArgumentException("Unclosed quote in launcher options.");
        return value.ToString();
    }
}
