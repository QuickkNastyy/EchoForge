using EchoForge.Contracts.Processing;
using EchoForge.Contracts.Summaries;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.Infrastructure.Processing;

/// <summary>How one reprocessing request from the library ended.</summary>
public sealed record ReprocessOutcome(bool Succeeded, string Code, string Message, int? Revision)
{
    public static ReprocessOutcome Refused(string code, string message) => new(false, code, message, null);
}

/// <summary>
/// Running transcription or summarisation again on a meeting the user is already looking at.
///
/// <para>
/// A seam, so the library window can offer the action without knowing anything about workers,
/// leases or GPU contention, and so the view model that offers it can be tested without launching
/// a Python process.
/// </para>
/// </summary>
public interface IMeetingReprocessor
{
    bool CanTranscribe { get; }

    bool CanSummarize { get; }

    Task<ReprocessOutcome> TranscribeAgainAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<ReprocessOutcome> SummarizeAgainAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole of processing, as one action.
    ///
    /// <para>
    /// Recording to brief is one thing a person wants, and it used to be two buttons they had to
    /// press in the right order, with a model picker beside each. Both halves still run exactly as
    /// they did — same coordinators, same revisions, same refusals — but the sequence is here
    /// rather than in the user's head, and the settings it uses were chosen once in Settings
    /// instead of again for every meeting.
    /// </para>
    /// </summary>
    /// <param name="stage">
    /// Told what is happening in words a person recognises, so an hour-long meeting does not look
    /// like a stalled progress bar.
    /// </param>
    Task<ReprocessOutcome> ProcessMeetingAsync(
        string sessionId,
        IProgress<string>? stage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation of the currently running heavy work, if any.
    ///
    /// The caller token remains the normal ownership boundary. This explicit hook exists because
    /// worker/coordinator cancellation also needs to wake processes that may be blocked outside a
    /// managed await; implementations without such a worker can safely do nothing.
    /// </summary>
    void Cancel() { }
}

/// <summary>
/// A reprocessor that reads whatever reprocessor currently exists, or none.
///
/// <para>
/// The library is composed at startup, before it is known whether this machine can run a worker at
/// all: a recording must be reachable, playable and searchable with nothing installed. Reprocessing,
/// on the other hand, only becomes possible once a runtime is present, which may be after Setup runs.
/// This indirection lets the library hold one stable reprocessor whose <em>capabilities</em> follow
/// the runtime, so transcribing again lights up the moment the coordinators attach — without a
/// second library, and without a restart.
/// </para>
/// </summary>
public sealed class LiveReprocessor(Func<IMeetingReprocessor?> current) : IMeetingReprocessor
{
    private readonly Func<IMeetingReprocessor?> _current =
        current ?? throw new ArgumentNullException(nameof(current));

    public bool CanTranscribe => _current()?.CanTranscribe ?? false;

    public bool CanSummarize => _current()?.CanSummarize ?? false;

    public Task<ReprocessOutcome> TranscribeAgainAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _current() is { } reprocessor
            ? reprocessor.TranscribeAgainAsync(sessionId, cancellationToken)
            : Task.FromResult(ReprocessOutcome.Refused(
                "unavailable", "Transcription is not set up on this machine yet. Open Setup to install it."));

    public Task<ReprocessOutcome> SummarizeAgainAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _current() is { } reprocessor
            ? reprocessor.SummarizeAgainAsync(sessionId, cancellationToken)
            : Task.FromResult(ReprocessOutcome.Refused(
                "unavailable", "Summarisation is not set up on this machine yet. Open Setup to install it."));

    public Task<ReprocessOutcome> ProcessMeetingAsync(
        string sessionId,
        IProgress<string>? stage = null,
        CancellationToken cancellationToken = default) =>
        _current() is { } reprocessor
            ? reprocessor.ProcessMeetingAsync(sessionId, stage, cancellationToken)
            : Task.FromResult(ReprocessOutcome.Refused(
                "unavailable",
                "Processing is not set up on this machine yet. Settings → Models & runtime can install what it needs."));

    public void Cancel() => _current()?.Cancel();
}

/// <summary>
/// Reprocessing through the coordinators that already own it.
///
/// <para>
/// <b>Nothing about the processing lifecycle is reimplemented here.</b> Recording still outranks
/// everything, only one heavy job still runs at a time, revisions are still allocated before work
/// starts and activated only after validation, and every refusal still comes from the coordinator
/// that made it. This class does two things the coordinators deliberately do not: it waits for the
/// answer, and it selects the revision that resulted.
/// </para>
///
/// <para>
/// <b>Selecting the new transcript is the point of transcribing again.</b> Somebody who explicitly
/// chose an older revision earlier would otherwise get a new one that sits there unselected, and
/// the summary written from the old transcript would never be marked stale — which is precisely the
/// signal that a re-transcribed meeting needs a re-generated summary.
/// </para>
/// </summary>
public sealed class CoordinatorReprocessor(
    TranscriptionCoordinator? transcription,
    SummaryCoordinator? summaries,
    Func<TranscriptionOptions>? transcriptionOptions = null,
    Func<SummaryOptions>? summaryOptions = null) : IMeetingReprocessor
{
    public bool CanTranscribe => transcription is not null;

    public bool CanSummarize => summaries is not null;

    public void Cancel()
    {
        transcription?.Cancel();
        summaries?.Cancel();
    }

    public async Task<ReprocessOutcome> TranscribeAgainAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (transcription is null)
        {
            return ReprocessOutcome.Refused(
                "unavailable", "This installation cannot transcribe, so there is nothing to run again.");
        }

        TranscriptionTicket ticket = await Task
            .Run(() => transcription.Request(sessionId, transcriptionOptions?.Invoke()), cancellationToken)
            .ConfigureAwait(false);

        if (!ticket.Accepted)
        {
            // Busy, or a session that cannot be processed. Either way the coordinator's own
            // sentence is the one the user sees; inventing a second wording here would eventually
            // disagree with it.
            return ReprocessOutcome.Refused(
                ticket.Acceptance == TranscriptionAcceptance.Busy ? "busy" : "rejected", ticket.Message);
        }

        await using CancellationTokenRegistration _ = cancellationToken.Register(transcription.Cancel);

        TranscriptionRunResult result = await ticket.Completion.ConfigureAwait(false);

        if (!result.Succeeded || result.Revision is not { } revision)
        {
            return new ReprocessOutcome(false, result.FailureCode ?? "failed", result.Message, null);
        }

        transcription.SelectRevision(sessionId, revision);

        return new ReprocessOutcome(true, "transcribed", result.Message, revision);
    }

    public async Task<ReprocessOutcome> SummarizeAgainAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (summaries is null)
        {
            return ReprocessOutcome.Refused(
                "unavailable", "This installation cannot summarise, so there is nothing to run again.");
        }

        // The coordinator reads the selected transcript revision itself, which is what makes a
        // summary generated from the library come from the transcript the reader is looking at.
        SummaryRunResult result = await Task
            .Run(() => summaries.SummarizeAsync(sessionId, summaryOptions?.Invoke(), cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded || result.Revision is not { } revision)
        {
            return new ReprocessOutcome(false, result.FailureCode ?? "failed", result.Message, null);
        }

        summaries.SelectRevision(sessionId, revision);

        return new ReprocessOutcome(true, "summarized", result.Message, revision);
    }

    /// <summary>
    /// Transcribe, then summarise, and stop at the first thing that refuses.
    ///
    /// <para>
    /// Stopping matters. A summary written from a transcript that failed halfway would be a
    /// confident document about a fraction of a meeting, and nothing on it would say so. If the
    /// transcript does not activate, there is nothing to summarise and the run says why.
    /// </para>
    /// </summary>
    public async Task<ReprocessOutcome> ProcessMeetingAsync(
        string sessionId,
        IProgress<string>? stage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        stage?.Report("Preparing audio");
        ReprocessOutcome transcribed = await TranscribeAgainAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!transcribed.Succeeded)
        {
            return transcribed;
        }

        if (summaries is null)
        {
            // A machine that can transcribe but not summarise has still done half the work, and
            // saying so is better than reporting a failure over a transcript that exists.
            return new ReprocessOutcome(
                true,
                "transcribed",
                transcribed.Message + " No summary model is installed, so no brief was written.",
                transcribed.Revision);
        }

        stage?.Report("Reading the meeting");
        ReprocessOutcome summarized = await SummarizeAgainAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!summarized.Succeeded)
        {
            return new ReprocessOutcome(
                false,
                summarized.Code,
                $"The transcript is ready, but the brief was not written: {summarized.Message}",
                transcribed.Revision);
        }

        stage?.Report("Done");
        return new ReprocessOutcome(true, "processed", summarized.Message, summarized.Revision);
    }
}
