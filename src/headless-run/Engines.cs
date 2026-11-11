using System.Diagnostics;
using System.Text;

namespace HeadlessRun;

// One engine: the interpreter a script runs under, after every config layer is applied.
internal sealed record Engine(string Name, ConfigEntry Path, string[] Arguments, string[] Extensions, bool QuoteCommand);

// The built-in engines. They are the bottom config layer, so a config file can redefine any key of them.
internal static class BuiltInEngines
{
    internal const string Origin = "builtin";
    private static readonly string[] Scripts = [".js", ".mjs", ".cjs", ".ts", ".mts", ".cts"];
    private static readonly string[] PowerShellArguments = ["-NoProfile", "-NonInteractive", "-File"];
    private static readonly (string Name, string[] Path, string[] Arguments, string[] Extensions, bool QuoteCommand)[] Rows =
    [
        ("cmd", ["%ComSpec%", @"%SystemRoot%\System32\cmd.exe"], ["/d", "/s", "/c"], [".cmd", ".bat"], true),
        ("pwsh", ["pwsh.exe"], PowerShellArguments, [".ps1"], false),
        ("powershell", [@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"], PowerShellArguments, [".ps1"], false),
        ("node", ["node.exe"], [], Scripts, false),
        ("deno", ["deno.exe"], ["run", "--allow-all"], Scripts, false),
        ("bun", ["bun.exe"], [], Scripts, false),
        ("python", ["py.exe", "python.exe"], [], [".py", ".pyw"], false),
        ("perl", ["perl.exe"], [], [".pl"], false),
        ("ruby", ["ruby.exe"], [], [".rb"], false),
        ("php", ["php.exe"], [], [".php"], false),
        ("lua", ["lua.exe"], [], [".lua"], false),
        ("rscript", ["Rscript.exe"], [], [".r"], false),
        ("tclsh", ["tclsh.exe"], [], [".tcl"], false),
        ("bash", [@"%ProgramFiles%\Git\bin\bash.exe", @"%LOCALAPPDATA%\Programs\Git\bin\bash.exe", @"%USERPROFILE%\scoop\apps\git\current\bin\bash.exe"], [], [".sh"], false),
        ("cscript", [@"%SystemRoot%\System32\cscript.exe"], ["//nologo"], [".vbs", ".wsf", ".js"], false)
    ];
    // The engine each extension runs under when no --engine is given.
    private static readonly (string Extension, string Engine)[] Defaults =
    [
        ("cmd", "cmd"),
        ("bat", "cmd"),
        ("ps1", "pwsh"),
        ("js", "node"),
        ("mjs", "node"),
        ("cjs", "node"),
        ("ts", "node"),
        ("mts", "node"),
        ("cts", "node"),
        ("py", "python"),
        ("pyw", "python"),
        ("pl", "perl"),
        ("rb", "ruby"),
        ("php", "php"),
        ("lua", "lua"),
        ("r", "rscript"),
        ("tcl", "tclsh"),
        ("sh", "bash"),
        ("vbs", "cscript"),
        ("wsf", "cscript")
    ];

    internal static IEnumerable<ConfigEntry> Entries()
    {
        foreach (var row in Rows)
        {
            var prefix = ConfigKeys.EngineSection + "." + row.Name + ".";
            yield return new(prefix + ConfigKeys.Path, row.Path, Origin);
            yield return new(prefix + ConfigKeys.Arguments, row.Arguments, Origin);
            yield return new(prefix + ConfigKeys.Extensions, row.Extensions, Origin);
            if (row.QuoteCommand)
            {
                yield return new(prefix + ConfigKeys.QuoteCommand, ["true"], Origin);
            }
        }

        foreach (var (extension, engine) in Defaults)
        {
            yield return new(ConfigKeys.ExtensionSection + "." + extension, [engine], Origin);
        }
    }

    // A built-in hint for a missing interpreter, beyond the generic config advice.
    internal static string? Hint(string engine)
    {
        return engine.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? " Install PowerShell 7, or pass --engine powershell to use Windows PowerShell 5.1."
            : null;
    }
}

// Every config layer in order: built-in, system, global, then --config. The last entry for a key wins.
internal sealed class EngineTable
{
    private readonly List<ConfigEntry> entries;

    private EngineTable(List<ConfigEntry> entries)
    {
        this.entries = entries;
    }

    internal IReadOnlyList<ConfigEntry> Entries => entries;

    internal static EngineTable Load(string? configFile)
    {
        List<ConfigEntry> entries = [.. BuiltInEngines.Entries()];
        entries.AddRange(ConfigFile.Read(ConfigFile.SystemPath, false));
        entries.AddRange(ConfigFile.Read(ConfigFile.GlobalPath, false));
        if (configFile is not null)
        {
            entries.AddRange(ConfigFile.Read(configFile, true));
        }

        return new(entries);
    }

    internal ConfigEntry? Get(string key)
    {
        return entries.LastOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    internal IEnumerable<string> EngineNames()
    {
        return entries.Select(entry => ConfigKey.Parse(entry.Key, false))
            .Where(key => key.Section == ConfigKeys.EngineSection)
            .Select(key => key.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    // The engine for a program with this extension (lower case, with its dot), or null to run it directly.
    internal Engine? Select(string? requested, string extension, bool force)
    {
        var mapping = extension.Length > 1 ? Get(ConfigKeys.ExtensionSection + "." + extension[1..]) : null;
        if (requested is null)
        {
            if (mapping is null)
            {
                return null;
            }

            return Find(mapping.Values[0]) ?? throw new InvalidDataException(
                $"extension.{extension[1..]} names the engine '{mapping.Values[0]}' ({mapping.Origin}), which no config defines.");
        }

        var engine = Find(requested) ?? throw new ArgumentException(
            $"Unknown engine '{requested}'. Known engines: {string.Join(", ", EngineNames())}. " +
            "Register one with: headless-run config --global engine.<name>.path <interpreter>");
        var accepted = engine.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || (mapping is not null && mapping.Values[0].Equals(engine.Name, StringComparison.OrdinalIgnoreCase));
        if (!accepted && !force)
        {
            var shown = extension.Length == 0 ? "a program with no extension" : $"'{extension}' files";
            var accepts = engine.Extensions.Length == 0 ? "no extension" : string.Join(", ", engine.Extensions);
            throw new ArgumentException(
                $"The {engine.Name} engine does not run {shown}; it accepts {accepts}. " +
                "Pass --force-engine to run the program with it anyway.");
        }

        return engine;
    }

    private Engine? Find(string name)
    {
        var prefix = ConfigKeys.EngineSection + "." + name + ".";
        if (!entries.Any(entry => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var path = Get(prefix + ConfigKeys.Path) ?? throw new InvalidDataException(
            $"The {name} engine has no {prefix}{ConfigKeys.Path}: set it with headless-run config --global {prefix}{ConfigKeys.Path} <interpreter>");
        var canonical = ConfigKey.Parse(path.Key, false).Name;
        return new(canonical, path, Get(prefix + ConfigKeys.Arguments)?.Values ?? [],
            Get(prefix + ConfigKeys.Extensions)?.Values ?? [],
            Get(prefix + ConfigKeys.QuoteCommand)?.Values[0] == "true");
    }
}

internal static class Interpreter
{
    private const int VersionTimeoutMilliseconds = 10000;
    private static readonly string[] TypeScriptExtensions = [".ts", ".mts", ".cts"];
    // Node strips TypeScript types by default from 23.6.0, and from 22.18.0 on the 22 line.
    private static readonly Version TypeScriptMinimum = new(23, 6, 0);
    private static readonly Version TypeScriptBackport = new(22, 18, 0);
    private const int TypeScriptBackportMajor = 22;
    private const int BackslashesInEscapedPair = 2;

    // The interpreter's full path: the first of the engine's path candidates that exists.
    internal static string Find(Engine engine, IReadOnlyDictionary<string, string> overrides)
    {
        List<string> tried = [];
        foreach (var candidate in engine.Path.Values)
        {
            var expanded = Expand(candidate, overrides);
            tried.Add(expanded);
            if (Path.IsPathRooted(expanded) || expanded.Contains('\\') || expanded.Contains('/'))
            {
                if (File.Exists(expanded))
                {
                    return Path.GetFullPath(expanded);
                }

                continue;
            }

            var found = FindOnPath(Path.HasExtension(expanded) ? expanded : expanded + ".exe", overrides);
            if (found is not null)
            {
                return found;
            }
        }

        throw new FileNotFoundException(
            $"The {engine.Name} engine's interpreter was not found (tried {string.Join(", ", tried)}; {engine.Path.Key} from {engine.Path.Origin}). " +
            $"Install it, or point the engine at it: headless-run config --global {engine.Path.Key} <full path>." +
            BuiltInEngines.Hint(engine.Name));
    }

    // Fails before the launch when Node is too old to run a TypeScript file itself.
    internal static void CheckTypeScript(Engine engine, string interpreter, string extension)
    {
        if (!engine.Name.Equals("node", StringComparison.OrdinalIgnoreCase) || !TypeScriptExtensions.Contains(extension))
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo(interpreter)
        {
            Arguments = "--version",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(VersionTimeoutMilliseconds))
        {
            process.Kill(true);
            return;
        }

        var text = output.GetAwaiter().GetResult().Trim();
        // Not a Node version: leave the answer to the interpreter itself.
        if (!Version.TryParse(text.TrimStart('v'), out var version))
        {
            return;
        }

        var supported = version >= TypeScriptMinimum || (version.Major == TypeScriptBackportMajor && version >= TypeScriptBackport);
        if (!supported)
        {
            throw new InvalidOperationException(
                $"Node {text} cannot run '{extension}' files: Node runs TypeScript itself from {TypeScriptBackport} on the " +
                $"{TypeScriptBackportMajor}.x line and from {TypeScriptMinimum}. Upgrade Node, or compile the script to JavaScript.");
        }
    }

    // The command line: the quoted interpreter and engine arguments, then the raw tail, never re-quoted.
    internal static string CommandLine(string interpreter, Engine engine, string tail)
    {
        StringBuilder line = new();
        line.Append('"').Append(interpreter).Append('"');
        foreach (var argument in engine.Arguments)
        {
            line.Append(' ').Append(Quote(argument));
        }

        line.Append(' ');
        // cmd.exe /s /c "<tail>" strips exactly the outer quote pair and runs the rest as written.
        line.Append(engine.QuoteCommand ? "\"" + tail + "\"" : tail);
        return line.ToString();
    }

    internal static string? FindOnPath(string fileName, IReadOnlyDictionary<string, string> overrides)
    {
        var path = overrides.TryGetValue("PATH", out var overridden)
            ? overridden
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    // %NAME% from the child's environment: --env overrides first, then the launcher's own.
    private static string Expand(string value, IReadOnlyDictionary<string, string> overrides)
    {
        StringBuilder result = new();
        var position = 0;
        while (position < value.Length)
        {
            var start = value.IndexOf('%', position);
            var end = start < 0 ? -1 : value.IndexOf('%', start + 1);
            if (end < 0)
            {
                result.Append(value, position, value.Length - position);
                break;
            }

            var name = value[(start + 1)..end];
            var replacement = overrides.TryGetValue(name, out var overridden) ? overridden : Environment.GetEnvironmentVariable(name);
            result.Append(value, position, start - position);
            if (replacement is null || name.Length == 0)
            {
                // Not a variable: keep the text, and let the closing % start the next search.
                result.Append('%').Append(name);
                position = end;
                continue;
            }

            result.Append(replacement);
            position = end + 1;
        }

        return result.ToString();
    }

    // CommandLineToArgvW quoting, for the engine's own arguments only.
    private static string Quote(string argument)
    {
        if (argument.Length != 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        StringBuilder quoted = new("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }

            quoted.Append('\\', character == '"' ? slashes * BackslashesInEscapedPair + 1 : slashes);
            slashes = 0;
            quoted.Append(character);
        }

        quoted.Append('\\', slashes * BackslashesInEscapedPair).Append('"');
        return quoted.ToString();
    }
}
