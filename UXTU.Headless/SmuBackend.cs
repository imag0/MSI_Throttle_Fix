using Universal_x86_Tuning_Utility.Scripts.AMD_Backend;

namespace UXTU.Headless;

public sealed record SmuSendResult(
    bool Ok,
    string Response,
    string? Error = null,
    uint? ResponseCode = null)
{
    public static SmuSendResult Success(uint responseCode = 1) =>
        new(true, $"SMU response 0x{responseCode:X2}", null, responseCode);

    public static SmuSendResult Failure(string response, string? error = null, uint? responseCode = null) =>
        new(false, response, error, responseCode);
}

public interface ISmuBackend : IDisposable
{
    bool IsInitialized { get; }
    bool ModuleExists { get; }
    string ModulePath { get; }
    string InitializationError { get; }
    SmuSendResult Send(SmuCommand command);
}

public sealed class DryRunSmuBackend : ISmuBackend
{
    private int _activeCalls;

    public bool IsInitialized => true;
    public bool ModuleExists => true;
    public string ModulePath => "dry-run";
    public string InitializationError => string.Empty;
    public int MaxConcurrentCalls { get; private set; }
    public List<SmuCommand> Calls { get; } = [];
    public Func<SmuCommand, SmuSendResult>? ResultFactory { get; init; }

    public SmuSendResult Send(SmuCommand command)
    {
        int active = Interlocked.Increment(ref _activeCalls);
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, active);
        try
        {
            Calls.Add(command);
            return ResultFactory?.Invoke(command) ?? SmuSendResult.Success();
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    public void Dispose() { }
}

public sealed class PawnIoSmuBackend : ISmuBackend
{
    private AMDPawnIo? _pawnIo;
    private RyzenSmuTransport? _transport;

    public PawnIoSmuBackend(string? baseDirectory = null)
    {
        string root = baseDirectory ?? AppContext.BaseDirectory;
        ModulePath = Path.Combine(root, "Assets", "AMD", "PawnIO", "RyzenSMU.bin");
        ModuleExists = File.Exists(ModulePath);

        if (!ModuleExists)
        {
            InitializationError = $"RyzenSMU.bin was not found at '{ModulePath}'.";
            return;
        }

        try
        {
            _pawnIo = AMDPawnIo.LoadModuleFromFile(ModulePath);
            if (!_pawnIo.IsLoaded)
            {
                InitializationError = "PawnIO did not load RyzenSMU.bin. Verify the PawnIO driver and elevation.";
                return;
            }

            _transport = new RyzenSmuTransport(_pawnIo);
            _transport.Open();
            IsInitialized = true;
        }
        catch (Exception ex)
        {
            InitializationError = ex.Message;
        }
    }

    public bool IsInitialized { get; }
    public bool ModuleExists { get; }
    public string ModulePath { get; }
    public string InitializationError { get; } = string.Empty;

    public SmuSendResult Send(SmuCommand command)
    {
        if (!IsInitialized || _transport is null)
            return SmuSendResult.Failure("Backend not initialized", InitializationError);

        MailboxDefinition mailbox = command.Mailbox switch
        {
            SmuMailbox.Mp1 => MailboxDefinition.Mp1,
            SmuMailbox.Rsmu => MailboxDefinition.Rsmu,
            _ => throw new ArgumentOutOfRangeException(nameof(command.Mailbox))
        };

        return _transport.Send(mailbox, command.Message, command.Argument);
    }

    public void Dispose()
    {
        _transport?.Dispose();
        _pawnIo?.Close();
    }
}
