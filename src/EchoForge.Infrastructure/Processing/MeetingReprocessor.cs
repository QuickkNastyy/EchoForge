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
}
