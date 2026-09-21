using System.Text.Json;

namespace UXTU.Headless;

public sealed record CycleTimingSnapshot(int BalancedMilliseconds, int ExtremeMilliseconds);

public sealed class CycleTimingSettings
{
    public const int MinimumMenuMilliseconds = 1;
    public const int MaximumMenuMilliseconds = int.MaxValue;

    private readonly object _gate = new();
    private readonly IEventLogger _logger;
    private readonly CycleTimingSnapshot _defaults;
    private CycleTimingSnapshot _current;

    public CycleTimingSettings(
        int balancedMilliseconds,
        int extremeMilliseconds,
        IEventLogger logger,
        string? settingsPath = null)
    {
        if (balancedMilliseconds < 1 || extremeMilliseconds < 1)
            throw new ArgumentOutOfRangeException(nameof(balancedMilliseconds), "Cycle delays must be positive.");

        _logger = logger;
        _defaults = new CycleTimingSnapshot(balancedMilliseconds, extremeMilliseconds);
        _current = _defaults;
        SettingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSIThrottleFix", "cycle-timing.json"));

        try
        {
            if (!File.Exists(SettingsPath))
                return;

            CycleTimingSnapshot? saved = JsonSerializer.Deserialize<CycleTimingSnapshot>(
                File.ReadAllText(SettingsPath));
            if (saved is null || saved.BalancedMilliseconds < 1 || saved.ExtremeMilliseconds < 1 ||
                saved.BalancedMilliseconds > MaximumMenuMilliseconds ||
                saved.ExtremeMilliseconds > MaximumMenuMilliseconds)
                throw new InvalidDataException("Saved cycle delays are outside the supported range.");

            _current = saved;
            _logger.Emit("timing_loaded", new
            {
                balancedMs = saved.BalancedMilliseconds,
                extremeMs = saved.ExtremeMilliseconds,
                settingsFile = SettingsPath
            });
        }
        catch (Exception ex)
        {
            _logger.Emit("warning", new
            {
                message = "Could not load saved cycle delays; using command-line values.",
                settingsFile = SettingsPath,
                error = ex.Message
            });
        }
    }

    public string SettingsPath { get; }

    public CycleTimingSnapshot Snapshot()
    {
        lock (_gate)
            return _current;
    }

    public bool AdjustBalanced(int deltaMilliseconds) => Change(
        current => current with { BalancedMilliseconds = checked(current.BalancedMilliseconds + deltaMilliseconds) });

    public bool AdjustExtreme(int deltaMilliseconds) => Change(
        current => current with { ExtremeMilliseconds = checked(current.ExtremeMilliseconds + deltaMilliseconds) });

    public bool RestoreDefaults() => Change(_ => _defaults);

    public bool CanAdjustBalanced(int deltaMilliseconds)
    {
        CycleTimingSnapshot current = Snapshot();
        return IsInMenuRange((long)current.BalancedMilliseconds + deltaMilliseconds);
    }

    public bool CanAdjustExtreme(int deltaMilliseconds)
    {
        CycleTimingSnapshot current = Snapshot();
        return IsInMenuRange((long)current.ExtremeMilliseconds + deltaMilliseconds);
    }

    private bool Change(Func<CycleTimingSnapshot, CycleTimingSnapshot> edit)
    {
        lock (_gate)
        {
            CycleTimingSnapshot next;
            try
            {
                next = edit(_current);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (!IsInMenuRange(next.BalancedMilliseconds) ||
                !IsInMenuRange(next.ExtremeMilliseconds))
                return false;
            if (next == _current)
                return true;

            _current = next;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                string temporaryPath = SettingsPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(next));
                File.Move(temporaryPath, SettingsPath, true);
                _logger.Emit("timing_changed", new
                {
                    balancedMs = next.BalancedMilliseconds,
                    extremeMs = next.ExtremeMilliseconds,
                    settingsFile = SettingsPath,
                    persisted = true
                });
            }
            catch (Exception ex)
            {
                _logger.Emit("warning", new
                {
                    message = "Cycle delays changed for this run but could not be saved.",
                    balancedMs = next.BalancedMilliseconds,
                    extremeMs = next.ExtremeMilliseconds,
                    settingsFile = SettingsPath,
                    error = ex.Message
                });
            }

            return true;
        }
    }

    private static bool IsInMenuRange(long milliseconds) =>
        milliseconds >= MinimumMenuMilliseconds && milliseconds <= MaximumMenuMilliseconds;
}
