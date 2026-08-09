# Meeting intelligence: from a transcript to a plan somebody can act on

**Implementation status:** 2026-08-08
**Supersedes:** the "extract → synthesize → narrative" description in earlier phase reports.

This document describes what EchoForge produces after a meeting, and why it is built the way it
is. It is the authority for the summary pipeline; where it disagrees with a phase report, the phase
report is history.

## The problem this replaced

The previous pipeline extracted one-line facts from slices of the transcript, merged duplicates,
and asked a model to write prose about the merged list. Every claim was grounded and every citation
resolved, and the result was still not much use, for a specific and fixable reason:

**the final pass never saw the meeting.** It saw a bag of sentences. Nothing in its input said that
one piece of work was waiting on another, that a deadline was tomorrow, or that half the list was
speculation somebody had explicitly parked. So it could describe a meeting accurately and could not
answer the only question a person actually has afterwards, which is *what do I do now*.

Two failures followed from that, both observed on real recordings:

- **"Stop the recording" appeared as an action item.** It is an imperative sentence somebody really
  said, grounded in a real segment. It is also not work; it stopped existing the moment the call
  ended.
- **Sections repeated each other.** Summary, main topics, key points and important details said the
  same thing four ways, because nothing decided what belonged where.

## What is produced now

One document — the **meeting brief** — persisted as `brief` on schema-v3 summary revisions.

| Section | What goes in it |
|---|---|
| Summary | What the meeting was about, what happened, what changed. Never a count of objects. |
| What you need to do | An **ordered** plan. Not a list. |
| Other people's action items | Split out when somebody else was named, so "what do I do" stays answerable. |
| Decisions | What the meeting settled, including both sides of a reversal. |
| Blockers and dependencies | What is stopping progress, and what has to clear it. |
| Important context | Money, constraints, background. Not work. |
| Follow-ups | Should happen, nobody assigned it, did not earn a place in the plan. |
| Open questions | Unresolved and worth resolving. |
| Discussed, not now | Speculative features, future ideas, anything explicitly deferred. |
| Risks | Only when the meeting raised one. |

**Sections are omitted when empty.** A brief that prints an empty *Risks* heading after every call
teaches the reader to skim past the one call where it says something.

## What an action item is

The test is one sentence: **work that still exists after the call ends.**

Every commitment the analysis stage extracts carries a `classification`:

| Classification | Example | Reaches the plan |
|---|---|---|
| `post_meeting_commitment` | "Storm needs to QA signup before tomorrow's demo." | yes |
| `inferred_next_step` | The meeting plainly leaves it to be done, nobody said it outright. | yes |
| `ephemeral_instruction` | "Stop the recording." "Share your screen." "Scroll down." | no |
| `completed_in_meeting` | Asked for and done while everybody watched. | no |
| `future_idea` | "We should eventually rebuild that." | no — it goes to the backlog |

Everything is kept and labelled; nothing is deleted. An in-meeting instruction is a real thing that
was said, and somebody who goes looking for it should find it, in a place where it cannot be
mistaken for something to do. What changes is only whether it can become a plan step.

A meeting that produced no post-meeting work produces an **empty plan** and says so. That is a
finding about the meeting, not a failure of the summariser.

## The pipeline

```
transcript revision
  → analysis        (per chunk, prompts/analyze-v2.txt)
  → consolidation   (recursive fold, prompts/synthesize-v2.txt)
  → meeting brief   (one pass over the whole meeting, prompts/brief-v2.txt)
  → validation      (SummaryValidator, on the host)
  → immutable revision
```

### Analysis

Extracts, per chunk: key points, decisions, action items *with classifications*, open questions,
risks, blockers, **dependencies** ("we need X before Y"), **future ideas** (including anything
explicitly deprioritised) and **important context**. Owner and date invariants are unchanged: a
name has to be in a cited segment, an indefinite pronoun is the meeting saying nobody was assigned,
and the model never computes a calendar date.

### Consolidation

Unchanged. The fold may only merge: it may not invent a claim, cite a segment none of its inputs
cited, raise a certainty, or drop a fact to make the list shorter. Enforced by code, not by the
prompt.

### The brief

This is the stage that changed. It runs **once**, over the meeting, because a step's position in a
plan is a claim about every other step and cannot be produced in batches.

What reaches it depends on what fits, in four rungs, each of which is recorded on the run when it
is used:

1. every validated fact with its quoted evidence, **plus the entire transcript**;
2. the same facts, with the transcript replaced by an ordered digest of the whole meeting;
3. the same facts with evidence reduced to identity and time;
4. the facts that can become work, with descriptive key points withheld and counted.

The digest splits the meeting's **time span** into equal parts, not the fact list into equal
chunks. That is what guarantees complete coverage: a quiet stretch still gets a part, and the last
twenty minutes of a two-hour meeting cannot be squeezed out by a talkative first hour. When even
the digest will not fit, each part is thinned — never truncated to the first few, which would drop
the end of the meeting.

Every degradation is reported on the summary revision's `run.fallback_steps`, so a reader can see
what the brief was written from.

## Ordering, and how far it may reason

The brief is **allowed to reason** about sequence. That is the point of it. What it may not do is
invent.

Each plan step records a `basis`:

- `explicit` — the meeting stated the work, and where it was ordered, stated the ordering too;
- `grounded_inference` — the meeting stated the pieces and the relationship follows from them.
  *"Get access first, because the meeting said the integration cannot be finished without it"* is
  a grounded inference and exactly what the brief is for;
- `recommendation` — the work is real and the ordering is EchoForge's.

The UI says so only for the last two. Labelling every step would train a reader to ignore the
label; silence means somebody actually said it.

Priority comes from the transcript, never from generic business sense. The signals are: an explicit
block, a named deadline or demo, "do this first", "not now", a prerequisite that is still
unresolved, and a stated dependency.

### Owners and dates are not the brief's to choose

The plan-step schema **has no owner or due-date field**. Owner and date are lifted by the host from
the action facts a step names, where they already passed the owner and date invariants. This is
structural rather than a rule in a prompt: a final pass cannot introduce a commitment nobody made,
because it has nowhere to put one. A step addressed to `others` with no resolved owner becomes
`unassigned` rather than a dead end.

## Grounding

Unchanged in strength, and checked twice.

Every block and every plan step names the validated facts it rests on. Citations are built by code
from those facts' own segments, so a block cannot arrive carrying a timestamp of its own. The host
validator then re-checks, independently: unknown fact IDs, citations outside the named facts,
duplicate IDs, plan numbering, unknown audience/timing/basis values, an owner where the status says
unknown, a dependency on a step that is not in the plan, and a resolved date with no meeting date
to resolve it against.

A brief that fails any of that is refused, not repaired. Dropping the unsupported half of a claim
and keeping the rest would produce a document nobody wrote and nobody reviewed.

## Evaluation

`scripts/evaluate-meeting-briefs.py` runs the same meeting through several installed models and
reports what each brief would lead somebody to do:

- task recall against the commitments the meeting actually made;
- false tasks — anything from the "must not appear" list, such as an in-meeting instruction;
- whether the blocker came first;
- whether speculative work stayed out of the plan and landed in the backlog;
- invented owners and invented dates;
- readability proxies: plan length, sections used, summary length.

It deliberately prints no verdict. Two models that both recall every task and differ in whether
they ordered them usefully are not separated by an average.

The two scenarios live in `tests/fixtures/summary-benchmark/synthetic`, written rather than
recorded:

- **`synthetic-002-short-test`** — two people testing a recorder, one remarking that the interface
  is getting smaller, one saying "stop the recording". Correct outcome: a useful summary and an
  **empty** action plan.
- **`synthetic-003-work-meeting`** — an access blocker that gates everything, a customer demo
  tomorrow, dependent work behind it, speculative ideas, an explicit deferral, a reversal,
  in-meeting instructions, and money talk that is context rather than a task. Correct outcome: the
  blocker first, the demo second, backlog separated, instructions excluded, the reversal preserved.

Deterministic tests in `tests/worker_tests/test_meeting_brief.py` cover everything about those
scenarios that does not depend on a model: classification routing, what reaches the final pass for
short and long meetings, time coverage of the digest, the assembled document, and the fact that the
plan schema cannot express an owner.

## Compatibility

Schema v1 and v2 summaries are read exactly as they were written and are never rewritten. They keep
their `narrative`, their sections and their overview; the meeting page renders those instead of a
brief. New summaries are written as v3 with `brief` set and `narrative` null. The structured fact
arrays are unchanged and three optional ones were added (`future_ideas`, `important_context`,
`dependencies`), which older documents simply do not have.
