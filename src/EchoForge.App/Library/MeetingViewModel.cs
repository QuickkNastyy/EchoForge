using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using EchoForge.Contracts.Library;
using EchoForge.Contracts.Summaries;
using EchoForge.Contracts.Transcripts;
using EchoForge.Core.Exports;
using EchoForge.Core.Library;
using EchoForge.Core.ManualCopy;
using EchoForge.Infrastructure.Library;
using EchoForge.Infrastructure.Processing;
using EchoForge.Infrastructure.Summaries;

namespace EchoForge.App.Library;

/// <summary>
/// One line of transcript, as the list shows it.
///
/// <para>
/// A plain record rather than a view model on purpose. A three-hour meeting is tens of thousands
/// of these, and the difference between a record and something with change notification and
/// commands attached is the difference between a list that opens and one that stalls.
/// </para>
/// </summary>
public sealed record TranscriptLine(
    string SegmentId,
    string Timestamp,
    string Speaker,
    string Text,
    double StartSeconds,
    bool IsYou)
{
    /// <summary>True when a search or an evidence click pointed at this line.</summary>
    public bool IsHighlighted { get; init; }
}

/// <summary>
/// One citation, ready to be drawn as a chip.
///
/// <para>
/// Carries the track so the chip can be amber for You and teal for Remote, which is the same rule
/// the ribbon and the transcript follow — a reader should never have to work out whose words are
/// being cited. An unresolved citation says so on its face rather than looking like a link that
/// works.
/// </para>
/// </summary>
public sealed record EvidenceChip(string Label, bool IsYou, bool IsResolved, EvidenceLocation Location);

/// <summary>
/// One rendered summary item, with its citations already resolved.
///
/// <para>
/// What was <em>said</em> and what was merely <em>not said</em> are separate fields rather than one
/// sentence, because the design draws them differently and for good reason: an owner nobody named
/// is a hole in the record, and printing "Owner: unknown" in the same ink as a real name invites a
/// reader to skim past it.
/// </para>
/// </summary>
public sealed record SummaryLine(
    string Section,
    string Text,
    string Certainty,
    IReadOnlyList<EvidenceLocation> Evidence)
{
    /// <summary>The owner, when the meeting actually named one.</summary>
    public string? Owner { get; init; }

    /// <summary>The due date as stated or resolved, when the meeting gave one.</summary>
    public string? Due { get; init; }

    /// <summary>True when this item was inferred rather than stated. Never promoted.</summary>
    public bool IsInferred { get; init; }

    /// <summary>True when the item carries owner and date slots at all — actions do, decisions do not.</summary>
    public bool HasAssignment { get; init; }

    public bool HasOwner => !string.IsNullOrWhiteSpace(Owner);

    public bool HasDue => !string.IsNullOrWhiteSpace(Due);

    /// <summary>Shown in place of an owner: a slot the meeting left empty, drawn as one.</summary>
    public bool OwnerMissing => HasAssignment && !HasOwner;

    public bool DueMissing => HasAssignment && !HasDue;

    public bool HasEvidence => Evidence.Count > 0;

    public IReadOnlyList<EvidenceChip> Chips =>
    [
        .. Evidence.Select(e => new EvidenceChip(
            e.IsResolved ? e.StartSeconds.ToTimestamp() : e.StartSeconds.ToTimestamp() + " · unresolved",
            string.Equals(e.SourceTrack, TranscriptSpeakers.MicrophoneTrack, StringComparison.Ordinal),
            e.IsResolved,
            e))
    ];
}

internal static class TimestampExtensions
{
    public static string ToTimestamp(this double seconds)
    {
        TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");
    }
}

/// <summary>
/// One meeting, opened.
///
/// <para>
/// Reads canonical files directly rather than the index. The index is how a meeting is
/// <i>found</i>; what it contains comes from the revision the user is looking at, so a stale
/// database can never put words on screen that the transcript does not contain.
/// </para>
/// </summary>
public sealed class MeetingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly FileTranscriptionStore _transcripts;
    private readonly FileSummaryStore _summaries;
    private readonly FileSpeakerAliasStore _aliases;

    private LibraryEntry _entry;
    private TranscriptDocument? _transcript;
    private SummaryDocument? _summary;
    private string? _notice;
    private bool _disposed;

    public MeetingViewModel(
        LibraryEntry entry,
        FileTranscriptionStore transcripts,
        FileSummaryStore summaries,
        FileSpeakerAliasStore aliases,
        PlaybackViewModel? playback = null)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _summaries = summaries ?? throw new ArgumentNullException(nameof(summaries));
        _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        Playback = playback;

        Load();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when something canonical about this meeting changed on disk.
    ///
    /// <para>
    /// Selecting a revision and renaming a speaker both change what the library should say about a
    /// meeting, and both happen here rather than where the index lives. The event is how the index
    /// finds out without this class having to know one exists.
    /// </para>
    /// </summary>
    public event EventHandler<string>? CanonicalChanged;

    /// <summary>
    /// The transport for this meeting, or null when playback is not composed.
    ///
    /// <para>
    /// One per opened meeting, owned by the meeting and disposed with it, so there is never a
    /// second transport holding an audio device for a meeting nobody is looking at.
    /// </para>
    /// </summary>
    public PlaybackViewModel? Playback { get; }

    public bool CanPlay => Playback is not null;

    public string SessionId => _entry.SessionId;

    public string Title => _entry.Title;

    public LibraryEntry Entry => _entry;

    /// <summary>
    /// The two-lane conversation shape for the detail timeline, derived from the selected transcript
    /// (already in memory here), so the timeline ribbon costs nothing extra to draw.
    /// </summary>
    public ConversationShape Shape { get; private set; } = ConversationShape.Empty;

    public ObservableCollection<TranscriptLine> Lines { get; } = [];

    public ObservableCollection<SummaryLine> SummaryLines { get; } = [];

    public ObservableCollection<int> TranscriptRevisions { get; } = [];

    public ObservableCollection<int> SummaryRevisions { get; } = [];

    public ObservableCollection<SpeakerRow> Speakers { get; } = [];

    // -- what is shown --------------------------------------------------------------------------

    public int? SelectedTranscriptRevision
    {
        get => _transcript?.TranscriptRevision;
        set
        {
            if (value is not { } revision || revision == _transcript?.TranscriptRevision)
            {
                return;
            }

            // Selecting a revision changes what is read, and nothing else. No summary is
            // rewritten, retracted or re-pointed by looking at a different transcript.
            _transcripts.SelectRevision(SessionId, revision, DateTimeOffset.UtcNow);
            Reload();
            RaiseCanonicalChanged();
        }
    }

    public int? SelectedSummaryRevision
    {
        get => _summary?.SummaryRevision;
        set
        {
            if (value is not { } revision || revision == _summary?.SummaryRevision)
            {
                return;
            }

            _summaries.SelectRevision(SessionId, revision, DateTimeOffset.UtcNow);
            Reload();
            RaiseCanonicalChanged();
        }
    }

    public bool HasTranscript => _transcript is not null;

    public bool HasSummary => _summary is not null;

    /// <summary>
    /// True only when there is a summary in hand and it came from a transcript version that is no
    /// longer selected. Gated on the summary this view model actually read rather than on the index
    /// entry alone, so a warning about a stale summary can never be shown where there is no summary.
    /// </summary>
    public bool SummaryIsStale => HasSummary && _entry.SummaryIsStale;

    // -- following a claim (scene 07) -------------------------------------------------------------

    private string? _evidenceSegmentId;
    private double _evidenceSeconds = -1;

    /// <summary>
    /// The transcript segment the selected claim cites, while one is selected.
    ///
    /// <para>
    /// Held here rather than on each line so that marking one of ten thousand lines costs one
    /// property change instead of rebuilding the list. The transcript rows compare their own
    /// segment against it.
    /// </para>
    /// </summary>
    public string? EvidenceSegmentId
    {
        get => _evidenceSegmentId;
        private set
        {
            _evidenceSegmentId = value;
            Changed();
            Changed(nameof(HasEvidenceFocus));
        }
    }

    public bool HasEvidenceFocus => _evidenceSegmentId is not null;

    /// <summary>Where the cited moment sits along the timeline, 0..1, or negative for none.</summary>
    public double EvidenceFraction => _evidenceSeconds < 0 || TimelineSeconds <= 0
        ? -1
        : Math.Clamp(_evidenceSeconds / TimelineSeconds, 0, 1);

    /// <summary>The cited moment as a timecode, for the transcript header and the transport.</summary>
    public string EvidenceTimestamp => _evidenceSeconds < 0 ? string.Empty : _evidenceSeconds.ToTimestamp();

    /// <summary>
    /// How long the ribbon's ruler runs.
    ///
    /// <para>
    /// The transport knows the exact length of the prepared audio, but it only exists once playback
    /// is composed. Falling back to the transcript's own duration means the ruler is drawn — and
    /// the evidence marker lands in the right place — on a build with no transport at all.
    /// </para>
    /// </summary>
    public double TimelineSeconds =>
        Playback?.DurationSeconds is > 0 and { } playable ? playable : _transcript?.DurationSeconds ?? 0;

    /// <summary>What the transport says it is about to play, in the reader's terms.</summary>
    public string TransportCaption => _evidenceSegmentId is null
        ? "You + Remote · click the ribbon to play from that moment"
        : "Evidence for the selected claim · " + EvidenceTimestamp;

    /// <summary>Marks a claim's evidence, or clears the mark when nothing is selected.</summary>
    public void FocusEvidence(EvidenceLocation? location)
    {
        _evidenceSeconds = location?.StartSeconds ?? -1;
        EvidenceSegmentId = location?.SegmentId;

        Changed(nameof(EvidenceFraction));
        Changed(nameof(EvidenceTimestamp));
        Changed(nameof(TransportCaption));
    }

    /// <summary>
    /// Says which transcript the summary belongs to, rather than only that it is out of date.
    ///
    /// <para>
    /// A stale summary is still correct about the transcript it was written from, and its
    /// evidence still resolves. Presenting it as simply wrong would throw away work that is
    /// perfectly readable.
    /// </para>
    /// </summary>
    public string StaleNotice => SummaryIsStale
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"This summary was written from transcript version {_entry.SummarySourceTranscriptRevision}, and version {_entry.SelectedTranscriptRevision} is selected. It is still accurate about the version it came from. Generate again to bring it up to date.")
        : string.Empty;

    public string TranscriptModelText => _transcript is { } t
        ? string.Create(CultureInfo.CurrentCulture, $"{t.Model.Backend} · {t.Model.ModelId} · version {t.TranscriptRevision} · {Lines.Count} lines")
        : "No transcript yet";

    public string SummaryModelText => _summary is { } s
        ? string.Create(
            CultureInfo.CurrentCulture,
            $"{s.Model.Backend} · {s.Model.ModelId} · version {s.SummaryRevision} · from transcript v{s.TranscriptRevision}{(s.Model.ProducesSummaries ? string.Empty : " · placeholder")}")
        : "No summary yet";

    public string SummaryOverview => _summary?.Overview ?? string.Empty;

    public string? Notice
    {
        get => _notice;
        private set { _notice = value; Changed(); Changed(nameof(HasNotice)); }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    // -- loading ---------------------------------------------------------------------------------

    /// <summary>Re-reads everything from disk, which is always what settles a disagreement.</summary>
    public void Reload(LibraryEntry? refreshed = null)
    {
        if (refreshed is not null)
        {
            _entry = refreshed;
        }

        Load();
    }

    private void Load()
    {
        SpeakerAliases overlay = _aliases.Read(SessionId);

        // Read the selection from the stores rather than from the entry this was opened with.
        // The entry is a snapshot of the index, and the moment a revision is selected it is out
        // of date - trusting it would show the previous transcript and call it the current one.
        Contracts.Processing.TranscriptionState transcription = _transcripts.Read(SessionId);
        SummaryState summaries = _summaries.Read(SessionId);

        _entry = _entry with
        {
            SelectedTranscriptRevision = transcription.SelectedRevision,
            TranscriptRevisions = [.. transcription.Revisions.Where(r => r.FileExists).Select(r => r.Revision).Order()],
            SelectedSummaryRevision = summaries.SelectedRevision,
            SummaryRevisions = [.. summaries.Revisions.Where(r => r.FileExists).Select(r => r.Revision).Order()],
            SummarySourceTranscriptRevision = summaries.Selected?.TranscriptRevision,
        };

        _transcript = _entry.SelectedTranscriptRevision is { } tr
            ? _transcripts.ReadTranscript(SessionId, tr)
            : null;

        _summary = _entry.SelectedSummaryRevision is { } sr
            ? _summaries.ReadSummary(SessionId, sr)
            : null;

        Lines.Clear();
        if (_transcript is { } transcript)
        {
            foreach (TranscriptSegment segment in transcript.Segments)
            {
                Lines.Add(new TranscriptLine(
                    segment.Id,
                    segment.StartSeconds.ToTimestamp(),
                    SpeakerPresentation.Present(segment, overlay),
                    segment.Text,
                    segment.StartSeconds,
                    string.Equals(segment.SpeakerId, TranscriptSpeakers.YouId, StringComparison.Ordinal)));
            }
        }

        Shape = _transcript is { } shapeSource
            ? ConversationShape.FromTranscript(shapeSource)
            : ConversationShape.Empty;

        BuildSummaryLines(overlay);
        BuildRevisionLists();
        BuildSpeakers(overlay);

        foreach (string name in (string[])
        [
            nameof(HasTranscript), nameof(HasSummary), nameof(SummaryIsStale), nameof(StaleNotice),
            nameof(TranscriptModelText), nameof(SummaryModelText), nameof(SummaryOverview),
            nameof(SelectedTranscriptRevision), nameof(SelectedSummaryRevision), nameof(Entry), nameof(Shape),
            nameof(TimelineSeconds), nameof(EvidenceFraction),
        ])
        {
            Changed(name);
        }
    }

    private void BuildSummaryLines(SpeakerAliases overlay)
    {
        SummaryLines.Clear();

        if (_summary is not { } summary)
        {
            return;
        }

        // Citations are resolved against the revision the summary names, loaded separately from
        // whatever is currently selected. That is the whole point of the pair.
        TranscriptDocument? source = _transcripts.ReadTranscript(SessionId, summary.TranscriptRevision);

        void Add(string section, IReadOnlyList<SummaryItem> items)
        {
            foreach (SummaryItem item in items)
            {
                SummaryLines.Add(new SummaryLine(
                    section,
                    item.Text,
                    item.Certainty,
                    EvidenceResolver.ResolveAll(SessionId, item.Evidence, source, overlay))
                {
                    IsInferred = item.Support == SupportStatus.Inferred,
                });
            }
        }

        Add("Key points", summary.KeyPoints);
        Add("Decisions", summary.Decisions);

        foreach (SummaryAction action in summary.ActionItems)
        {
            SummaryLines.Add(new SummaryLine(
                "Action items",
                action.Task,
                action.Certainty,
                EvidenceResolver.ResolveAll(SessionId, action.Evidence, source, overlay))
            {
                HasAssignment = true,
                Owner = action.Owner is { } named
                    ? named + (action.OwnerSupport == SupportStatus.Inferred ? " (inferred)" : string.Empty)
                    : null,
                Due = action.DueDate ?? action.DueDateText,
                IsInferred = action.Support == SupportStatus.Inferred,
            });
        }

        Add("Open questions", summary.OpenQuestions);
        Add("Risks", summary.Risks);
        Add("Blockers", summary.Blockers);
    }

    private void BuildRevisionLists()
    {
        TranscriptRevisions.Clear();
        foreach (int revision in _entry.TranscriptRevisions)
        {
            TranscriptRevisions.Add(revision);
        }

        SummaryRevisions.Clear();
        foreach (int revision in _entry.SummaryRevisions)
        {
            SummaryRevisions.Add(revision);
        }
    }

    private void BuildSpeakers(SpeakerAliases overlay)
    {
        Speakers.Clear();

        if (_transcript is not { } transcript)
        {
            return;
        }

        foreach ((string speakerId, string original, string display) in SpeakerPresentation.Renameable(transcript, overlay))
        {
            Speakers.Add(new SpeakerRow(speakerId, original, display));
        }
    }

    /// <summary>Renames a remote speaker for presentation. Reversible by passing null.</summary>
    public void Rename(string speakerId, string? alias)
    {
        if (!_aliases.Rename(SessionId, speakerId, alias))
        {
            Notice = "That name could not be saved.";
            return;
        }

        Load();
        Notice = string.IsNullOrWhiteSpace(alias) ? "Original name restored." : "Name updated for display only.";

        // Search results carry the presented name, so the index has something to catch up on.
        RaiseCanonicalChanged();
    }

    private void RaiseCanonicalChanged() => CanonicalChanged?.Invoke(this, SessionId);

    // -- evidence navigation -----------------------------------------------------------------------

    /// <summary>What a click on a citation should do. Null when it cannot be followed.</summary>
    public TranscriptLine? LocateEvidence(EvidenceLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        if (!location.IsResolved)
        {
            Notice = location.Explanation ?? "That passage could not be located in this transcript version.";
            return null;
        }

        // Opening the exact revision the citation names, not the selected one.
        if (_transcript?.TranscriptRevision != location.TranscriptRevision)
        {
            SelectedTranscriptRevision = location.TranscriptRevision;
        }

        TranscriptLine? line = Lines.FirstOrDefault(l => string.Equals(l.SegmentId, location.SegmentId, StringComparison.Ordinal));

        if (line is null)
        {
            Notice = "That passage is not in this transcript version.";
            return null;
        }

        Notice = null;
        return line;
    }

    /// <summary>Locates a search hit the same way, so both routes land in one place.</summary>
    public TranscriptLine? LocateSegment(int transcriptRevision, string segmentId)
    {
        if (_transcript?.TranscriptRevision != transcriptRevision)
        {
            SelectedTranscriptRevision = transcriptRevision;
        }

        return Lines.FirstOrDefault(l => string.Equals(l.SegmentId, segmentId, StringComparison.Ordinal));
    }

    /// <summary>What following a citation reached: a line to reveal, and a moment to hear.</summary>
    public sealed record EvidenceFollow(TranscriptLine? Line, PlaybackRequest Request)
    {
        public bool IsApproximate => Request.IsApproximate;
    }

    /// <summary>
    /// Follows a citation into both the transcript and the audio, in one step.
    ///
    /// <para>
    /// A resolved citation opens the revision it names — never the selected one — and produces an
    /// exact seek. An unresolved one produces a seek to the time stored with the citation and says
    /// so, and does <b>not</b> change which transcript is selected: re-pointing a citation at a
    /// segment ID in a newer revision would show a reader a sentence the summary never saw, with
    /// every appearance of authority.
    /// </para>
    /// </summary>
    public EvidenceFollow FollowEvidence(EvidenceLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);

        PlaybackRequest request = location.ToPlaybackRequest();

        if (!location.IsResolved)
        {
            Notice = location.Explanation ?? "That passage could not be located in this transcript version. " +
                "The audio can still be played from the time stored with the citation.";

            return new EvidenceFollow(null, request);
        }

        Notice = null;
        return new EvidenceFollow(LocateEvidence(location), request);
    }

    /// <summary>A seek from a transcript line, which is exact by construction.</summary>
    public PlaybackRequest RequestPlayback(TranscriptLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new PlaybackRequest
        {
            SessionId = SessionId,
            StartSeconds = line.StartSeconds,
            EndSeconds = line.StartSeconds,
        };
    }

    // -- exports -----------------------------------------------------------------------------------

    public string SuggestTranscriptFileName(TranscriptExportFormat format) =>
        _transcript is { } transcript ? TranscriptExporter.SuggestFileName(transcript, format) : "transcript" + TranscriptExporter.Extension(format);

    public string SuggestSummaryFileName(SummaryExportFormat format) =>
        _summary is { } summary ? SummaryExporter.SuggestFileName(summary, format) : "summary" + SummaryExporter.Extension(format);

    public ExportResult ExportTranscript(TranscriptExportFormat format, string destination, bool overwrite = false)
    {
        if (_transcript is not { } transcript)
        {
            return ExportResult.Fail("no_transcript", "There is no transcript to export.");
        }

        return TranscriptExporter.Export(
            transcript,
            _transcripts.RevisionPath(SessionId, transcript.TranscriptRevision),
            format,
            destination,
            overwrite);
    }

    /// <summary>
    /// Composes a manual handoff from the transcript revision this meeting currently shows, and
    /// nothing else.
    ///
    /// <para>
    /// The revision is <c>_transcript</c>, which is always the selected one, so a handoff cannot copy
    /// a different version from the one the UI names. Presentation aliases are applied for the speaker
    /// labels; the immutable transcript and its segment IDs are untouched. A local summary is included
    /// only when the options ask for it. No network request is made, here or below this call.
    /// </para>
    /// </summary>
    public ManualHandoffResult BuildManualHandoff(ManualHandoffOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_transcript is not { } transcript)
        {
            return ManualHandoffResult.Fail("no_transcript", "There is no transcript to copy.");
        }

        return ManualHandoffComposer.Compose(
            transcript,
            _aliases.Read(SessionId),
            options,
            options.IncludeSummaryReference ? _summary?.Overview : null);
    }

    /// <summary>A safe filename for saving a handoff, carrying the session and revision.</summary>
    public string SuggestManualHandoffFileName() =>
        _transcript is { } transcript ? ManualHandoffComposer.SuggestFileName(transcript) : "handoff.md";

    public ExportResult ExportSummary(SummaryExportFormat format, string destination, bool overwrite = false)
    {
        if (_summary is not { } summary)
        {
            return ExportResult.Fail("no_summary", "There is no summary to export.");
        }

        return SummaryExporter.Export(
            summary,
            _summaries.PathFor(SessionId, summary.SummaryRevision),
            format,
            destination,
            overwrite,
            _aliases.Read(SessionId),
            Title);
    }

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Closes the meeting, and with it the audio device and the file it held.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Playback?.Dispose();
    }
}

/// <summary>A speaker a user may rename, and what they are currently called.</summary>
public sealed record SpeakerRow(string SpeakerId, string OriginalName, string DisplayName);
