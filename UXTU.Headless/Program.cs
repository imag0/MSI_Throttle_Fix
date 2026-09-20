using System.Security.Principal;

namespace UXTU.Headless;

public static class Program
{
    private const int ExitUsage = 2;
    private const int ExitAdministratorRequired = 3;
    private const int ExitCpuGuard = 4;
    private const int ExitBackendInitialization = 5;
    private const int ExitHardwareFailure = 6;

    public static async Task<int> Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            PrintUsage();
            return ExitUsage;
        }

        if (options.Hidden)
            ConsoleWindow.Hide();

        if (options.Command is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        using var logger = new JsonEventLogger(options.GetString("log-file"));
        RegisterUnhandledExceptionLogging(logger);

        CpuInfo cpu = CpuDetector.Detect();
        bool elevated = IsElevated();

        if (options.Command == "info")
            return RunInfo(cpu, elevated, options, logger);

        if (!IsWriteCommand(options.Command))
        {
            logger.Emit("error", new { message = $"Unknown command '{options.Command}'." });
            PrintUsage();
            return ExitUsage;
        }

        GuardDecision guard = CpuDetector.EvaluateGuard(cpu, options.Force, options.DryRun);
        if (!guard.Allowed)
        {
            logger.Emit("error", new
            {
                message = guard.Reason,
                cpu = cpu.Name,
                family = cpu.RyzenFamily.ToString()
            });
            return ExitCpuGuard;
        }

        if (guard.Forced)
        {
            logger.Emit("warning", new
            {
                message = "*** --force OVERRIDE ACTIVE: WRITING AUDITED 7945HX VALUES TO A NON-MATCHING CPU ***",
                reason = guard.Reason
            });
        }

        if (!options.DryRun && !elevated)
        {
            logger.Emit("error", new { message = "Administrator privileges are required." });
            return ExitAdministratorRequired;
        }

        using SingleWriterLease? writerLease = options.DryRun ? null : SingleWriterLease.TryAcquire();
        if (writerLease is not null && !writerLease.Acquired)
        {
            logger.Emit("error", new
            {
                message = "Another MSIThrottleFix hardware-writer process is already running."
            });
            return ExitBackendInitialization;
        }

        using var cancellation = new CancellationTokenSource();
        using NativeTrayIcon? tray = CreateTrayIcon(options, logger, cancellation);
        string currentPhase = "INITIALIZING";
        long currentCycle = 0;
        tray?.Update(currentPhase, currentCycle, 0);

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            using ISmuBackend? backend = await InitializeBackendAsync(
                options.DryRun, logger, tray, cancellation.Token);
            if (backend is null)
                return ExitBackendInitialization;

            logger.Emit("started", new
            {
                cpu = cpu.Name,
                processorIdentifier = cpu.ProcessorIdentifier,
                family = cpu.RyzenFamily.ToString(),
                dragonRange = cpu.IsDragonRange,
                expectedCpu = cpu.IsExpectedCpu,
                elevated,
                dryRun = options.DryRun,
                pawnIoInitialized = backend.IsInitialized,
                ryzenSmuModuleExists = backend.ModuleExists,
                ryzenSmuModulePath = backend.ModulePath,
                mailboxTable = DragonRangeAm5CommandTable.Description,
                logFile = logger.LogPath
            });

            if (!backend.IsInitialized)
            {
                tray?.Update("ERROR", 0, 1);
                logger.Emit("error", new
                {
                    message = "PawnIO/SMU backend initialization failed.",
                    error = backend.InitializationError,
                    module = backend.ModulePath
                });
                return ExitBackendInitialization;
            }

            currentPhase = "READY";
            tray?.Update(currentPhase, currentCycle, 0);
            var controller = new HeadlessSmuController(
                backend,
                logger,
                options.Verbose,
                failures => tray?.Update(currentPhase, currentCycle, failures));

            Action<string, long, long> phaseChanged = (phase, cycle, failures) =>
            {
                currentPhase = phase;
                currentCycle = cycle;
                tray?.Update(phase, cycle, failures);
            };

            int result = options.Command switch
            {
                "balanced" => ApplyOnce(controller, Presets.Balanced),
                "extreme" => ApplyOnce(controller, Presets.Extreme),
                "apply-command" => ApplyDiagnostic(controller, options.Positionals),
                "cycle" or "daemon" => await RunCycleAsync(
                    controller, logger, options, phaseChanged, cancellation.Token),
                "watch-extreme" or "extreme-only" =>
                    await RunExtremeOnlyAsync(controller, logger, options, phaseChanged, cancellation.Token),
                _ => ExitUsage
            };

            logger.Emit("stopped", new
            {
                exitCode = result,
                hardwareWriteFailures = controller.HardwareWriteFailures
            });
            return result;
        }
        catch (ArgumentException ex)
        {
            logger.Emit("error", new { message = ex.Message });
            return ExitUsage;
        }
        catch (Exception ex)
        {
            logger.Emit("unhandled_exception", new { error = ex.ToString() });
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<ISmuBackend?> InitializeBackendAsync(
        bool dryRun,
        IEventLogger logger,
        NativeTrayIcon? tray,
        CancellationToken cancellationToken)
    {
        if (dryRun)
            return new DryRunSmuBackend();

        Task<ISmuBackend> initialization = Task.Factory.StartNew<ISmuBackend>(
            () => new PawnIoSmuBackend(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Task timeout = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        Task completed = await Task.WhenAny(initialization, timeout).ConfigureAwait(false);

        if (completed == initialization)
            return await initialization.ConfigureAwait(false);

        bool cancelled = cancellationToken.IsCancellationRequested;
        tray?.Update(cancelled ? "STOPPING" : "ERROR", 0, cancelled ? 0 : 1);
        logger.Emit(cancelled ? "cancellation_requested" : "error", new
        {
            message = cancelled
                ? "Backend initialization was cancelled."
                : "PawnIO/SMU backend initialization timed out after 10 seconds. No SMU writes were attempted."
        });
        return null;
    }

    private static int RunInfo(CpuInfo cpu, bool elevated, CliOptions options, JsonEventLogger logger)
    {
        using ISmuBackend backend = options.DryRun
            ? new DryRunSmuBackend()
            : new PawnIoSmuBackend();

        logger.Emit("info", new
        {
            cpu = cpu.Name,
            processorIdentifier = cpu.ProcessorIdentifier,
            cpuFamily = cpu.Family,
            cpuModel = cpu.Model,
            cpuStepping = cpu.Stepping,
            ryzenFamily = cpu.RyzenFamily.ToString(),
            dragonRangeSelected = cpu.IsDragonRange,
            expectedCpu = cpu.IsExpectedCpu,
            elevated,
            dryRun = options.DryRun,
            pawnIoInitialized = backend.IsInitialized,
            ryzenSmuModuleExists = backend.ModuleExists,
            ryzenSmuModuleLoaded = backend.IsInitialized,
            ryzenSmuModulePath = backend.ModulePath,
            initializationError = backend.InitializationError,
            selectedMailboxTable = DragonRangeAm5CommandTable.Description,
            logFile = logger.LogPath
        });
        return 0;
    }

    private static int ApplyOnce(HeadlessSmuController controller, AmdPreset preset)
    {
        ApplySummary summary = controller.ApplyPreset(preset);
        return summary.Ok ? 0 : ExitHardwareFailure;
    }

    private static int ApplyDiagnostic(HeadlessSmuController controller, IReadOnlyList<string> positionals)
    {
        if (positionals.Count != 2 || !uint.TryParse(positionals[1], out uint value))
            throw new ArgumentException("Usage: apply-command <name> <unsigned-value>");

        ApplySummary summary = controller.ApplyDiagnostic(positionals[0], value);
        return summary.Ok ? 0 : ExitHardwareFailure;
    }

    private static async Task<int> RunCycleAsync(
        HeadlessSmuController controller,
        IEventLogger logger,
        CliOptions options,
        Action<string, long, long> phaseChanged,
        CancellationToken cancellationToken)
    {
        int balancedMs = options.GetInt("balanced-ms", 750);
        int extremeMs = options.GetInt("extreme-ms", 4250);
        var runner = new CycleRunner(controller, logger, phaseChanged, options.Verbose);
        await runner.RunCycleAsync(
            new CycleOptions(balancedMs, extremeMs, options.FinalExtreme),
            cancellationToken);
        return 0;
    }

    private static async Task<int> RunExtremeOnlyAsync(
        HeadlessSmuController controller,
        IEventLogger logger,
        CliOptions options,
        Action<string, long, long> phaseChanged,
        CancellationToken cancellationToken)
    {
        int interval = options.GetInt("interval", 5000);
        var runner = new CycleRunner(controller, logger, phaseChanged, options.Verbose);
        await runner.RunExtremeOnlyAsync(interval, options.FinalExtreme, null, cancellationToken);
        return 0;
    }

    private static bool IsWriteCommand(string command) => command is
        "balanced" or "extreme" or "apply-command" or "cycle" or "daemon" or
        "watch-extreme" or "extreme-only";

    private static NativeTrayIcon? CreateTrayIcon(
        CliOptions options,
        IEventLogger logger,
        CancellationTokenSource cancellation)
    {
        if (!options.Tray || options.Command is not ("cycle" or "daemon" or "watch-extreme" or "extreme-only"))
            return null;

        try
        {
            return new NativeTrayIcon(cancellation.Cancel);
        }
        catch (Exception ex)
        {
            logger.Emit("warning", new
            {
                message = "The workaround will continue without a notification-area icon.",
                error = ex.Message
            });
            return null;
        }
    }

    private static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void RegisterUnhandledExceptionLogging(IEventLogger logger)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            logger.Emit("unhandled_exception", new { error = eventArgs.ExceptionObject?.ToString() });

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            logger.Emit("unobserved_task_exception", new { error = eventArgs.Exception.ToString() });
            eventArgs.SetObserved();
        };
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            MSIThrottleFix - deterministic Dragon Range SMU preset helper

            Usage:
              MSIThrottleFix.exe info [--dry-run]
              MSIThrottleFix.exe balanced [--dry-run] [--force] [--verbose]
              MSIThrottleFix.exe extreme [--dry-run] [--force] [--verbose]
              MSIThrottleFix.exe cycle [--balanced-ms 750] [--extreme-ms 4250]
                                       [--tray] [--hidden] [--dry-run] [--force]
                                       [--verbose] [--no-final-extreme]
              MSIThrottleFix.exe watch-extreme [--interval 5000]
                                              [--tray] [--hidden] [--dry-run] [--force]
                                              [--verbose] [--no-final-extreme]
              MSIThrottleFix.exe apply-command <name> <value> [--dry-run] [--force] [--verbose]

            Diagnostic command names:
              stapm, fast, slow, tdc, edc, tctl, chtc, stapm-time, slow-time

            All write modes require Administrator privileges unless --dry-run is used.
            """);
    }
}
