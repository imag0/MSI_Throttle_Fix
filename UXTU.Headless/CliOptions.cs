namespace UXTU.Headless;

public sealed class CliOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    private CliOptions(string command, IReadOnlyList<string> positionals)
    {
        Command = command;
        Positionals = positionals;
    }

    public string Command { get; }
    public IReadOnlyList<string> Positionals { get; }

    public bool DryRun => HasFlag("dry-run");
    public bool Force => HasFlag("force");
    public bool Verbose => HasFlag("verbose");
    public bool Hidden => HasFlag("hidden");
    public bool Tray => HasFlag("tray");
    public bool FinalExtreme => !HasFlag("no-final-extreme");

    public static CliOptions Parse(string[] args)
    {
        if (args.Length == 0)
            return new CliOptions("help", []);

        string command = args[0].ToLowerInvariant();
        var positionals = new List<string>();
        var parsed = new CliOptions(command, positionals);

        for (int index = 1; index < args.Length; index++)
        {
            string token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(token);
                continue;
            }

            string name = token[2..];
            if (name is "dry-run" or "force" or "verbose" or "hidden" or "tray" or "no-final-extreme")
            {
                parsed._flags.Add(name);
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option --{name} requires a value.");

            parsed._values[name] = args[++index];
        }

        return parsed;
    }

    public bool HasFlag(string name) => _flags.Contains(name);

    public int GetInt(string name, int defaultValue, int minimum = 1)
    {
        if (!_values.TryGetValue(name, out string? raw))
            return defaultValue;
        if (!int.TryParse(raw, out int value) || value < minimum)
            throw new ArgumentException($"--{name} must be an integer >= {minimum}.");
        return value;
    }

    public string? GetString(string name) =>
        _values.TryGetValue(name, out string? value) ? value : null;
}
