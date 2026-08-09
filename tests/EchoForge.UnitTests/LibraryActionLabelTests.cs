using EchoForge.App.Library;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Processing;

namespace EchoForge.UnitTests;

/// <summary>
/// What the library's reprocessing actions say, and when they are allowed to run.
///
/// <para>
/// Two rules the first hands-on use got wrong. A recording is reachable from the library whether or
/// not this machine can transcribe, and the very first transcription is <c>Transcribe</c>, never
/// <c>Transcribe again</c> — a small wording lie is exactly what makes a workflow feel like it is
/// hiding state.
/// </para>
/// </summary>
public sealed class LibraryActionLabelTests : IDisposable
{
    private readonly LibraryFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private sealed class FakeReprocessor : IMeetingReprocessor
    {
        public bool CanTranscribe { get; set; }

        public bool CanSummarize { get; set; }

        public bool BlockProcessing { get; set; }

        public bool Cancelled { get; private set; }

        public void Cancel() => Cancelled = true;

        public Task<ReprocessOutcome> TranscribeAgainAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReprocessOutcome(true, "transcribed", "done", 1));

        public Task<ReprocessOutcome> SummarizeAgainAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReprocessOutcome(true, "summarized", "done", 1));

        public async Task<ReprocessOutcome> ProcessMeetingAsync(
            string sessionId,
            IProgress<string>? stage = null,
            CancellationToken cancellationToken = default)
        {
            if (BlockProcessing)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ReprocessOutcome(true, "processed", "processed", 1);
        }
    }

    private async Task<LibraryViewModel> OpenLibraryAsync(IMeetingReprocessor? reprocessor)
    {
        LibraryViewModel library = new(
            _fixture.NewIndex(),
            _fixture.Projection,
            _fixture.Transcripts,
            _fixture.Summaries,
            _fixture.Aliases,
            new LibraryServices { Reprocessor = reprocessor });

        await library.InitializeAsync();
        return library;
    }

    [Fact]
    public async Task AMeetingIsReachableWithNoProcessingRuntime()
    {
        _fixture.AddSession("01JA", "Kickoff");

        // A library composed with a reprocessor that cannot do anything yet — the "no worker" case.
        using LibraryViewModel library = await OpenLibraryAsync(new LiveReprocessor(() => null));

        Assert.Single(library.Meetings);

        library.SelectedMeeting = library.Meetings[0];

        // The recording opens and is readable regardless of whether it can be transcribed.
        Assert.NotNull(library.OpenMeeting);
        Assert.Equal("Kickoff", library.OpenMeeting!.Title);
    }

    [Fact]
    public async Task AFreshRecordingSaysTranscribeNotTranscribeAgain()
    {
        _fixture.AddSession("01JA", "Kickoff");

        using LibraryViewModel library = await OpenLibraryAsync(new FakeReprocessor { CanTranscribe = true });
        library.SelectedMeeting = library.Meetings[0];

        Assert.False(library.OpenMeeting!.HasTranscript);
        Assert.Equal("Transcribe", library.TranscribeActionLabel);
        Assert.Equal("Generate summary", library.SummarizeActionLabel);
    }

    [Fact]
    public async Task AMeetingWithATranscriptOffersToDoItAgain()
    {
        _fixture.AddSession("01JA", "Kickoff");
        _fixture.AddTranscript("01JA", ("microphone", "Hello everyone"));

        using LibraryViewModel library = await OpenLibraryAsync(new FakeReprocessor { CanTranscribe = true });
        library.SelectedMeeting = library.Meetings[0];

        Assert.True(library.OpenMeeting!.HasTranscript);
        Assert.False(library.OpenMeeting.HasSummary);

        // Transcript exists, so transcription is "again"; the summary is still its first run.
        Assert.Equal("Transcribe again", library.TranscribeActionLabel);
        Assert.Equal("Generate summary", library.SummarizeActionLabel);
    }

    [Fact]
    public async Task AMeetingWithASummaryOffersToGenerateItAgain()
    {
        _fixture.AddSession("01JA", "Kickoff");
        _fixture.AddTranscript("01JA", ("microphone", "Ship on Friday"));
        _fixture.AddSummary("01JA", 1, decisions: [("Ship on Friday", "segment-000001")]);

        using LibraryViewModel library = await OpenLibraryAsync(new FakeReprocessor { CanTranscribe = true, CanSummarize = true });
        library.SelectedMeeting = library.Meetings[0];

        Assert.True(library.OpenMeeting!.HasSummary);
        Assert.Equal("Transcribe again", library.TranscribeActionLabel);
        Assert.Equal("Generate summary again", library.SummarizeActionLabel);
    }

    [Fact]
    public async Task CancelProcessingCancelsTheTokenAndTheActiveReprocessor()
    {
        _fixture.AddSession("01JCANCEL", "Long processing");
        FakeReprocessor reprocessor = new() { CanTranscribe = true, CanSummarize = true, BlockProcessing = true };
        using LibraryViewModel library = await OpenLibraryAsync(reprocessor);
        library.SelectedMeeting = library.Meetings[0];

        library.ProcessMeetingCommand.Execute(null);
        Assert.True(SpinWait.SpinUntil(() => library.IsProcessing, TimeSpan.FromSeconds(2)));
        Assert.True(library.CancelProcessingCommand.CanExecute(null));

        library.CancelProcessingCommand.Execute(null);

        Assert.True(reprocessor.Cancelled);
        Assert.False(library.CancelProcessingCommand.CanExecute(null));
        Assert.True(SpinWait.SpinUntil(() => !library.IsProcessing, TimeSpan.FromSeconds(2)));
        Assert.Contains("cancelled", library.Status ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReprocessingLightsUpWhenARuntimeArrivesWithoutReopeningTheLibrary()
    {
        _fixture.AddSession("01JA", "Kickoff");

        // The runtime is not there when the library opens…
        FakeReprocessor? live = null;
        using LibraryViewModel library = await OpenLibraryAsync(new LiveReprocessor(() => live));
        library.SelectedMeeting = library.Meetings[0];

        Assert.False(library.TranscribeAgainCommand.CanExecute(null));

        // …then Setup installs one and the composition root swaps it in and rings the bell.
        live = new FakeReprocessor { CanTranscribe = true, CanSummarize = true };
        library.ProcessingAvailabilityChanged();

        Assert.True(library.TranscribeAgainCommand.CanExecute(null));
        Assert.True(library.SummarizeAgainCommand.CanExecute(null));
    }
}
