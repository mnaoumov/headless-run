using System.Globalization;

namespace HeadlessRun;

// --max-size and output.maxSize: before an --append run, an output file over the cap moves to <file>.1.
// The idea is SilentCMD's /LOGSIZE, which truncates instead; one backup keeps the run that crossed the cap.
internal static class OutputRotation
{
    internal const string Disabled = "none";
    internal const string BackupSuffix = ".1";
    private const long BYTES_IN_KIBIBYTE = 1024;
    private const long KIBIBYTES_IN_MEBIBYTE = 1024;
    private const long MEBIBYTES_IN_GIBIBYTE = 1024;
    private const long BYTES_IN_MEBIBYTE = KIBIBYTES_IN_MEBIBYTE * BYTES_IN_KIBIBYTE;
    private const long BYTES_IN_GIBIBYTE = MEBIBYTES_IN_GIBIBYTE * BYTES_IN_MEBIBYTE;
    // Longest suffix first, so "KiB" is not read as a number ending in "B".
    private static readonly (string Suffix, long Multiplier)[] Units =
    [
        ("GiB", BYTES_IN_GIBIBYTE),
        ("MiB", BYTES_IN_MEBIBYTE),
        ("KiB", BYTES_IN_KIBIBYTE),
        ("GB", BYTES_IN_GIBIBYTE),
        ("MB", BYTES_IN_MEBIBYTE),
        ("KB", BYTES_IN_KIBIBYTE),
        ("G", BYTES_IN_GIBIBYTE),
        ("M", BYTES_IN_MEBIBYTE),
        ("K", BYTES_IN_KIBIBYTE),
        ("B", 1)
    ];

    // The cap in bytes, or null for none. source names the option or config key in a message.
    internal static long? ParseSize(string value, string source)
    {
        if (value.Equals(Disabled, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var number = value;
        long multiplier = 1;
        foreach (var (suffix, factor) in Units)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                number = value[..^suffix.Length];
                multiplier = factor;
                break;
            }
        }

        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount > long.MaxValue / multiplier)
        {
            throw new ArgumentException($"{source} requires a size such as 1048576, 512K, 10M or 1G, or {Disabled}; got '{value}'.");
        }

        if (amount == 0)
        {
            throw new ArgumentException($"{source} must be above 0; pass {Disabled} for no cap.");
        }

        return amount * multiplier;
    }

    // --max-size when given, else output.maxSize from the config layers, else no cap.
    internal static long? Cap(Options options, EngineTable config)
    {
        if (options.MaxSizeGiven)
        {
            return options.MaxSize;
        }

        var entry = config.Get(ConfigKeys.OutputSection + "." + ConfigKeys.MaxSize);
        return entry is null ? null : ParseSize(entry.Values[0], $"{entry.Key} ({entry.Origin})");
    }

    // Before the files are opened. An overwritten file never grows across runs, so only --append rotates.
    // Returns a diagnostic for each file that could not be moved; the job still runs, appending to it.
    internal static List<string> Apply(Options options, long? cap, bool sharedFile)
    {
        List<string> problems = [];
        if (cap is null || !options.Append)
        {
            return problems;
        }

        foreach (var path in new[] { options.Output, sharedFile ? null : options.Error })
        {
            if (path is null)
            {
                continue;
            }

            try
            {
                FileInfo file = new(path);
                if (file.Exists && file.Length > cap)
                {
                    File.Move(file.FullName, file.FullName + BackupSuffix, true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add($"'{path}' is over --max-size and could not be moved to '{path}{BackupSuffix}', so this run appends to it: {error.Message}");
            }
        }

        return problems;
    }
}
