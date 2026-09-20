namespace UXTU.Headless;

public sealed record CycleOptions(
    int BalancedMilliseconds = 750,
    int ExtremeMilliseconds = 4250,
    bool FinalExtreme = true,
    int? MaxCycles = null);

public sealed class CycleRunner
{
    private readonly HeadlessSmuController _controller;
    private readonly IEventLogger _logger;
    private readonly Action<string, long, long>? _phaseChanged;
    private readonly bool _verbose;

    public CycleRunner(
        HeadlessSmuController controller,
        IEventLogger logger,
        Action<string, long, long>? phaseChanged = null,
        bool verbose = true)
    {
        _controller = controller;
        _logger = logger;
        _phaseChanged = phaseChanged;
        _verbose = verbose;
    }

    public long CompletedCycles { get; private set; }

    public async Task RunCycleAsync(CycleOptions options, CancellationToken cancellationToken)
    {
        ValidateDelay(options.BalancedMilliseconds, nameof(options.BalancedMilliseconds));
        ValidateDelay(options.ExtremeMilliseconds, nameof(options.ExtremeMilliseconds));

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   (!options.MaxCycles.HasValue || CompletedCycles < options.MaxCycles.Value))
            {
                long cycle = CompletedCycles + 1;
                EmitPhase("BALANCED", cycle);
                _controller.ApplyPreset(Presets.Balanced);
                await Task.Delay(options.BalancedMilliseconds, cancellationToken).ConfigureAwait(false);

                EmitPhase("EXTREME", cycle);
                _controller.ApplyPreset(Presets.Extreme);
                await Task.Delay(options.ExtremeMilliseconds, cancellationToken).ConfigureAwait(false);

                CompletedCycles++;
                if (_verbose)
                {
                    _logger.Emit("cycle_complete", new
                    {
                        cycle = CompletedCycles,
                        hardwareWriteFailures = _controller.HardwareWriteFailures
                    });
                }
                else if (CompletedCycles == 1)
                {
                    _logger.Emit("cycle_running", new
                    {
                        message = "The first Balanced/Extreme cycle completed.",
                        cycle = CompletedCycles,
                        hardwareWriteFailures = _controller.HardwareWriteFailures
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_verbose)
            {
                _logger.Emit("cancellation_requested", new
                {
                    cycle = CompletedCycles,
                    hardwareWriteFailures = _controller.HardwareWriteFailures
                });
            }
        }
        finally
        {
            if (options.FinalExtreme)
            {
                if (_verbose)
                {
                    _logger.Emit("final_extreme", new
                    {
                        cycle = CompletedCycles,
                        hardwareWriteFailures = _controller.HardwareWriteFailures
                    });
                }
                _phaseChanged?.Invoke("EXTREME (final)", CompletedCycles, _controller.HardwareWriteFailures);
                _controller.ApplyPreset(Presets.Extreme);
            }
        }
    }

    public async Task RunExtremeOnlyAsync(
        int intervalMilliseconds,
        bool finalExtreme,
        int? maxApplications,
        CancellationToken cancellationToken)
    {
        ValidateDelay(intervalMilliseconds, nameof(intervalMilliseconds));
        long applications = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   (!maxApplications.HasValue || applications < maxApplications.Value))
            {
                applications++;
                EmitPhase("EXTREME", applications);
                _controller.ApplyPreset(Presets.Extreme);
                if (!_verbose && applications == 1)
                {
                    _logger.Emit("extreme_only_running", new
                    {
                        message = "The first Extreme application completed.",
                        cycle = applications,
                        hardwareWriteFailures = _controller.HardwareWriteFailures
                    });
                }
                await Task.Delay(intervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_verbose)
            {
                _logger.Emit("cancellation_requested", new
                {
                    cycle = applications,
                    hardwareWriteFailures = _controller.HardwareWriteFailures
                });
            }
        }
        finally
        {
            if (finalExtreme)
            {
                if (_verbose)
                {
                    _logger.Emit("final_extreme", new
                    {
                        cycle = applications,
                        hardwareWriteFailures = _controller.HardwareWriteFailures
                    });
                }
                _phaseChanged?.Invoke("EXTREME (final)", applications, _controller.HardwareWriteFailures);
                _controller.ApplyPreset(Presets.Extreme);
            }
        }

        CompletedCycles = applications;
    }

    private void EmitPhase(string preset, long cycle)
    {
        _phaseChanged?.Invoke(preset, cycle, _controller.HardwareWriteFailures);
        if (_verbose)
        {
            _logger.Emit("phase", new
            {
                preset,
                cycle,
                hardwareWriteFailures = _controller.HardwareWriteFailures
            });
        }
    }

    private static void ValidateDelay(int milliseconds, string name)
    {
        if (milliseconds < 1)
            throw new ArgumentOutOfRangeException(name, "Delay must be at least 1 ms.");
    }
}
