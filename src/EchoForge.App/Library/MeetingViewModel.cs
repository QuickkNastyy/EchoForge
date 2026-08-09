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
    string SpeakerId,
    string Speaker,
    string Text,
    double StartSeconds,
    bool IsYou)
{
    /// <summary>
    /// Canonical transcript segment IDs represented by this displayed utterance.
    ///
    /// The reader is allowed to join adjacent ASR chunks for legibility, but citations/search still
    /// target the immutable segment IDs written to the transcript. Keeping all IDs here lets either
    /// route land on the combined row without rewriting the transcript.
    /// </summary>
    public IReadOnlyList<string> SegmentIds { get; init; } = [SegmentId];

    /// <summary>True when a search or an evidence click pointed at this line.</summary>
    public bool IsHighlighted { get; init; }

    public bool ContainsSegment(string segmentId) =>
        SegmentIds.Contains(segmentId, StringComparer.Ordinal);
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

/// <summary>
/// One numbered step of the action plan, ready to draw.
///
/// <para>
/// Ordering is presentation here and nowhere else: the number, the timing and the dependency all
/// came from the persisted brief, which was itself validated against the transcript. This record
/// decides nothing — it just refuses to hide the difference between "the meeting said to do this
/// first" and "EchoForge put it first".
/// </para>
/// </summary>
public sealed record PlanStepRow(
    int Number,
    string Title,
    string Detail,
    string Timing,
    string Basis,
    IReadOnlyList<EvidenceLocation> Evidence)
{
    public string? Owner { get; init; }

    public string? Due { get; init; }

    public string? DependsOn { get; init; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public bool HasOwner => !string.IsNullOrWhiteSpace(Owner);

    public bool HasDue => !string.IsNullOrWhiteSpace(Due);

    public bool HasDependency => !string.IsNullOrWhiteSpace(DependsOn);

    /// <summary>Shown only when the ordering is EchoForge's rather than the meeting's.</summary>
    public bool IsReasoned => !string.Equals(Basis, PlanBases.Explicit, StringComparison.Ordinal);

    public string BasisLabel => Basis switch
    {
        PlanBases.GroundedInference => "follows from what was said",
        PlanBases.Recommendation => "suggested order",
        _ => string.Empty,
    };

    public IReadOnlyList<EvidenceChip> Chips =>
    [
        .. Evidence.Select(e => new EvidenceChip(
            e.IsResolved ? e.StartSeconds.ToTimestamp() : e.StartSeconds.ToTimestamp() + " · unresolved",
            string.Equals(e.SourceTrack, TranscriptSpeakers.MicrophoneTrack, StringComparison.Ordinal),
            e.IsResolved,
            e))
    ];
}

/// <summary>
/// One section of the brief, with its heading.
///
/// <para>
/// A section only exists when the meeting had something for it. A brief that prints an empty
/// "Risks" heading after every call teaches the reader to skim past the one call where it says
/// something, so an empty section is not rendered quietly — it is not built at all.
/// </para>
/// </summary>
public sealed record BriefSection(string Heading, IReadOnlyList<SummaryLine> Blocks)
{
    public bool HasBlocks => Blocks.Count > 0;
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
        if (Playback is not null)
        {
            Playback.PropertyChanged += OnPlaybackChanged;
        }

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

    /// <summary>The opening prose of the brief: what happened, and what it changed.</summary>
    public ObservableCollection<SummaryLine> BriefSummary { get; } = [];

    /// <summary>
    /// What the reader has to do, in order.
    ///
    /// <para>
    /// Split from other people's work rather than interleaved with it, because the question this
    /// page exists to answer is "what do I need to do now" and an answer mixed with six other
    /// people's tasks is not an answer.
    /// </para>
    /// </summary>
    public ObservableCollection<PlanStepRow> YourPlan { get; } = [];

    public ObservableCollection<PlanStepRow> OtherPeoplesPlan { get; } = [];

    /// <summary>Decisions, blockers, context, backlog — only the ones the meeting earned.</summary>
    public ObservableCollection<BriefSection> BriefSections { get; } = [];

    public bool HasBrief => _summary?.Brief is not null;

    public bool HasPlan => YourPlan.Count > 0 || OtherPeoplesPlan.Count > 0;

    public bool HasOtherPeoplesPlan => OtherPeoplesPlan.Count > 0;

    /// <summary>
    /// What to say when a meeting genuinely produced no work.
    ///
    /// <para>
    /// A short test call, a demo, a conversation that assigned nothing: the right answer is to say
    /// so, not to manufacture a task out of "stop the recording". This is the sentence that makes
    /// an empty plan read as a finding rather than as a failure.
    /// </para>
    /// </summary>
    public static string EmptyPlanNotice =>
        "No post-meeting work was assigned in this meeting.";

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
        Playback?.DurationSeconds is > 0 and { } playable
            ? playable
            : _transcript?.DurationSeconds is > 0 and { } transcribed
                ? transcribed
                : Math.Max(0, _entry.Duration.TotalSeconds);

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
        ? "The transcript changed after this brief was written. Generate the brief again to bring it up to date."
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

    /// <summary>When this recording started, in the user's Windows time zone.</summary>
    public string RecordedWhen => (Entry.StartedUtc ?? Entry.CreatedUtc).ToLocalTime()
        .ToString("ddd, MMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture);

    /// <summary>Human-readable duration for the meeting header.</summary>
    public string DurationText
    {
        get
        {
            int seconds = Math.Max(0, (int)Math.Round(_entry.Duration.TotalSeconds));
            if (seconds < 60) { return $"{seconds} sec"; }
            int minutes = seconds / 60;
            int remainder = seconds % 60;
            if (minutes < 60) { return remainder == 0 ? $"{minutes} min" : $"{minutes} min {remainder} sec"; }
            int hours = minutes / 60;
            int minuteRemainder = minutes % 60;
            return minuteRemainder == 0 ? $"{hours} hr" : $"{hours} hr {minuteRemainder} min";
        }
    }

    // Kept for older bindings; the normal meeting UI uses RecordedWhen + DurationText.
    public string ProvenanceLine => RecordedWhen + " · " + DurationText;

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
            // ASR engines emit chunks for decoding convenience, not for reading. A fluent sentence
            // can therefore arrive as six tiny adjacent segments. Join only adjacent chunks from
            // the same canonical speaker when the real audio gap is small, and cap an utterance at
            // 30 seconds so a long uninterrupted monologue still has readable landmarks. Nothing
            // persisted changes: SegmentIds retains every canonical ID for search/evidence.
            const double joinGapSeconds = 3.0;
            const double maxUtteranceSeconds = 30;

            List<TranscriptSegment> utterance = [];

            void FlushUtterance()
            {
                if (utterance.Count == 0)
                {
                    return;
                }

                TranscriptSegment first = utterance[0];
                Lines.Add(new TranscriptLine(
                    first.Id,
                    first.StartSeconds.ToTimestamp(),
                    first.SpeakerId,
                    SpeakerPresentation.Present(first, overlay),
                    string.Join(" ", utterance.Select(part => part.Text.Trim()).Where(text => text.Length > 0)),
                    first.StartSeconds,
                    string.Equals(first.SpeakerId, TranscriptSpeakers.YouId, StringComparison.Ordinal))
                {
                    SegmentIds = [.. utterance.Select(part => part.Id)],
                });
                utterance.Clear();
            }

            foreach (TranscriptSegment segment in transcript.Segments)
            {
                if (utterance.Count == 0)
                {
                    utterance.Add(segment);
                    continue;
                }

                TranscriptSegment first = utterance[0];
                TranscriptSegment previous = utterance[^1];
                double gap = segment.StartSeconds - previous.EndSeconds;
                bool sameSpeaker =
                    string.Equals(previous.SpeakerId, segment.SpeakerId, StringComparison.Ordinal) &&
                    string.Equals(previous.SourceTrack, segment.SourceTrack, StringComparison.Ordinal);
                bool closeEnough = gap <= joinGapSeconds;
                bool withinReadableTurn = segment.EndSeconds - first.StartSeconds <= maxUtteranceSeconds;

                if (sameSpeaker && closeEnough && withinReadableTurn)
                {
                    utterance.Add(segment);
                    continue;
                }

                FlushUtterance();
                utterance.Add(segment);
            }

            FlushUtterance();
        }

        Shape = _transcript is { } shapeSource
            ? ConversationShape.FromTranscript(shapeSource)
            : Playback?.EnergyEnvelope is { } envelope
                ? ConversationShape.FromEnergyEnvelope(envelope)
                : ConversationShape.Empty;

        BuildSummaryLines(overlay);
        BuildRevisionLists();
        BuildSpeakers(overlay);

        foreach (string name in (string[])
        [
            nameof(HasTranscript), nameof(HasSummary), nameof(SummaryIsStale), nameof(StaleNotice),
            nameof(TranscriptModelText), nameof(SummaryModelText), nameof(SummaryOverview), nameof(RecordedWhen), nameof(DurationText),
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
        BriefSummary.Clear();
        YourPlan.Clear();
        OtherPeoplesPlan.Clear();
        BriefSections.Clear();

        if (_summary is not { } summary)
        {
            return;
        }

        // Citations are resolved against the revision the summary names, loaded separately from
        // whatever is currently selected. That is the whole point of the pair.
        TranscriptDocument? source = _transcripts.ReadTranscript(SessionId, summary.TranscriptRevision);

        BuildBrief(summary, source, overlay);

        if (summary.Narrative is { } narrative)
        {
            // Schema-v2 history. The brief replaced these sections because they said the same
            // thing three ways; documents written before that keep showing exactly what they said.
            AddNarrative("Main topics", narrative.MainTopics);
            AddNarrative("Important details", narrative.ImportantDetails);
            AddNarrative("Follow-ups", narrative.FollowUps);
        }

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

        void AddNarrative(string section, IReadOnlyList<SummaryNarrativeBlock> blocks)
        {
            foreach (SummaryNarrativeBlock block in blocks)
            {
                SummaryLines.Add(new SummaryLine(
                    section,
                    block.Text,
                    NarrativeCertainty(summary, block),
                    EvidenceResolver.ResolveAll(SessionId, block.Evidence, source, overlay)));
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

    /// <summary>
    /// Turns the persisted brief into what the page draws.
    ///
    /// <para>
    /// It decides nothing about content. Ordering, timing, audience and basis were all fixed when
    /// the brief was written and validated; this reads them. The one editorial act here is
    /// omission: a section with nothing in it is not built, so the page never shows a heading over
    /// an empty space.
    /// </para>
    /// </summary>
    private void BuildBrief(SummaryDocument summary, TranscriptDocument? source, SpeakerAliases overlay)
    {
        if (summary.Brief is not { } brief)
        {
            return;
        }

        foreach (SummaryNarrativeBlock block in brief.Summary)
        {
            BriefSummary.Add(new SummaryLine(
                "Summary",
                block.Text,
                NarrativeCertainty(summary, block),
                EvidenceResolver.ResolveAll(SessionId, block.Evidence, source, overlay)));
        }

        foreach (MeetingPlanStep step in brief.ActionPlan)
        {
            PlanStepRow row = new(
                step.Order,
                step.Title,
                step.Detail,
                step.Timing,
                step.Basis,
                EvidenceResolver.ResolveAll(SessionId, step.Evidence, source, overlay))
            {
                Owner = step.Owner is { } named
                    ? named + (step.OwnerSupport == SupportStatus.Inferred ? " (inferred)" : string.Empty)
                    : null,
                Due = step.DueDate ?? step.DueDateText,
                DependsOn = step.DependsOn,
            };

            if (string.Equals(step.Audience, PlanAudiences.Others, StringComparison.Ordinal))
            {
                OtherPeoplesPlan.Add(row);
            }
            else
            {
                YourPlan.Add(row);
            }
        }

        AddSection("Decisions", brief.Decisions);
        AddSection("Blockers and dependencies", brief.Blockers);
        AddSection("Important context", brief.ImportantContext);
        AddSection("Follow-ups", brief.FollowUps);
        AddSection("Open questions", brief.OpenQuestions);
        AddSection("Discussed, not now", brief.Backlog);
        AddSection("Risks", brief.Risks);

        void AddSection(string heading, IReadOnlyList<SummaryNarrativeBlock> blocks)
        {
            if (blocks.Count == 0)
            {
                return;
            }

            BriefSections.Add(new BriefSection(
                heading,
                [
                    .. blocks.Select(block => new SummaryLine(
                        heading,
                        block.Text,
                        NarrativeCertainty(summary, block),
                        EvidenceResolver.ResolveAll(SessionId, block.Evidence, source, overlay)))
                ]));
        }
    }

    private static string NarrativeCertainty(SummaryDocument summary, SummaryNarrativeBlock block)
    {
        Dictionary<string, string> certainty = summary.AllItems
            .Select(item => (item.Id, item.Certainty))
            .Concat(summary.ActionItems.Select(item => (item.Id, item.Certainty)))
            .ToDictionary(item => item.Id, item => item.Certainty, StringComparer.Ordinal);

        return block.SupportingItemIds.Any(id =>
                   certainty.TryGetValue(id, out string? value)
                   && string.Equals(value, SupportStatuses.Inferred, StringComparison.Ordinal))
            ? SupportStatuses.Inferred
            : SupportStatuses.Explicit;
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

        TranscriptLine? line = Lines.FirstOrDefault(l => l.ContainsSegment(location.SegmentId));

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

        return Lines.FirstOrDefault(l => l.ContainsSegment(segmentId));
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

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_transcript is null &&
            e.PropertyName == nameof(PlaybackViewModel.EnergyEnvelope) &&
            Playback?.EnergyEnvelope is { } envelope)
        {
            Shape = ConversationShape.FromEnergyEnvelope(envelope);
            Changed(nameof(Shape));
        }

        if (e.PropertyName is nameof(PlaybackViewModel.DurationSeconds) or nameof(PlaybackViewModel.EnergyEnvelope))
        {
            Changed(nameof(TimelineSeconds));
            Changed(nameof(EvidenceFraction));
        }
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
        if (Playback is not null)
        {
            Playback.PropertyChanged -= OnPlaybackChanged;
            Playback.Dispose();
        }
    }
}

/// <summary>A speaker a user may rename, and what they are currently called.</summary>
public sealed record SpeakerRow(string SpeakerId, string OriginalName, string DisplayName);
