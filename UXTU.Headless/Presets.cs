namespace UXTU.Headless;

public sealed record AmdPreset(
    string Name,
    uint Tctl,
    uint Chtc,
    uint Stapm,
    uint Fast,
    uint StapmTime,
    uint Slow,
    uint SlowTime,
    uint Tdc,
    uint Edc);

public static class Presets
{
    public static readonly AmdPreset Balanced = new(
        "Balanced",
        Tctl: 100,
        Chtc: 100,
        Stapm: 65_000,
        Fast: 75_000,
        StapmTime: 64,
        Slow: 65_000,
        SlowTime: 128,
        Tdc: 180_000,
        Edc: 180_000);

    public static readonly AmdPreset Extreme = new(
        "Extreme",
        Tctl: 100,
        Chtc: 100,
        Stapm: 125_000,
        Fast: 145_000,
        StapmTime: 64,
        Slow: 125_000,
        SlowTime: 128,
        Tdc: 240_000,
        Edc: 240_000);
}

public sealed record CpuPresetProfile(
    string Id,
    string DisplayName,
    AmdPreset Balanced,
    AmdPreset Extreme);

public static class PresetProfiles
{
    /// <summary>
    /// UXTU applies this profile to the DragonRange family rather than to one
    /// individual SKU. This covers Ryzen 7/9 Dragon Range HX processors that
    /// resolve as Family 25, Model 97.
    /// </summary>
    public static readonly CpuPresetProfile DragonRange = new(
        "uxtu-dragon-range",
        "UXTU Dragon Range",
        Presets.Balanced,
        Presets.Extreme);

    public static CpuPresetProfile? Select(CpuInfo cpu) =>
        cpu.IsDragonRange ? DragonRange : null;
}

public enum SmuMailbox
{
    Mp1,
    Rsmu
}

public sealed record SmuCommand(string Name, SmuMailbox Mailbox, uint Message, uint Argument);

public static class DragonRangeAm5CommandTable
{
    public const uint Mp1MessageAddress = 0x3B10530;
    public const uint Mp1ResponseAddress = 0x3B1057C;
    public const uint Mp1ArgumentAddress = 0x3B109C4;

    public const uint RsmuMessageAddress = 0x03B10524;
    public const uint RsmuResponseAddress = 0x03B10570;
    public const uint RsmuArgumentAddress = 0x03B10A40;

    private static readonly IReadOnlyDictionary<string, (SmuMailbox Mailbox, uint Message)[]> Commands =
        new Dictionary<string, (SmuMailbox, uint)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["tctl"] = [(SmuMailbox.Mp1, 0x3F), (SmuMailbox.Rsmu, 0x59)],
            ["chtc"] = [(SmuMailbox.Rsmu, 0x59)],
            ["stapm"] = [(SmuMailbox.Mp1, 0x4F)],
            ["fast"] = [(SmuMailbox.Mp1, 0x3E)],
            ["stapm-time"] = [(SmuMailbox.Mp1, 0x53)],
            ["slow"] = [(SmuMailbox.Mp1, 0x5F), (SmuMailbox.Rsmu, 0xCB)],
            ["slow-time"] = [(SmuMailbox.Mp1, 0x60)],
            ["tdc"] = [(SmuMailbox.Mp1, 0x3C), (SmuMailbox.Rsmu, 0x57)],
            ["edc"] = [(SmuMailbox.Mp1, 0x3D), (SmuMailbox.Rsmu, 0x58)]
        };

    public static string Description =>
        "Socket_AM5_V1 MP1(msg=0x3B10530,rsp=0x3B1057C,arg=0x3B109C4) " +
        "RSMU(msg=0x3B10524,rsp=0x3B10570,arg=0x3B10A40)";

    public static IReadOnlyList<SmuCommand> BuildPresetPlan(AmdPreset preset)
    {
        var plan = new List<SmuCommand>(13);
        Add(plan, "tctl", preset.Tctl);
        Add(plan, "chtc", preset.Chtc);
        Add(plan, "stapm", preset.Stapm);
        Add(plan, "fast", preset.Fast);
        Add(plan, "stapm-time", preset.StapmTime);
        Add(plan, "slow", preset.Slow);
        Add(plan, "slow-time", preset.SlowTime);
        Add(plan, "tdc", preset.Tdc);
        Add(plan, "edc", preset.Edc);
        return plan;
    }

    public static IReadOnlyList<SmuCommand> BuildDiagnosticPlan(string name, uint value)
    {
        if (!Commands.ContainsKey(name))
            throw new ArgumentException(
                $"Unsupported command '{name}'. Supported commands: {string.Join(", ", Commands.Keys)}");

        var plan = new List<SmuCommand>();
        Add(plan, name, value);
        return plan;
    }

    private static void Add(List<SmuCommand> plan, string name, uint value)
    {
        foreach ((SmuMailbox mailbox, uint message) in Commands[name])
            plan.Add(new SmuCommand(name, mailbox, message, value));
    }
}
