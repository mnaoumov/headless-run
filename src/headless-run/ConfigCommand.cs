using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace HeadlessRun;

// headless-run config: read and write the engine config files, after git config.
internal static class ConfigCommand
{
    internal const string Name = "config";
    // git config's own codes: a key that is not set, and an --unset that found nothing.
    private const int KeyNotFound = 1;
    private const int NothingToUnset = 5;
    private const string Usage = """
        Usage: headless-run config [--global | --system | --file FILE] <action>
          --list, -l [--show-origin]  List every entry; with no scope, all layers in order
          --edit, -e                  Open the file in an editor, then validate it
          <key>                       Print a value (one line per array item)
          <key> <value>               Set a value; extensions takes a comma-separated list
          --add <key> <value>         Append to path, arguments or extensions
          --unset <key>               Remove a key, or a whole engine.<name>
        Keys: engine.<name>.path, engine.<name>.arguments, engine.<name>.extensions,
              engine.<name>.quoteCommand, extension.<ext>
        Writes go to --global unless a scope is given. Exit: 0; 1 key not set;
        5 nothing to unset; 125 invalid usage or config.
        """ + "\n";

    private enum Action
    {
        Read,
        List,
        Edit,
        Add,
        Unset
    }

    internal static int Run(string arguments)
    {
        var attached = AttachOutput();
        try
        {
            return Execute(Tokenize(arguments), attached);
        }
        catch (Exception error)
        {
            Console.Error.Write($"headless-run: {error.Message}\n");
            return Program.LaunchFailure;
        }
        finally
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
    }

    private static int Execute(List<string> tokens, bool attached)
    {
        string? scope = null;
        var action = Action.Read;
        var actionSet = false;
        var showOrigin = false;
        List<string> positional = [];
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            // Options come before the key; a value such as -NoProfile after it is a value.
            if (positional.Count != 0 || !token.StartsWith('-'))
            {
                positional.Add(token);
                continue;
            }

            switch (token)
            {
                case "--global":
                    SetScope(ConfigFile.GlobalPath, token);
                    break;
                case "--system":
                    SetScope(ConfigFile.SystemPath, token);
                    break;
                case "--file":
                case "-f":
                    if (++index == tokens.Count || tokens[index].Length == 0)
                    {
                        throw new ArgumentException($"Missing value for {token}.");
                    }

                    SetScope(Path.GetFullPath(tokens[index]), token);
                    break;
                case "--list":
                case "-l":
                    SetAction(Action.List, token);
                    break;
                case "--edit":
                case "-e":
                    SetAction(Action.Edit, token);
                    break;
                case "--add":
                    SetAction(Action.Add, token);
                    break;
                case "--unset":
                    SetAction(Action.Unset, token);
                    break;
                case "--show-origin":
                    showOrigin = true;
                    break;
                case "--help":
                case "-h":
                    Console.Out.Write(Usage);
                    return 0;
                default:
                    if (token.StartsWith("--file=", StringComparison.Ordinal) && token.Length > "--file=".Length)
                    {
                        SetScope(Path.GetFullPath(token["--file=".Length..]), "--file");
                        break;
                    }

                    throw new ArgumentException($"Unknown config option '{token}'. Use headless-run config --help.");
            }
        }

        if (showOrigin && action != Action.List)
        {
            throw new ArgumentException("--show-origin applies only to --list.");
        }

        switch (action)
        {
            case Action.List:
                Expect(0);
                return List(scope, showOrigin);
            case Action.Edit:
                Expect(0);
                return Edit(scope ?? ConfigFile.GlobalPath, attached);
            case Action.Add:
                Expect(2);
                return Write(scope ?? ConfigFile.GlobalPath, positional[0], positional[1], true);
            case Action.Unset:
                Expect(1);
                return Unset(scope ?? ConfigFile.GlobalPath, positional[0]);
            default:
                if (positional.Count == 1)
                {
                    return Get(scope, positional[0]);
                }

                Expect(2);
                return Write(scope ?? ConfigFile.GlobalPath, positional[0], positional[1], false);
        }

        void SetScope(string path, string option)
        {
            if (scope is not null)
            {
                throw new ArgumentException($"{option} cannot be combined with another scope.");
            }

            scope = path;
        }

        void SetAction(Action value, string option)
        {
            if (actionSet)
            {
                throw new ArgumentException($"{option} cannot be combined with another action.");
            }

            action = value;
            actionSet = true;
        }

        void Expect(int count)
        {
            if (positional.Count != count)
            {
                throw new ArgumentException($"Wrong number of arguments: expected {count}, got {positional.Count}. Use headless-run config --help.");
            }
        }
    }

    private static int List(string? scope, bool showOrigin)
    {
        IEnumerable<ConfigEntry> entries = scope is null ? EngineTable.Load(null).Entries : ConfigFile.Read(scope, false);
        foreach (var entry in entries)
        {
            var prefix = showOrigin ? entry.Origin + "\t" : "";
            if (entry.Values.Length == 0)
            {
                Console.Out.Write($"{prefix}{entry.Key}=\n");
            }

            foreach (var value in entry.Values)
            {
                Console.Out.Write($"{prefix}{entry.Key}={value}\n");
            }
        }

        return 0;
    }

    private static int Get(string? scope, string text)
    {
        var key = ConfigKey.Parse(text, false).Text;
        var entry = scope is null
            ? EngineTable.Load(null).Get(key)
            : ConfigFile.Read(scope, false).LastOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return KeyNotFound;
        }

        foreach (var value in entry.Values)
        {
            Console.Out.Write(value + "\n");
        }

        return 0;
    }

    private static int Write(string path, string text, string value, bool add)
    {
        var key = ConfigKey.Parse(text, false);
        var root = ConfigFile.Load(path);
        if (key.Section == ConfigKeys.ExtensionSection)
        {
            if (add)
            {
                throw new ArgumentException($"--add needs an array key; {key.Text} holds one engine name.");
            }

            Section(root, ConfigKeys.ExtensionSection)[Existing(Section(root, ConfigKeys.ExtensionSection), key.Name) ?? key.Name] = value;
        }
        else
        {
            var engines = Section(root, ConfigKeys.EngineSection);
            var name = Existing(engines, key.Name) ?? key.Name;
            if (engines[name] is not JsonObject engine)
            {
                engine = [];
                engines[name] = engine;
            }

            switch (key.Field)
            {
                case ConfigKeys.QuoteCommand:
                    if (add || value is not ("true" or "false"))
                    {
                        throw new ArgumentException($"{key.Text} is true or false.");
                    }

                    engine[key.Field] = value == "true";
                    break;
                case ConfigKeys.Path when !add:
                    engine[key.Field] = value;
                    break;
                default:
                    var values = key.Field == ConfigKeys.Extensions
                        ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(extension => (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant())
                        : [value];
                    var array = add ? AsArray(engine[key.Field!]) : [];
                    foreach (var item in values)
                    {
                        array.Add((JsonNode) JsonValue.Create(item));
                    }

                    engine[key.Field!] = array;
                    break;
            }
        }

        ConfigFile.Save(path, root);
        return 0;
    }

    private static int Unset(string path, string text)
    {
        var key = ConfigKey.Parse(text, true);
        if (!File.Exists(path))
        {
            return NothingToUnset;
        }

        var root = ConfigFile.Load(path);
        if (root[key.Section] is not JsonObject section || Existing(section, key.Name) is not { } name)
        {
            return NothingToUnset;
        }

        if (key.Field is null)
        {
            section.Remove(name);
        }
        else
        {
            if (section[name] is not JsonObject engine || !engine.Remove(key.Field))
            {
                return NothingToUnset;
            }

            if (engine.Count == 0)
            {
                section.Remove(name);
            }
        }

        ConfigFile.Save(path, root);
        return 0;
    }

    private static int Edit(string path, bool attached)
    {
        if (!File.Exists(path))
        {
            JsonObject root = new()
            {
                [ConfigKeys.SchemaProperty] = ConfigKeys.SchemaUrl,
                [ConfigKeys.EngineSection] = new JsonObject()
            };
            ConfigFile.Save(path, root);
        }

        var editor = new[] { "HEADLESS_RUN_EDITOR", "VISUAL", "EDITOR" }
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "notepad.exe";
        var shell = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec
            ? comSpec
            : Path.Combine(Environment.SystemDirectory, "cmd.exe");
        // As git does, the editor value is a command, so "code --wait" works: cmd /s /c runs it as written.
        using var process = Process.Start(new ProcessStartInfo(shell)
        {
            Arguments = $"/d /s /c \"{editor} \"{Path.GetFullPath(path)}\"\"",
            UseShellExecute = false,
            // A terminal editor needs the console this command attached to; with none, no window flashes.
            CreateNoWindow = !attached
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The editor '{editor}' exited with code {process.ExitCode}; '{path}' was not validated.");
        }

        ConfigFile.Read(path, true);
        return 0;
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject section)
        {
            return section;
        }

        section = [];
        root[name] = section;
        return section;
    }

    // The spelling a key already has in the file, matched without case.
    private static string? Existing(JsonObject section, string name)
    {
        return section.Select(property => property.Key)
            .FirstOrDefault(key => key.TrimStart('.').Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonArray AsArray(JsonNode? node)
    {
        return node switch
        {
            null => [],
            JsonArray array => new JsonArray([.. array.Select(item => item?.DeepClone())]),
            _ => [node.DeepClone()]
        };
    }

    // A Windows-subsystem process has no console: reuse the terminal's, when output is not redirected.
    private static bool AttachOutput()
    {
        var output = Native.GetStdHandle(Native.StandardOutputHandle);
        var error = Native.GetStdHandle(Native.StandardErrorHandle);
        if (output is not (0 or -1) && error is not (0 or -1))
        {
            return false;
        }

        if (!Native.AttachConsole(Native.AttachParentProcess))
        {
            return false;
        }

        Native.SecurityAttributes security = new()
        {
            Size = Marshal.SizeOf<Native.SecurityAttributes>(),
            Inherit = 1
        };
        var console = Native.CreateFile("CONOUT$", Native.GenericRead | Native.GenericWrite, Native.FileShareRead | Native.FileShareWrite,
            ref security, Native.OpenExisting, 0, 0);
        if (console is 0 or -1)
        {
            return false;
        }

        if (output is 0 or -1)
        {
            Native.SetStdHandle(Native.StandardOutputHandle, console);
        }

        if (error is 0 or -1)
        {
            Native.SetStdHandle(Native.StandardErrorHandle, console);
        }

        return true;
    }

    private static List<string> Tokenize(string arguments)
    {
        List<string> tokens = [];
        var position = 0;
        while (true)
        {
            Options.SkipSpace(arguments, ref position);
            if (position == arguments.Length)
            {
                return tokens;
            }

            tokens.Add(Options.ReadToken(arguments, ref position));
        }
    }
}
