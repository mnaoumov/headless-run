using System.Globalization;
using System.Text;

namespace HeadlessRun;

internal sealed class Options
{
    private const int MILLISECONDS_IN_SECOND = 1000;
    private const int SECONDS_IN_MINUTE = 60;
    private const int MINUTES_IN_HOUR = 60;
    private const int BackslashesInEscapedPair = 2;
    private static readonly HashSet<string> ValueOptions = ["--stdout", "--stderr", "--cwd", "--timeout", "--env", "--priority", "--elevated-relay"];
    private static readonly Dictionary<string, uint> PriorityClasses = new(StringComparer.Ordinal)
    {
        ["idle"] = Native.IdlePriorityClass,
        ["below-normal"] = Native.BelowNormalPriorityClass,
        ["normal"] = Native.NormalPriorityClass,
        ["above-normal"] = Native.AboveNormalPriorityClass,
        ["high"] = Native.HighPriorityClass,
        ["realtime"] = Native.RealtimePriorityClass
    };
    public string? Output;
    public string? Error;
    public string? Directory;
    public bool Append;
    public bool Detach;
    public bool CreateDirectory;
    public bool WindowsPowerShell;
    public bool Elevate;
    public bool Tray;
    public string? ElevatedRelay;
    public string Arguments = "";
    public uint PriorityClass;
    public bool Help;
    public bool Version;
    public uint Timeout = uint.MaxValue;
    public string Tail = "";
    public Dictionary<string, string> Environment = new(StringComparer.OrdinalIgnoreCase);

    public void Parse(string commandLine)
    {
        var position = 0;
        string? inlineValue = null;
        ReadToken(commandLine, ref position); // launcher executable, never forwarded
        SkipSpace(commandLine, ref position);
        Arguments = commandLine[position..]; // forwarded verbatim to an elevated copy
        while (position < commandLine.Length)
        {
            SkipSpace(commandLine, ref position);
            if (position == commandLine.Length)
            {
                break;
            }

            var flag = ReadToken(commandLine, ref position);
            if (flag == "--")
            {
                SkipSpace(commandLine, ref position);
                Tail = commandLine[position..]; // Do not parse or reconstruct the child command line.
                break;
            }
            inlineValue = null;
            var equals = flag.IndexOf('=');
            if (flag.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                inlineValue = flag[(equals + 1)..];
                flag = flag[..equals];
                if (!ValueOptions.Contains(flag))
                {
                    throw new ArgumentException($"Option '{flag}' does not accept '=VALUE'. Use --help.");
                }
            }
            switch (flag)
            {
                case "--stdout":
                    Output = ReadValue(flag);
                    break;
                case "--stderr":
                    Error = ReadValue(flag);
                    break;
                case "--cwd":
                    Directory = ReadValue(flag);
                    break;
                case "--append":
                    Append = true;
                    break;
                case "--overwrite":
                    Append = false;
                    break;
                case "--detach":
                    Detach = true;
                    break;
                case "--create-cwd":
                    CreateDirectory = true;
                    break;
                case "--elevate":
                    Elevate = true;
                    break;
                case "--tray":
                    Tray = true;
                    break;
                case "--elevated-relay":
                    ElevatedRelay = ReadValue(flag);
                    break;
                case "--windows-powershell":
                    WindowsPowerShell = true;
                    break;
                case "--priority":
                    var priority = ReadValue(flag);
                    if (!PriorityClasses.TryGetValue(priority, out PriorityClass))
                    {
                        throw new ArgumentException($"--priority requires one of {string.Join(", ", PriorityClasses.Keys)}; got '{priority}'.");
                    }

                    break;
                case "--help":
                    Help = true;
                    break;
                case "--version":
                    Version = true;
                    break;
                case "--timeout":
                    Timeout = ParseDuration(ReadValue(flag));
                    break;
                case "--env":
                    var entry = ReadValue(flag);
                    var separator = entry.IndexOf('=');
                    if (separator <= 0)
                    {
                        throw new ArgumentException("--env requires NAME=VALUE with a nonempty name.");
                    }

                    Environment[entry[..separator]] = entry[(separator + 1)..];
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{flag}'. Use --help.");
            }

        }
        if (Help || Version)
        {
            return;
        }

        if (Tail.Length == 0)
        {
            throw new ArgumentException("Expected -- followed by a program and its arguments.");
        }

        if (Detach && Timeout != uint.MaxValue)
        {
            throw new ArgumentException("--detach cannot be combined with --timeout.");
        }

        if (Detach && Tray)
        {
            throw new ArgumentException("--detach cannot be combined with --tray: a detached launcher exits at once, so nothing is left to show the job.");
        }

        string ReadValue(string flag)
        {
            if (inlineValue is not null)
            {
                if (inlineValue.Length == 0 && flag != "--env")
                {
                    throw new ArgumentException($"Empty value for {flag}.");
                }

                return inlineValue;
            }

            SkipSpace(commandLine, ref position);
            if (position == commandLine.Length)
            {
                throw new ArgumentException($"Missing value for {flag}.");
            }

            var value = ReadToken(commandLine, ref position);
            if (value.Length == 0 && flag != "--env")
            {
                throw new ArgumentException($"Empty value for {flag}.");
            }

            return value;
        }
    }

    private static uint ParseDuration(string value)
    {
        var suffix = value.EndsWith("ms", StringComparison.Ordinal) ? "ms" : value.Length > 0 ? value[^1..] : "";
        double multiplier = suffix switch
        {
            "ms" => 1,
            "s" => MILLISECONDS_IN_SECOND,
            "m" => SECONDS_IN_MINUTE * MILLISECONDS_IN_SECOND,
            "h" => MINUTES_IN_HOUR * SECONDS_IN_MINUTE * MILLISECONDS_IN_SECOND,
            _ => 0
        };
        if (multiplier == 0 || !double.TryParse(value[..^suffix.Length], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var amount) || !double.IsFinite(amount) || amount <= 0 ||
            amount * multiplier > uint.MaxValue - 1)
        {
            throw new ArgumentException("--timeout requires a positive duration such as 250ms, 30s, 2m or 1h (at most 4294967294ms).");
        }

        return (uint) Math.Ceiling(amount * multiplier);
    }

    private static void SkipSpace(string text, ref int position)
    {
        while (position < text.Length && text[position] is ' ' or '\t')
        {
            position++;
        }
    }

    // Windows quote/backslash rules, used ONLY for our own option prefix.
    private static string ReadToken(string text, ref int position)
    {
        SkipSpace(text, ref position);
        StringBuilder value = new();
        var quoted = false;
        while (position < text.Length && (quoted || text[position] is not (' ' or '\t')))
        {
            var slashes = 0;
            while (position < text.Length && text[position] == '\\')
            {
                slashes++;
                position++;
            }
            if (position < text.Length && text[position] == '"')
            {
                value.Append('\\', slashes / BackslashesInEscapedPair);
                if ((slashes & 1) != 0)
                {
                    value.Append('"');
                }
                else if (quoted && position + 1 < text.Length && text[position + 1] == '"')
                {
                    value.Append('"');
                    position++;
                }
                else
                {
                    quoted = !quoted;
                }

                position++;
            }
            else
            {
                value.Append('\\', slashes);
                if (position < text.Length && (quoted || text[position] is not (' ' or '\t')))
                {
                    value.Append(text[position++]);
                }
            }
        }
        if (quoted)
        {
            throw new ArgumentException("Unclosed quote in launcher options.");
        }

        return value.ToString();
    }
}
