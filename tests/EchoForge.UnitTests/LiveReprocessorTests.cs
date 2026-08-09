using EchoForge.Infrastructure.Processing;

namespace EchoForge.UnitTests;

/// <summary>
/// The seam that lets the library be composed before anyone knows whether this machine can process
/// anything, and light its actions up later without a restart.
///
/// <para>
/// The behaviour that matters is that capability <i>follows</i> the runtime: empty means a clear
/// refusal rather than a crash, and the moment a real reprocessor exists the same object starts
/// delegating to it. This is exactly what makes "Transcribe again" work after Setup installs a
/// worker into a running application.
/// </para>
/// </summary>
public sealed class LiveReprocessorTests
{
    /// <summary>A reprocessor that records what it was asked, standing in for the coordinators.</summary>
    private sealed class SpyReprocessor : IMeetingReprocessor
    {
        public bool CanTranscribe { get; init; } = true;

        public bool CanSummarize { get; init; } = true;

        public string? Transcribed { get; private set; }

        public string? Summarized { get; private set; }

        public bool Cancelled { get; private set; }

        public void Cancel() => Cancelled = true;

        public Task<ReprocessOutcome> TranscribeAgainAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Transcribed = sessionId;
            return Task.FromResult(new ReprocessOutcome(true, "transcribed", "done", 1));
        }

        public Task<ReprocessOutcome> SummarizeAgainAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Summarized = sessionId;
            return Task.FromResult(new ReprocessOutcome(true, "summarized", "done", 1));
        }

    public Task<ReprocessOutcome> ProcessMeetingAsync(
        string sessionId,
        IProgress<string>? stage = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReprocessOutcome(true, "processed", "processed", 1));
}

    [Fact]
    public async Task WithNoRuntimeItRefusesInsteadOfFailing()
    {
        LiveReprocessor reprocessor = new(() => null);

        Assert.False(reprocessor.CanTranscribe);
        Assert.False(reprocessor.CanSummarize);

        ReprocessOutcome transcribe = await reprocessor.TranscribeAgainAsync("01J");
        ReprocessOutcome summarize = await reprocessor.SummarizeAgainAsync("01J");

        Assert.False(transcribe.Succeeded);
        Assert.False(summarize.Succeeded);
        Assert.Equal("unavailable", transcribe.Code);
        Assert.Equal("unavailable", summarize.Code);

        // The refusal points at the one thing that fixes it.
        Assert.Contains("Setup", transcribe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityFollowsTheRuntimeWithoutRebuildingTheSeam()
    {
        SpyReprocessor? live = null;

        // The library holds this one object for its whole life; only what it points at changes.
        LiveReprocessor reprocessor = new(() => live);

        Assert.False(reprocessor.CanTranscribe);

        // A worker attaches — the same object it was composed with now reports it can run.
        live = new SpyReprocessor();

        Assert.True(reprocessor.CanTranscribe);
        Assert.True(reprocessor.CanSummarize);

        ReprocessOutcome outcome = await reprocessor.TranscribeAgainAsync("01JLATE");

        Assert.True(outcome.Succeeded);
        Assert.Equal("01JLATE", live.Transcribed);
    }

    [Fact]
    public void CancelReachesTheCurrentlyAttachedReprocessor()
    {
        SpyReprocessor live = new();
        LiveReprocessor reprocessor = new(() => live);

        reprocessor.Cancel();

        Assert.True(live.Cancelled);
    }

    [Fact]
    public void CapabilityReflectsWhatTheLiveReprocessorCanDo()
    {
        SpyReprocessor partial = new() { CanTranscribe = true, CanSummarize = false };
        LiveReprocessor reprocessor = new(() => partial);

        Assert.True(reprocessor.CanTranscribe);
        Assert.False(reprocessor.CanSummarize);
    }
}
