namespace EchoForge.Core.ManualCopy;

/// <summary>
/// The instructions EchoForge hands to an external assistant along with a transcript, versioned so a
/// change to the wording is a new version rather than a silent edit.
///
/// <para>
/// This is a <b>manual prompt</b>, not a second summarisation implementation. It carries the same
/// evidence discipline the local pipeline enforces — cite the exact segment IDs, never invent an
/// owner or a date, keep contradictions, separate what was decided from what was merely discussed —
/// so that a summary produced by pasting this into ChatGPT or Claude is held to the same rules as one
/// produced locally. The rules live here, as source content, rather than scattered through a view
/// model, and the version travels in the copied document so a reader can tell which wording produced
/// a given handoff.
/// </para>
///
/// <para>
/// The instruction text is deliberately fixed and self-contained: composing a handoff performs no
/// network request and consults no service, so the same transcript and options always produce the
/// same bytes.
/// </para>
/// </summary>
public static class ManualPromptTemplate
{
    /// <summary>
    /// The current template version. It is recorded in every handoff. Changing the instructions
    /// below is a new version; historical transcripts and summaries are never rewritten by it.
    /// </summary>
    public const string Version = "manual-summary-v1";

    /// <summary>How each transcript line is laid out, explained to the assistant.</summary>
    public const string LineFormat = "segment-000123  [00:01:23]  Speaker: what they said";

    /// <summary>
    /// The instruction block. It mirrors <c>worker/prompts/extract-v1.txt</c> and
    /// <c>synthesize-v1.txt</c>: the same certainty ladder, the same owner/date discipline, the same
    /// insistence on keeping contradictions and citing exact IDs. It says nothing that would require
    /// EchoForge to send anything anywhere.
    /// </summary>
    public static string Instructions =>
        """
        You are summarising one meeting from its transcript. The transcript is supplied below and is
        the only thing you may use. You are not writing to impress anyone; you are recording what was
        said and pointing at where it was said.

        ## How to read the transcript

        Each line is one segment, laid out like this:

            segment-000123  [00:01:23]  Speaker: what they said

        - The first token is the **segment ID**. It is exactly `segment-000123` — the digits are part
          of it and nothing else is. Cite it exactly as written. Do not paraphrase it, do not renumber
          it, and do not wrap it in brackets, quotes or anything else: write `segment-000123`, never
          `[segment-000123]` and never `123`.
        - The value in square brackets is the time from the start of the meeting. It is context, not
          an ID; never cite it in place of a segment ID.
        - The speaker label is a **presentation name** for reading convenience. `You` is the person
          who recorded the meeting; other labels are the remote side of the call and may be renamed
          display names. The evidence anchor is the segment ID, never the speaker label.

        ## The one rule that matters

        Every claim you make must be supported by the segments supplied, and must cite them by their
        exact segment IDs. You may only cite IDs that appear below. Do not claim evidence you were not
        given, and do not refer to audio, video or anything outside this text — you have only the
        transcript.

        ## Certainty: explicit, inferred, unknown

        Mark every item with one of these. They are not a confidence scale to climb:

        - **explicit** — the transcript says this, in words, in a segment you cite.
        - **inferred** — this is your reading of what was said. Keep it separate and label it, and use
          it only when the meeting clearly meant something it did not quite say.
        - **unknown** — the transcript does not say. This is a real answer and is often the right one.

        If you are choosing between explicit and inferred, choose inferred.

        ## Owner and due date

        Owner and due date each carry their own status, separate from the item's certainty. A task can
        be perfectly explicit while nobody said who owns it — that is the normal case, not a gap to
        fill.

        - An owner is **explicit** only when a person is actually named in a cited segment as doing the
          work. "Alex will prepare the deck" names an owner. "Someone will write it up", "we should do
          this", "I'll take a look" from an unnamed speaker do **not** — an indefinite pronoun means
          nobody was assigned. If no owner was named, the owner is **unknown**, and an unknown owner
          has no name.
        - Never compute a calendar date. Put the words that were actually said ("by Friday", "end of
          the month") as the due date text and leave the resolved date unknown. You do not know what
          day the meeting was.

        Guessing an owner is the single most damaging thing you can do here. "Unknown owner" is better
        than a name nobody agreed to.

        ## Contradictions

        If the meeting said one thing and later said the opposite, record **both**, each citing its own
        segments. Do not reconcile them, do not keep only the later one, and do not add a note about
        which you think won. A meeting that changed its mind is a fact about that meeting.

        ## Decisions versus discussion, actions versus possibilities

        - A **decision** is something the meeting actually settled. Something discussed but not settled
          is an open question, not a decision.
        - An **action item** is something someone actually committed to doing. Work that was merely
          floated as possible is not an action item. Do not promote a "we could" into a "we will".

        ## What to produce

        Organise the summary into these sections. Any section may be empty — most meetings do not
        contain a decision in every stretch, and an empty list is a better answer than one padded with
        things nobody said.

        - **Key points** — what the meeting was about.
        - **Decisions** — each citing at least one segment.
        - **Action items** — each with its task, owner (or unknown), owner status, due-date text (or
          none), due-date status, and at least one cited segment.
        - **Open questions** — raised and not answered.
        - **Risks** — something that might go wrong.
        - **Blockers** — something already stopping work.

        Quote or closely paraphrase what was said. Do not add framing, recommendations, next steps or
        severity ratings that nobody in the room said. Every item ends with the segment IDs that
        support it.
        """;
}
