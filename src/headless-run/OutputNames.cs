using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace HeadlessRun;

// Placeholders in --stdout and --stderr paths, so a job can write one file per run.
// The idea is hide-win32-console-window's per-run log name (program and start time); no code is copied.
// '<' and '>' are reserved in Windows file names, so a placeholder can never collide with a real path
// and no escape is needed: a '<' or '>' that does not open a known placeholder is refused.
internal static class OutputNames
{
    private const char Open = '<';
    private const char Close = '>';
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH-mm-ss";
    private static readonly string[] Names = ["date", "time", "program", "pid"];

    // Under --elevate the elevated copy uses the caller's start time and pid, so both sides name the same files.
    internal static void Expand(Options options)
    {
        if (!HasMarker(options.Output) && !HasMarker(options.Error))
        {
            return;
        }

        var (processId, started) = Launcher(options);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"] = started.ToString(DateFormat, CultureInfo.InvariantCulture),
            ["time"] = started.ToString(TimeFormat, CultureInfo.InvariantCulture),
            ["program"] = Path.GetFileNameWithoutExtension(Program.ProgramToken(options.Tail)),
            ["pid"] = processId.ToString(CultureInfo.InvariantCulture)
        };
        options.Output = Expand(options.Output, "--stdout", values);
        options.Error = Expand(options.Error, "--stderr", values);
    }

    private static bool HasMarker(string? path)
    {
        return path is not null && path.AsSpan().IndexOfAny(Open, Close) >= 0;
    }

    private static string? Expand(string? path, string option, Dictionary<string, string> values)
    {
        if (!HasMarker(path))
        {
            return path;
        }

        StringBuilder result = new();
        var position = 0;
        while (position < path!.Length)
        {
            var character = path[position];
            if (character == Close)
            {
                throw Refused(path, option, $"a '{Close}' with no '{Open}' before it");
            }

            if (character != Open)
            {
                result.Append(character);
                position++;
                continue;
            }

            var end = path.IndexOf(Close, position + 1);
            var name = end < 0 ? null : path[(position + 1)..end];
            if (name is null || !values.TryGetValue(name, out var value))
            {
                throw Refused(path, option, name is null ? $"a '{Open}' with no closing '{Close}'" : $"unknown placeholder '{Open}{name}{Close}'");
            }

            if (value.Length == 0)
            {
                throw Refused(path, option, $"'{Open}{name}{Close}' is empty here");
            }

            result.Append(value);
            position = end + 1;
        }

        return result.ToString();
    }

    private static ArgumentException Refused(string path, string option, string problem)
    {
        var known = string.Join(", ", Names.Select(name => Open + name + Close));
        return new ArgumentException($"{option} '{path}' has {problem}. Placeholders are {known}; '{Open}' and '{Close}' cannot appear in a file name otherwise.");
    }

    private static (int ProcessId, DateTime Started) Launcher(Options options)
    {
        var processId = options.ElevatedRelay is null ? Environment.ProcessId : (int) Elevation.CallerId(options);
        using var process = Process.GetProcessById(processId);
        return (processId, process.StartTime);
    }
}
