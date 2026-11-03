using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace TaskbarTemp;

internal sealed class FakeNpuQuery(string name, float? result = null, Exception error = null)
{
    internal readonly string Name = name;
    internal readonly float? Result = result;
    internal readonly Exception Error = error;
    internal readonly ManualResetEventSlim Started = new(false);
    internal readonly ManualResetEventSlim Release = new(false);
}

internal static class NpuRegressionFake
{
    private static readonly ConcurrentQueue<FakeNpuQuery> Queries = new();
    private static readonly ConcurrentBag<FakeNpuQuery> AllQueries = new();
    private static readonly ManualResetEventSlim CompletionGateEntered = new(false);
    private static readonly ManualResetEventSlim CompletionGateRelease = new(true);
    private static int _calls, _active, _maxActive, _hardwareSamples, _hardwareCloses, _pauseNextCompletion;

    internal static int Calls => Volatile.Read(ref _calls);
    internal static int Active => Volatile.Read(ref _active);
    internal static int MaxActive => Volatile.Read(ref _maxActive);
    internal static int HardwareSamples => Volatile.Read(ref _hardwareSamples);
    internal static int HardwareCloses => Volatile.Read(ref _hardwareCloses);
    internal static bool CompletionGateWasEntered => CompletionGateEntered.IsSet;

    internal static void Queue(FakeNpuQuery query)
    {
        AllQueries.Add(query);
        Queries.Enqueue(query);
    }

    internal static float? Read()
    {
        int call = Interlocked.Increment(ref _calls);
        int active = Interlocked.Increment(ref _active);
        int previous;
        while (active > (previous = Volatile.Read(ref _maxActive)) &&
               Interlocked.CompareExchange(ref _maxActive, active, previous) != previous) { }

        try
        {
            if (!Queries.TryDequeue(out var query))
                throw new InvalidOperationException("No fake NPU query was queued.");
            query.Started.Set();
            if (!query.Release.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Fake NPU query timed out: " + query.Name);
            if (query.Error != null) throw query.Error;
            return query.Result;
        }
        finally { Interlocked.Decrement(ref _active); }
    }

    internal static void HardwareSample() => Interlocked.Increment(ref _hardwareSamples);
    internal static void HardwareClose() => Interlocked.Increment(ref _hardwareCloses);

    internal static void PauseNextCompletionDecision()
    {
        CompletionGateEntered.Reset();
        CompletionGateRelease.Reset();
        Volatile.Write(ref _pauseNextCompletion, 1);
    }

    internal static void BeforeCompletionDecision()
    {
        if (Interlocked.Exchange(ref _pauseNextCompletion, 0) == 0) return;
        CompletionGateEntered.Set();
        if (!CompletionGateRelease.Wait(TimeSpan.FromSeconds(8)))
            throw new TimeoutException("Completion lock test gate timed out.");
    }

    internal static void ReleaseCompletionDecision() => CompletionGateRelease.Set();

    internal static void ReleaseAll()
    {
        CompletionGateRelease.Set();
        foreach (var query in AllQueries) query.Release.Set();
    }
}

internal static class NpuRegression
{
    private static readonly List<string> Checks = new();
    internal static bool Passed { get; private set; }

    internal static void Start(TrayContext context)
    {
        var start = new System.Windows.Forms.Timer { Interval = 1 };
        start.Tick += async (sender, args) =>
        {
            start.Stop();
            start.Dispose();
            try
            {
                await RunAsync(context);
                Passed = true;
                Write("RESULT=PASS" + Environment.NewLine + string.Join(Environment.NewLine, Checks));
            }
            catch (Exception ex)
            {
                Write("RESULT=FAIL" + Environment.NewLine + ex);
            }
            finally
            {
                NpuRegressionFake.ReleaseAll();
                if (!(bool)GetField(context, "_disposed")) Invoke(context, "ExitApp");
            }
        };
        start.Start();
    }

    private static async Task RunAsync(TrayContext context)
    {
        var hardwareTimer = (System.Windows.Forms.Timer)GetField(context, "_timer");
        var npuTimer = (System.Windows.Forms.Timer)GetField(context, "_npuTimer");
        hardwareTimer.Interval = 45;
        npuTimer.Interval = 15;

        var initial = new FakeNpuQuery("initial sample", 11f);
        NpuRegressionFake.Queue(initial);
        Invoke(context, "ShowDetails");
        await WaitFor(() => initial.Started.IsSet, "initial NPU sample");
        npuTimer.Stop();
        initial.Release.Set();
        await WaitFor(() => GetNpuUsage(context) == 11f, "initial NPU value");
        Check(GetNpuUsage(context) == 11f, "initial value applied");

        var oldGeneration = new FakeNpuQuery("held old generation", 22f);
        NpuRegressionFake.Queue(oldGeneration);
        Invoke(context, "RequestNpuSample");
        await WaitFor(() => oldGeneration.Started.IsSet, "held old-generation query");
        int hardwareBeforeHold = NpuRegressionFake.HardwareSamples;
        npuTimer.Start();
        await Task.Delay(220);
        Check(NpuRegressionFake.Calls == 2 && NpuRegressionFake.Active == 1,
            "repeated NPU timer ticks did not queue another query");
        Check(NpuRegressionFake.MaxActive == 1, "NPU queries never overlapped");
        Check(NpuRegressionFake.HardwareSamples >= hardwareBeforeHold + 2,
            "ordinary hardware samples continued during a blocked NPU query");

        var details = (Form)GetField(context, "_details");
        details.Hide();
        var reopened = new FakeNpuQuery("new generation after reopen", 73f);
        NpuRegressionFake.Queue(reopened);
        Invoke(context, "ShowDetails");
        npuTimer.Stop();
        Check(GetNpuUsage(context) == null, "reopening details cleared the old NPU value");
        await Task.Delay(80);
        Check(NpuRegressionFake.Calls == 2 && NpuRegressionFake.Active == 1,
            "reopen kept the outstanding query serialized");

        NpuRegressionFake.PauseNextCompletionDecision();
        oldGeneration.Release.Set();
        await WaitFor(() => NpuRegressionFake.CompletionGateWasEntered, "completion lock gate");
        using var contenderStarted = new ManualResetEventSlim(false);
        Task contender = Task.Run(() =>
        {
            contenderStarted.Set();
            Invoke(context, "RequestNpuSample");
        });
        await WaitFor(() => contenderStarted.IsSet, "competing timer request");
        await Task.Delay(80);
        Check(!contender.IsCompleted, "new-generation request waited for the completion lock");
        NpuRegressionFake.ReleaseCompletionDecision();
        await contender;
        await WaitFor(() => reopened.Started.IsSet, "new-generation follow-up query");
        Check(NpuRegressionFake.Calls == 3 && NpuRegressionFake.MaxActive == 1,
            "old completion and competing request produced exactly one sequential follow-up");
        Check(GetNpuUsage(context) == null, "old-generation result was discarded");
        reopened.Release.Set();
        await WaitFor(() => GetNpuUsage(context) == 73f, "new-generation NPU value");
        Check(GetNpuUsage(context) == 73f, "new-generation follow-up value applied");

        var failure = new FakeNpuQuery("controlled query exception", error: new InvalidOperationException("fake failure"));
        NpuRegressionFake.Queue(failure);
        Invoke(context, "RequestNpuSample");
        await WaitFor(() => failure.Started.IsSet, "exception query");
        failure.Release.Set();
        await WaitFor(() => NpuRegressionFake.Active == 0 && GetWorkerRunning(context) == 0,
            "exception cleanup");
        Check(NpuRegressionFake.Calls == 4, "failed query completed and released the worker gate");

        var retry = new FakeNpuQuery("retry after exception", 88f);
        NpuRegressionFake.Queue(retry);
        Invoke(context, "RequestNpuSample");
        await WaitFor(() => retry.Started.IsSet, "retry query");
        retry.Release.Set();
        await WaitFor(() => GetNpuUsage(context) == 88f, "retry value");
        Check(NpuRegressionFake.Calls == 5 && GetWorkerRunning(context) == 0,
            "a later query succeeded after an exception");

        var heldDuringExit = new FakeNpuQuery("held during exit", 99f);
        NpuRegressionFake.Queue(heldDuringExit);
        Invoke(context, "RequestNpuSample");
        await WaitFor(() => heldDuringExit.Started.IsSet, "held query before exit");
        int hardwareBeforeExit = NpuRegressionFake.HardwareSamples;
        await Task.Delay(120);
        Check(NpuRegressionFake.HardwareSamples > hardwareBeforeExit,
            "ordinary hardware sampling continued while the final NPU query was held");
        Invoke(context, "ExitApp");
        Check(context.WaitForShutdown(TimeSpan.FromSeconds(3)),
            "hardware shutdown completed without waiting for the blocked NPU query");
        Check(NpuRegressionFake.HardwareCloses == 1, "fake hardware close ran during shutdown");
        Check(NpuRegressionFake.Active == 1 && GetWorkerRunning(context) == 1,
            "the NPU query was still blocked when shutdown completed");
        Check(NpuRegressionFake.Calls == 6 && NpuRegressionFake.MaxActive == 1,
            "exit did not create an extra NPU query");
    }

    private static async Task WaitFor(Func<bool> condition, string label)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(5))
                throw new TimeoutException("Timed out waiting for " + label);
            await Task.Delay(10);
        }
    }

    private static object GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

    private static float? GetNpuUsage(object target) => (float?)GetField(target, "_npuUsage");
    private static int GetWorkerRunning(object target) => (int)GetField(target, "_npuWorkerRunning");

    private static void Invoke(object target, string name) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + label);
        Checks.Add("PASS " + label);
    }

    private static void Write(string report)
    {
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "NpuRegressionResult.txt"),
            report + Environment.NewLine);
    }
}
