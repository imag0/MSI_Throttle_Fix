using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UXTU.Headless;

public enum RyzenFamily
{
    Unknown,
    Raphael,
    DragonRange
}

public sealed record CpuInfo(
    string Name,
    string ProcessorIdentifier,
    int Family,
    int Model,
    int Stepping,
    RyzenFamily RyzenFamily)
{
    public bool IsDragonRange => RyzenFamily == RyzenFamily.DragonRange;
    public bool IsSupportedCpu => IsDragonRange;
}

public sealed record GuardDecision(bool Allowed, string Reason, bool Forced);

public static partial class CpuDetector
{
    public static CpuInfo Detect()
    {
        string identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
        string name = ReadProcessorName();

        Match match = ProcessorIdentifierRegex().Match(identifier);
        int family = ParseGroup(match, "family");
        int model = ParseGroup(match, "model");
        int stepping = ParseGroup(match, "stepping");

        RyzenFamily ryzenFamily = RyzenFamily.Unknown;
        if (family == 25 && model == 97)
        {
            ryzenFamily = name.Contains("HX", StringComparison.OrdinalIgnoreCase)
                ? RyzenFamily.DragonRange
                : RyzenFamily.Raphael;
        }

        return new CpuInfo(name, identifier, family, model, stepping, ryzenFamily);
    }

    public static GuardDecision EvaluateGuard(CpuInfo cpu, bool force, bool dryRun)
    {
        if (dryRun)
            return new GuardDecision(true, "Dry-run mode does not access hardware.", false);

        if (cpu.IsSupportedCpu)
            return new GuardDecision(true, $"Detected supported Dragon Range CPU '{cpu.Name}'.", false);

        if (force)
            return new GuardDecision(true,
                "FORCED: CPU did not resolve to the UXTU Dragon Range target family.", true);

        return new GuardDecision(false,
            $"CPU '{cpu.Name}' did not resolve to DragonRange. Refusing hardware writes without --force.",
            false);
    }

    private static string ReadProcessorName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return Convert.ToString(key?.GetValue("ProcessorNameString"))?.Trim() ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static int ParseGroup(Match match, string name) =>
        match.Success && int.TryParse(match.Groups[name].Value, out int value) ? value : -1;

    [GeneratedRegex(@"Family\s+(?<family>\d+)\s+Model\s+(?<model>\d+)\s+Stepping\s+(?<stepping>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProcessorIdentifierRegex();
}
