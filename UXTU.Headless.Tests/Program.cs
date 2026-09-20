using System.Collections.Concurrent;
using System.Diagnostics;
using UXTU.Headless;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Balanced values", TestBalancedValues),
    ("Extreme values", TestExtremeValues),
    ("Exact command order", TestCommandOrder),
    ("Default timing", TestDefaultTiming),
    ("Cancellation", TestCancellation),
    ("Failure propagation", TestFailurePropagation),
    ("CPU guard", TestCpuGuard),
    ("No overlapping calls", TestNoOverlap),
    ("No concurrent mailbox calls", TestNoConcurrentMailboxes)
};

int failed = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL: {name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT: {tests.Length - failed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static Task TestBalancedValues()
{
    Equal(95u, Presets.Balanced.Tctl);
    Equal(95u, Presets.Balanced.Chtc);
    Equal(65_000u, Presets.Balanced.Stapm);
    Equal(75_000u, Presets.Balanced.Fast);
    Equal(64u, Presets.Balanced.StapmTime);
    Equal(65_000u, Presets.Balanced.Slow);
    Equal(128u, Presets.Balanced.SlowTime);
    Equal(180_000u, Presets.Balanced.Tdc);
    Equal(180_000u, Presets.Balanced.Edc);
    return Task.CompletedTask;
}

static Task TestExtremeValues()
{
    Equal(95u, Presets.Extreme.Tctl);
    Equal(95u, Presets.Extreme.Chtc);
    Equal(125_000u, Presets.Extreme.Stapm);
    Equal(145_000u, Presets.Extreme.Fast);
    Equal(64u, Presets.Extreme.StapmTime);
    Equal(125_000u, Presets.Extreme.Slow);
    Equal(128u, Presets.Extreme.SlowTime);
    Equal(240_000u, Presets.Extreme.Tdc);
    Equal(240_000u, Presets.Extreme.Edc);
    return Task.CompletedTask;
}

static Task TestCommandOrder()
{
    IReadOnlyList<SmuCommand> plan = DragonRangeAm5CommandTable.BuildPresetPlan(Presets.Balanced);
    string[] expected =
    [
        "tctl:Mp1:3F", "tctl:Rsmu:59", "chtc:Rsmu:59", "stapm:Mp1:4F",
        "fast:Mp1:3E", "stapm-time:Mp1:53", "slow:Mp1:5F", "slow:Rsmu:CB",
        "slow-time:Mp1:60", "tdc:Mp1:3C", "tdc:Rsmu:57", "edc:Mp1:3D", "edc:Rsmu:58"
    ];
    string[] actual = plan.Select(c => $"{c.Name}:{c.Mailbox}:{c.Message:X2}").ToArray();
    SequenceEqual(expected, actual);
    return Task.CompletedTask;
}

static async Task TestDefaultTiming()
{
    using var logger = new RecordingLogger();
    using var backend = new DryRunSmuBackend();
    var controller = new HeadlessSmuController(backend, logger, false);
    var runner = new CycleRunner(controller, logger);
    var stopwatch = Stopwatch.StartNew();

    await runner.RunCycleAsync(new CycleOptions(750, 4250, FinalExtreme: false, MaxCycles: 1),
        CancellationToken.None);
    stopwatch.Stop();

    var phases = logger.Events.Where(e => e.EventName == "phase").ToArray();
    Equal(2, phases.Length);
    long phaseDelta = phases[1].TimestampMilliseconds - phases[0].TimestampMilliseconds;
    InRange(phaseDelta, 650, 1_250, "Balanced phase delay");
    InRange(stopwatch.ElapsedMilliseconds, 4_850, 5_750, "Full cycle duration");
}

static async Task TestCancellation()
{
    using var logger = new NullEventLogger();
    using var backend = new DryRunSmuBackend();
    var controller = new HeadlessSmuController(backend, logger, false);
    var runner = new CycleRunner(controller, logger);
    using var cancellation = new CancellationTokenSource(75);
    var stopwatch = Stopwatch.StartNew();

    await runner.RunCycleAsync(new CycleOptions(5_000, 5_000, FinalExtreme: false), cancellation.Token);
    stopwatch.Stop();
    True(stopwatch.ElapsedMilliseconds < 1_000, "Cancellation did not stop the wait promptly.");
}

static Task TestFailurePropagation()
{
    using var logger = new NullEventLogger();
    using var backend = new DryRunSmuBackend
    {
        ResultFactory = command => command.Name == "fast"
            ? SmuSendResult.Failure("injected failure", "test")
            : SmuSendResult.Success()
    };
    var controller = new HeadlessSmuController(backend, logger, false);
    ApplySummary result = controller.ApplyPreset(Presets.Balanced);

    Equal(13, result.Attempted);
    Equal(12, result.Succeeded);
    Equal(1, result.Failed);
    Equal(1L, controller.HardwareWriteFailures);
    Equal(13, backend.Calls.Count);
    return Task.CompletedTask;
}

static Task TestCpuGuard()
{
    var expected = new CpuInfo("AMD Ryzen 9 7945HX", "", 25, 97, 2, RyzenFamily.DragonRange);
    var wrongModel = new CpuInfo("AMD Ryzen 9 7940HS", "", 25, 116, 1, RyzenFamily.Unknown);
    var otherDragon = new CpuInfo("AMD Ryzen 9 7845HX", "", 25, 97, 2, RyzenFamily.DragonRange);

    True(CpuDetector.EvaluateGuard(expected, false, false).Allowed, "Expected CPU was rejected.");
    True(!CpuDetector.EvaluateGuard(wrongModel, false, false).Allowed, "Unknown CPU was accepted.");
    True(!CpuDetector.EvaluateGuard(otherDragon, false, false).Allowed, "Non-7945HX was accepted.");
    True(CpuDetector.EvaluateGuard(wrongModel, true, false).Allowed, "--force did not override guard.");
    True(CpuDetector.EvaluateGuard(wrongModel, false, true).Allowed, "Dry-run did not bypass guard.");
    return Task.CompletedTask;
}

static Task TestNoOverlap()
{
    using var logger = new NullEventLogger();
    using var backend = new ConcurrencyCheckingBackend();
    var controller = new HeadlessSmuController(backend, logger, false);
    controller.ApplyPreset(Presets.Extreme);
    Equal(1, backend.MaxConcurrentCalls);
    Equal(13, backend.Calls.Count);
    return Task.CompletedTask;
}

static Task TestNoConcurrentMailboxes()
{
    using var logger = new NullEventLogger();
    using var backend = new ConcurrencyCheckingBackend();
    var controller = new HeadlessSmuController(backend, logger, false);
    controller.ApplyDiagnostic("slow", 65_000);
    SequenceEqual(new[] { SmuMailbox.Mp1, SmuMailbox.Rsmu }, backend.Calls.Select(c => c.Mailbox));
    Equal(1, backend.MaxConcurrentCalls);
    return Task.CompletedTask;
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
        throw new InvalidOperationException(
            $"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
}

static void InRange(long actual, long minimum, long maximum, string label)
{
    if (actual < minimum || actual > maximum)
        throw new InvalidOperationException($"{label} expected {minimum}-{maximum} ms, got {actual} ms.");
}

sealed class ConcurrencyCheckingBackend : ISmuBackend
{
    private int _active;
    public int MaxConcurrentCalls { get; private set; }
    public ConcurrentQueue<SmuCommand> Calls { get; } = new();
    public bool IsInitialized => true;
    public bool ModuleExists => true;
    public string ModulePath => "mock";
    public string InitializationError => string.Empty;

    public SmuSendResult Send(SmuCommand command)
    {
        int active = Interlocked.Increment(ref _active);
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, active);
        try
        {
            Calls.Enqueue(command);
            Thread.Sleep(5);
            return SmuSendResult.Success();
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public void Dispose() { }
}

sealed class RecordingLogger : IEventLogger
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    public List<(string EventName, long TimestampMilliseconds)> Events { get; } = [];
    public void Emit(string eventName, object? data = null, bool console = true) =>
        Events.Add((eventName, _stopwatch.ElapsedMilliseconds));
    public void Dispose() { }
}
