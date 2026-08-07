# Phase 4 Pass 1 — meeting library, indexing, search, and result navigation

**Date:** 2026-08-07
**Scope:** making sessions, transcripts and summaries findable and reviewable. Stops before
synchronized audio playback and any deletion workflow.

> **Phase 3 acceptance quality gate remains NOT RUN — pending human corpus data.** The
> human-corrected development and held-out release corpora still do not exist. Nothing in this
> pass changes that, and no summary-quality claim is made anywhere in it.

## SQLite provider

`Microsoft.Data.Sqlite.Core` **10.0.0** with `SQLitePCLRaw.bundle_e_sqlite3` **3.0.2**, pinned
exactly in `Directory.Packages.props` under the repository's existing no-floating-versions rule.

The `.Core` package plus an explicit bundle rather than the metapackage, because **FTS5 lives in
the native library, not the managed provider** — pinning only the provider would leave the thing
search actually depends on floating.

SQLite is referenced by `EchoForge.Infrastructure` and nowhere else. `EchoForge.Core` states in
its project file that it takes no SQLite dependency, and that stays true: nothing that decides
what a transcript or a summary *is* can start depending on a database.

## The canonical authority rule

**Everything in the database was computed from the session folders, and nothing is stored only
there.** `LibraryProjection` reads the journal, the projections and the revision files; the index
holds that plus an inverted index. Delete it, corrupt it, or find it written by an older schema and
the answer is identical: throw it away and rebuild.

There is deliberately **no migration path**. A projection that rebuilds in seconds does not need
one, and hand-written migrations are how a cache quietly becomes something you are afraid to
delete.

Four tests hold this: the database deleted, replaced with a text file, stamped with a wrong schema
version, and left describing a superseded transcript. In every case the library comes back whole
and the files win.

### A real defect this found

`Open()` ran a PRAGMA to force the first read of the file. On a file that was not a database, that
threw *inside* the helper — so the connection was never assigned, never disposed, and kept the
corrupt file locked. The rebuild that followed then could not delete the very file it was called
to replace, and reported failure. It disposes on the way out now. The corrupt-database test is
what surfaced it.

## Index schema and lifecycle

Schema version 1. Three tables: `index_meta`, `meetings` (one row per session), and `search`, an
FTS5 virtual table tokenized `unicode61 remove_diacritics 2`.

- **Create** — on first open.
- **Ensure ready** — checks the version *and* probes both tables, because a table existing is not
  the same as it working; a truncated file presents a valid header and fails on the first real
  read. Better found here than mid-search.
- **Incremental update** — `UpdateAsync(sessionId)` re-reads one session and replaces its rows.
- **Full rebuild** — discards and re-reads every folder, with progress.
- **Cancellation** — a cancelled rebuild discards the partial index rather than leaving one that
  answers with a subset of the library and no way to tell.
- **Concurrency** — WAL, `busy_timeout`, one write gate; readers open their own connections.

`EnsureReadyAsync` returns an `IndexHealth` rather than throwing. A library that refused to open
because its cache was damaged would be worse than one with no cache at all.

## Search

FTS5 across transcript segments and every summary section (overview, key points, decisions,
actions, open questions, risks, blockers).

- **Words are ANDed.** Any-of would bury the meeting somebody wanted under everything containing
  one common word.
- **Quotes mean a phrase**, because that is what quotes mean everywhere else.
- **The search box is not a query console.** Every term is quoted into an FTS5 literal and
  embedded quotes are doubled, so `Q3 (revised)` and `budget: 40k` are searched for literally
  instead of being a syntax error or silently becoming operators.
- **Highlights come from the tokenizer**, via `highlight()` with two control characters as
  delimiters, then located and stripped. Re-finding terms in .NET would disagree with SQLite about
  case folding, diacritics and word boundaries. Control characters because any printable delimiter
  can appear in a transcript.
- **Deterministic ordering**: bm25, then meeting date, then hit ID. bm25 ties are ordinary —
  identical text scores identically — and without the tie-break the same search would shuffle on
  every rebuild. A test asserts stability across one.
- Hits carry `transcript_revision` + `segment_id`, or the summary revision, so a result can be
  opened against the text its offsets belong to.

### Unicode needed more than a tokenizer setting

`unicode61` splits on whitespace and punctuation, which covers every script that writes spaces and
none of the ones that do not. A Japanese sentence arrives as one enormous token and searching a
word inside it matches nothing.

An **exact substring scan** runs only when the query contains kana, CJK or hangul — i.e. only when
the tokenizer cannot have segmented it. A substring is a *stricter* test than a token match, so
this cannot turn `cat` into `certificate`, and it is deliberately not applied to space-separated
scripts where it would.

## Evidence resolution

`EvidenceResolver` resolves against the pair `transcript_revision` + `segment_id` and refuses
anything else. Handing it a different revision than the citation names returns **degraded**, not a
rebased answer.

That refusal is the point. A segment ID is unique only inside one revision, so the same ID in a
reprocessed transcript is a different piece of speech — following it would show a reader a
sentence the summary never saw, and it would look authoritative. A citation whose revision is gone
falls back to the stored timestamp, says so, and produces a `PlaybackRequest` marked
`IsApproximate`.

Times come from the segment when resolved; the citation's stored copy is a fallback, not a second
opinion. `EvidenceLocation` carries session, revision, segment, start/end, track, epoch and
presented speaker — enough for a later playback layer to seek, which is a **seam, not a stub**:
nothing here pretends to play audio.

## Speaker aliases

A separate `speaker-aliases.json` beside the session. Writing an alias into the transcript would
rewrite an immutable revision, break the digest it was activated under, and make every citation
into it unverifiable — to change a display name. Keeping it outside makes reverting free, because
nothing was overwritten.

**You is not renameable**, enforced in `SpeakerPresentation.Sanitize` on both read and write.
Microphone attribution follows from which device the audio came from; it is the one speaker fact
EchoForge does not infer, and letting it be renamed would put the only certain label on the same
footing as the uncertain ones. A file that somehow contains an alias for You cannot express it.

Aliases apply to the transcript view, search results and exports. Evidence identity never changes.

## Stale summaries

Computed, never stored: `SummaryIsStale` is true when the selected summary's source transcript
revision differs from the selected transcript revision.

Selecting a different transcript **does not touch any summary** — asserted by comparing the
summary file's bytes before and after. The stale notice names both versions, says the summary is
still accurate about the one it came from, and offers regeneration. Its citations still resolve,
because they still point at their own revision.

## WPF surface

A **Meetings…** button on the main window opens a library window (verified present in the running
application).

- **Library**: meeting list with date, length, status, and transcript / summary / stale summary /
  needs-attention indicators; a search box with results; refresh and rebuild.
- **Transcript tab**: virtualized recycling `ListBox`, selectable and copyable text, speaker
  labels, timestamps, revision selector, export.
- **Summary tab**: overview, all sections, owner/date detail, resolved evidence timestamps,
  revision selector, model metadata, stale banner with regenerate, export.
- **Speakers tab**: rename and reset, with the guarantee spelled out on screen.
- Double-clicking a citation opens the revision it names and scrolls to the segment; double-clicking
  a search result opens that meeting at that line.

Transcript lines are plain records, not view models — no change notification, no commands, nothing
to unsubscribe. A test loads 6,000 lines and asserts both that and the time bound. Search runs off
the UI thread; a test asserts `Execute` returns in under 250 ms.

## Exports

`SummaryExporter` (JSON, text, Markdown) joins the existing `TranscriptExporter` (JSON, text, SRT,
VTT). Shared `ExportNaming` handles sanitization and atomic writes.

- **Deterministic** — invariant formatting, one fixed line ending.
- **Canonical JSON is copied, never re-serialised**, so the file still hashes to the digest its
  revision was activated under.
- **Every claim exports with its evidence**, plus owner and date *status*: "Owner: unknown" stays
  visibly unknown in a document somebody pastes into an email.
- **Markdown escaping is structural, not blanket.** Characters that always format are escaped
  everywhere; `#`, `-`, `+`, `=` only at the start of an item. Escaping hyphens everywhere turned
  `gemma-4-12b` into `gemma\-4\-12b` for no benefit — my first version did exactly that, and the
  test caught it.
- **Unicode file names are kept**, not transliterated; reserved Windows names are prefixed, and
  trailing dots and spaces removed because Windows silently drops them and resolves to a different
  file.
- **No silent overwrite**, atomic via a temporary neighbour, and a failed export cannot touch the
  canonical revision — asserted.

## Verification

| Check | Result |
|---|---|
| `dotnet build -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test` | **642 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **218 passed** |
| `scripts/verify-models.ps1` | PASS, 35 entries |
| Application launch | opens, composes the library (Meetings… button present), closes cleanly (exit 0) |

58 new .NET tests. All Phase 1–3 tests unchanged and passing.

## Remaining Phase 4 work

1. **Synchronized audio playback**, and the aligned two-track mix derivative it needs. The
   `PlaybackRequest` seam is in place and produces correct seeks; nothing consumes it yet.
2. **Evidence click seeks audio within 250 ms** — the plan's completion criterion, which needs
   playback before it can be measured.
3. **Deletion workflow**: explicit confirmation, Recycle Bin where supported, refusal to delete a
   running session.
4. **Date-range filtering in the UI.** The query and index support `Since`/`Until`; no control is
   bound to them yet.
5. **Reprocess-from-library actions** (transcribe again, summarise again) invoked from a meeting
   rather than from the main window. The regenerate button currently explains where to do it.
6. **Index maintenance on change** — the index updates on demand and on rebuild; it does not yet
   subscribe to transcription or summarisation completing.

Phase 5 not started.
