namespace UXTU.Headless;

public sealed record ApplySummary(string Operation, int Attempted, int Succeeded, int Failed)
{
    public bool Ok => Failed == 0;
}

public sealed class HeadlessSmuController
{
    private readonly ISmuBackend _backend;
    private readonly IEventLogger _logger;
    private readonly bool _verbose;
    private readonly Action<long>? _failureChanged;
    private readonly Func<uint>? _temperatureLimit;

    public HeadlessSmuController(
        ISmuBackend backend,
        IEventLogger logger,
        bool verbose,
        Action<long>? failureChanged = null,
        Func<uint>? temperatureLimit = null)
    {
        _backend = backend;
        _logger = logger;
        _verbose = verbose;
        _failureChanged = failureChanged;
        _temperatureLimit = temperatureLimit;
    }

    public long HardwareWriteFailures { get; private set; }

    public ApplySummary ApplyPreset(AmdPreset preset)
    {
        if (_temperatureLimit is not null)
        {
            uint celsius = _temperatureLimit();
            preset = preset with { Tctl = celsius, Chtc = celsius };
        }

        return ApplyCommands(preset.Name, DragonRangeAm5CommandTable.BuildPresetPlan(preset));
    }

    public ApplySummary ApplyDiagnostic(string name, uint value) =>
        ApplyCommands($"apply-command:{name}", DragonRangeAm5CommandTable.BuildDiagnosticPlan(name, value));

    public ApplySummary ApplyCommands(string operation, IReadOnlyList<SmuCommand> commands)
    {
        int succeeded = 0;
        int failed = 0;

        foreach (SmuCommand command in commands)
        {
            SmuSendResult result = _backend.Send(command);
            if (result.Ok)
            {
                succeeded++;
                if (_verbose)
                    LogWrite(operation, command, result);
            }
            else
            {
                failed++;
                HardwareWriteFailures++;
                LogWrite(operation, command, result);
                _failureChanged?.Invoke(HardwareWriteFailures);
            }
        }

        var summary = new ApplySummary(operation, commands.Count, succeeded, failed);
        if (_verbose || !summary.Ok)
        {
            _logger.Emit("preset_result", new
            {
                operation,
                attempted = summary.Attempted,
                succeeded = summary.Succeeded,
                failed = summary.Failed,
                ok = summary.Ok,
                hardwareWriteFailures = HardwareWriteFailures
            });
        }
        return summary;
    }

    private void LogWrite(string operation, SmuCommand command, SmuSendResult result)
    {
        _logger.Emit("write", new
        {
            preset = operation,
            command = command.Name,
            mailbox = command.Mailbox == SmuMailbox.Mp1 ? "MP1" : "RSMU",
            messageId = $"0x{command.Message:X2}",
            argument = command.Argument,
            ok = result.Ok,
            response = result.Response,
            responseCode = result.ResponseCode,
            error = result.Error,
            hardwareWriteFailures = HardwareWriteFailures
        });
    }
}
