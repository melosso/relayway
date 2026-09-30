using System.Collections;
using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Relayway;

public static partial class EnvFile
{
    public const string FileName = ".env";
    public const string PathVariable = "RELAYWAY_ENV_FILE";
    private const string FileSuffix = "_FILE";

    public static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["RELAYWAY_TENANT_ID"] = "Graph:TenantId",
        ["RELAYWAY_CLIENT_ID"] = "Graph:ClientId",
        ["RELAYWAY_CLIENT_SECRET"] = "Graph:ClientSecret",
        ["RELAYWAY_CLOUD"] = "Graph:Cloud",
        ["RELAYWAY_SEND_FROM"] = "SendFrom",
        ["RELAYWAY_SMTP_HOST"] = "Smtp:Host",
        ["RELAYWAY_SMTP_PORT"] = "Smtp:Port",
        ["RELAYWAY_ALLOWED_NETWORKS"] = "Smtp:AllowedNetworks",
        ["RELAYWAY_MAX_MESSAGE_SIZE_MB"] = "Smtp:MaxMessageSizeMb",
        ["RELAYWAY_LOG_LEVEL"] = "LogLevel",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> KnownKeys = Aliases.Values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public sealed record Entry(int Line, string Name, string Value);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    public static IConfigurationBuilder AddRelaywaySources(this IConfigurationBuilder builder, string directory, IDictionary environment, List<string> problems)
    {
        Dictionary<string, string> variables = environment.Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.Ordinal);

        string? explicitPath = variables.GetValueOrDefault(PathVariable);
        string path = explicitPath ?? Path.Combine(directory, FileName);
        if (explicitPath is not null && !File.Exists(path))
        {
            throw new FileNotFoundException($"{PathVariable} points to {path}, which does not exist");
        }

        Dictionary<string, string?> fromFile = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            foreach (Entry entry in Parse(File.ReadAllText(path), problems))
            {
                string? key = KeyFor(entry.Name);
                if (key is null || !KnownKeys.Contains(key))
                {
                    problems.Add($"{FileName} line {entry.Line}: unknown name {entry.Name}");
                    continue;
                }
                Expand(entry.Name, entry.Value, $"{FileName} line {entry.Line}", fromFile);
            }
        }

        Dictionary<string, string?> fromAliases = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string?> fromSections = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in variables)
        {
            if (IsAlias(name))
            {
                Expand(name, value, name, fromAliases);
            }
            else if (name.Contains("__", StringComparison.Ordinal) || KnownKeys.Contains(name))
            {
                fromSections[name.Replace("__", ":", StringComparison.Ordinal)] = value;
            }
        }
        foreach (string key in fromAliases.Keys.Where(k => fromSections.ContainsKey(k) && fromSections[k] != fromAliases[k]))
        {
            problems.Add($"{key.Replace(":", "__", StringComparison.Ordinal)} overrides {Aliases.First(a => a.Value.Equals(key, StringComparison.OrdinalIgnoreCase)).Key}");
        }

        return builder
            .AddJsonFile(Path.Combine(directory, "appsettings.json"), optional: true)
            .AddInMemoryCollection(fromFile)
            .AddInMemoryCollection(fromAliases)
            .AddInMemoryCollection(fromSections);
    }

    public static List<Entry> Parse(string text, List<string> problems)
    {
        List<Entry> entries = [];
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            int eq = line.IndexOf('=');
            string name = eq > 0 ? line[..eq].Trim() : "";
            if (!NamePattern().IsMatch(name))
            {
                problems.Add($"{FileName} line {i + 1}: not NAME=value");
                continue;
            }
            entries.Add(new Entry(i + 1, name, Unquote(line[(eq + 1)..].Trim())));
        }

        foreach (IGrouping<string, Entry> repeated in entries.GroupBy(e => e.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            problems.Add($"{FileName}: {repeated.Key} is set on lines {string.Join(", ", repeated.Select(e => e.Line))}; line {repeated.Last().Line} wins");
        }
        return entries;
    }

    private static bool IsAlias(string name) =>
        Aliases.ContainsKey(name) || (name.EndsWith(FileSuffix, StringComparison.Ordinal) && Aliases.ContainsKey(name[..^FileSuffix.Length]));

    private static string? KeyFor(string name) =>
        IsAlias(name) ? Aliases[name.EndsWith(FileSuffix, StringComparison.Ordinal) && !Aliases.ContainsKey(name) ? name[..^FileSuffix.Length] : name]
        : name.Contains("__", StringComparison.Ordinal) ? name.Replace("__", ":", StringComparison.Ordinal)
        : KnownKeys.Contains(name) ? name
        : null;

    private static void Expand(string name, string value, string source, Dictionary<string, string?> target)
    {
        if (IsAlias(name) && !Aliases.ContainsKey(name))
        {
            if (!File.Exists(value))
            {
                throw new FileNotFoundException($"{source}: {name} points to {value}, which does not exist");
            }
            value = File.ReadAllText(value).Trim();
        }
        target[KeyFor(name)!] = value;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
        {
            return value[1..^1];
        }
        int comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }
}
