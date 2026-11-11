using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeadlessRun;

// One config value under a git-style dotted key, with the layer it came from: builtin, or file:<path>.
// A boolean is "true" or "false"; a string is one value; an array is its values in order.
internal sealed record ConfigEntry(string Key, string[] Values, string Origin);

internal static class ConfigKeys
{
    internal const string EngineSection = "engine";
    internal const string ExtensionSection = "extension";
    internal const string Path = "path";
    internal const string Arguments = "arguments";
    internal const string Extensions = "extensions";
    internal const string QuoteCommand = "quoteCommand";
    internal const string SchemaProperty = "$schema";
    internal const string SchemaUrl = "https://raw.githubusercontent.com/mnaoumov/headless-run/main/config.schema.json";
    internal static readonly string[] EngineFields = [Path, Arguments, Extensions, QuoteCommand];
}

// engine.<name>.<field>, engine.<name> (unset only) or extension.<ext>.
internal readonly record struct ConfigKey(string Section, string Name, string? Field)
{
    internal string Text => Field is null ? Section + "." + Name : Section + "." + Name + "." + Field;

    internal static ConfigKey Parse(string key, bool allowEngine)
    {
        var parts = key.Split('.');
        if (parts.Length >= 2 && parts[0].Equals(ConfigKeys.EngineSection, StringComparison.OrdinalIgnoreCase) && IsName(parts[1]))
        {
            if (parts.Length == 2 && allowEngine)
            {
                return new(ConfigKeys.EngineSection, parts[1], null);
            }

            var field = parts.Length == 3
                ? ConfigKeys.EngineFields.FirstOrDefault(known => known.Equals(parts[2], StringComparison.OrdinalIgnoreCase))
                : null;
            if (field is not null)
            {
                return new(ConfigKeys.EngineSection, parts[1], field);
            }
        }

        if (parts.Length == 2 && parts[0].Equals(ConfigKeys.ExtensionSection, StringComparison.OrdinalIgnoreCase) && IsName(parts[1]))
        {
            return new(ConfigKeys.ExtensionSection, parts[1].ToLowerInvariant(), null);
        }

        throw new ArgumentException(
            $"Invalid key '{key}'. Keys are engine.<name>.path, engine.<name>.arguments, engine.<name>.extensions, " +
            "engine.<name>.quoteCommand and extension.<ext>, with names of letters, digits, '-' and '_'.");
    }

    internal static bool IsName(string name)
    {
        return name.Length != 0 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }
}

internal static class ConfigFile
{
    internal const string FileName = "config.json";
    internal const string Folder = "headless-run";
    private static readonly JsonDocumentOptions Strict = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    // %ProgramData% and %APPDATA% are read from the environment, so a test or a caller can point them elsewhere.
    internal static string SystemPath => System.IO.Path.Combine(FolderOf("ProgramData", Environment.SpecialFolder.CommonApplicationData), Folder, FileName);

    internal static string GlobalPath => System.IO.Path.Combine(FolderOf("APPDATA", Environment.SpecialFolder.ApplicationData), Folder, FileName);

    internal static string Origin(string path)
    {
        return "file:" + System.IO.Path.GetFullPath(path);
    }

    // The entries of one file. A missing file is empty unless it was named explicitly.
    internal static List<ConfigEntry> Read(string path, bool required)
    {
        if (!File.Exists(path))
        {
            if (required)
            {
                throw new FileNotFoundException($"Config file '{path}' does not exist.");
            }

            return [];
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Config file '{path}' cannot be read: {error.Message}", error);
        }

        return Parse(text, path);
    }

    internal static List<ConfigEntry> Parse(string text, string path)
    {
        var origin = Origin(path);
        List<ConfigEntry> entries = [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, Strict);
        }
        catch (JsonException error)
        {
            var message = error.Message;
            var detail = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
            throw new InvalidDataException(
                $"Config file '{path}' is not valid JSON at line {error.LineNumber + 1}, position {error.BytePositionInLine + 1}: " +
                (detail < 0 ? message : message[..detail]), error);
        }

        using (document)
        {
            var root = document.RootElement;
            Require(root.ValueKind == JsonValueKind.Object, "", "the top level must be a JSON object");
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case ConfigKeys.SchemaProperty:
                        Require(property.Value.ValueKind == JsonValueKind.String, property.Name, "must be a string");
                        break;
                    case ConfigKeys.EngineSection:
                        ReadEngines(property.Value);
                        break;
                    case ConfigKeys.ExtensionSection:
                        ReadExtensions(property.Value);
                        break;
                    default:
                        Require(false, property.Name, $"unknown key; expected {ConfigKeys.EngineSection}, {ConfigKeys.ExtensionSection} or {ConfigKeys.SchemaProperty}");
                        break;
                }
            }
        }

        return entries;

        void ReadEngines(JsonElement engines)
        {
            Require(engines.ValueKind == JsonValueKind.Object, ConfigKeys.EngineSection, "must be an object of engines by name");
            foreach (var engine in engines.EnumerateObject())
            {
                var prefix = ConfigKeys.EngineSection + "." + engine.Name;
                Require(ConfigKey.IsName(engine.Name), prefix, "an engine name has only letters, digits, '-' and '_'");
                Require(engine.Value.ValueKind == JsonValueKind.Object, prefix, "must be an object");
                foreach (var field in engine.Value.EnumerateObject())
                {
                    var key = prefix + "." + field.Name;
                    switch (field.Name)
                    {
                        case ConfigKeys.Path:
                            var paths = field.Value.ValueKind == JsonValueKind.String ? [field.Value.GetString()!] : Strings(field.Value, key);
                            Require(paths.Length != 0 && paths.All(path => path.Length != 0), key, "must be a nonempty string or array of nonempty strings");
                            entries.Add(new(key, paths, origin));
                            break;
                        case ConfigKeys.Arguments:
                            entries.Add(new(key, Strings(field.Value, key), origin));
                            break;
                        case ConfigKeys.Extensions:
                            var extensions = Strings(field.Value, key);
                            Require(extensions.All(extension => extension.Length > 1 && extension[0] == '.' && ConfigKey.IsName(extension[1..])), key,
                                "each extension starts with a dot, such as \".py\"");
                            entries.Add(new(key, [.. extensions.Select(extension => extension.ToLowerInvariant())], origin));
                            break;
                        case ConfigKeys.QuoteCommand:
                            Require(field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False, key, "must be true or false");
                            entries.Add(new(key, [field.Value.GetBoolean() ? "true" : "false"], origin));
                            break;
                        default:
                            Require(false, key, $"unknown key; an engine has {string.Join(", ", ConfigKeys.EngineFields)}");
                            break;
                    }
                }
            }
        }

        void ReadExtensions(JsonElement extensions)
        {
            Require(extensions.ValueKind == JsonValueKind.Object, ConfigKeys.ExtensionSection, "must be an object of engine names by extension");
            foreach (var extension in extensions.EnumerateObject())
            {
                var name = extension.Name.StartsWith('.') ? extension.Name[1..] : extension.Name;
                var key = ConfigKeys.ExtensionSection + "." + extension.Name;
                Require(ConfigKey.IsName(name), key, "an extension has only letters, digits, '-' and '_'");
                Require(extension.Value.ValueKind == JsonValueKind.String && ConfigKey.IsName(extension.Value.GetString()!), key, "must be an engine name");
                entries.Add(new(ConfigKeys.ExtensionSection + "." + name.ToLowerInvariant(), [extension.Value.GetString()!], origin));
            }
        }

        string[] Strings(JsonElement value, string key)
        {
            Require(value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String), key, "must be an array of strings");
            return [.. value.EnumerateArray().Select(item => item.GetString()!)];
        }

        void Require(bool condition, string key, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException($"Config file '{path}': {(key.Length == 0 ? "" : key + ": ")}{message}.");
            }
        }
    }

    // The file as a JSON tree for an edit, keeping its key order; a missing file starts with only $schema.
    internal static JsonObject Load(string path)
    {
        if (!File.Exists(path))
        {
            return new()
            {
                [ConfigKeys.SchemaProperty] = ConfigKeys.SchemaUrl
            };
        }

        var text = File.ReadAllText(path);
        Parse(text, path);
        return JsonNode.Parse(text, null, Strict)!.AsObject();
    }

    // Validates the tree, then replaces the file atomically: a temporary file beside it, then a move.
    internal static void Save(string path, JsonObject root)
    {
        var text = Serialize(root);
        Parse(text, path);
        var full = System.IO.Path.GetFullPath(path);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, full, true);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new UnauthorizedAccessException(
                $"Cannot write '{full}': access denied." + (full.Equals(System.IO.Path.GetFullPath(SystemPath), StringComparison.OrdinalIgnoreCase)
                    ? " The --system file needs an elevated prompt: run the command from one."
                    : ""), error);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal static string Serialize(JsonObject root)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            root.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    private static string FolderOf(string variable, Environment.SpecialFolder fallback)
    {
        return Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : Environment.GetFolderPath(fallback);
    }
}
