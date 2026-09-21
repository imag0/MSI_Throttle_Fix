using System.Text.Json;

namespace UXTU.Headless;

public sealed record TemperatureLimitSnapshot(int Celsius);

public sealed class GlobalTemperatureSettings
{
    public const int MinimumCelsius = 60;
    public const int MaximumCelsius = 100;

    private readonly object _gate = new();
    private readonly IEventLogger _logger;
    private readonly int _defaultCelsius;
    private int _celsius;

    public GlobalTemperatureSettings(
        int defaultCelsius,
        IEventLogger logger,
        string? settingsPath = null)
    {
        if (!IsAllowed(defaultCelsius))
            throw new ArgumentOutOfRangeException(nameof(defaultCelsius));

        _logger = logger;
        _defaultCelsius = defaultCelsius;
        _celsius = defaultCelsius;
        SettingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSIThrottleFix", "temperature-limit.json"));

        try
        {
            if (!File.Exists(SettingsPath))
                return;

            TemperatureLimitSnapshot? saved = JsonSerializer.Deserialize<TemperatureLimitSnapshot>(
                File.ReadAllText(SettingsPath));
            if (saved is null || !IsAllowed(saved.Celsius))
                throw new InvalidDataException("Saved temperature limit is outside the supported range.");

            _celsius = saved.Celsius;
            _logger.Emit("temperature_limit_loaded", new
            {
                celsius = _celsius,
                settingsFile = SettingsPath
            });
        }
        catch (Exception ex)
        {
            _logger.Emit("warning", new
            {
                message = "Could not load the saved temperature limit; using the preset default.",
                settingsFile = SettingsPath,
                error = ex.Message
            });
        }
    }

    public string SettingsPath { get; }

    public uint Snapshot()
    {
        lock (_gate)
            return (uint)_celsius;
    }

    public bool CanAdjust(int deltaCelsius) => IsAllowed((long)Snapshot() + deltaCelsius);

    public bool Adjust(int deltaCelsius) => Change(current => (long)current + deltaCelsius);

    public bool RestoreDefault() => Change(_ => _defaultCelsius);

    private bool Change(Func<int, long> edit)
    {
        lock (_gate)
        {
            long next = edit(_celsius);
            if (!IsAllowed(next))
                return false;
            if (_celsius == next)
                return true;

            _celsius = (int)next;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                string temporaryPath = SettingsPath + ".tmp";
                File.WriteAllText(temporaryPath,
                    JsonSerializer.Serialize(new TemperatureLimitSnapshot(_celsius)));
                File.Move(temporaryPath, SettingsPath, true);
                _logger.Emit("temperature_limit_changed", new
                {
                    celsius = _celsius,
                    settingsFile = SettingsPath,
                    persisted = true
                });
            }
            catch (Exception ex)
            {
                _logger.Emit("warning", new
                {
                    message = "The temperature limit changed for this run but could not be saved.",
                    celsius = _celsius,
                    settingsFile = SettingsPath,
                    error = ex.Message
                });
            }

            return true;
        }
    }

    private static bool IsAllowed(long celsius) =>
        celsius >= MinimumCelsius && celsius <= MaximumCelsius;
}
