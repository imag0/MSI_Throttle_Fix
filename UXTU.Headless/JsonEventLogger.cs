using System.Text.Json;

namespace UXTU.Headless;

public interface IEventLogger : IDisposable
{
    void Emit(string eventName, object? data = null, bool console = true);
}

public sealed class JsonEventLogger : IEventLogger
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public JsonEventLogger(string? logPath = null)
    {
        LogPath = ResolveLogPath(logPath);
        string? directory = Path.GetDirectoryName(LogPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _writer = new StreamWriter(
            new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public string LogPath { get; }

    public void Emit(string eventName, object? data = null, bool console = true)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.Now.ToString("O"),
            ["event"] = eventName
        };

        if (data is not null)
        {
            JsonElement element = JsonSerializer.SerializeToElement(data);
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                    envelope[property.Name] = property.Value.Clone();
            }
            else
            {
                envelope["data"] = element.Clone();
            }
        }

        string line = JsonSerializer.Serialize(envelope);
        lock (_gate)
        {
            _writer.WriteLine(line);
            if (console)
                Console.Out.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }

    private static string ResolveLogPath(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return Path.GetFullPath(requested);

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "MSIThrottleFix", "MSIThrottleFix.log");
    }
}

public sealed class NullEventLogger : IEventLogger
{
    public void Emit(string eventName, object? data = null, bool console = true) { }
    public void Dispose() { }
}
