"""Run the same meeting through several local models and ask whether the brief would help.

The question this answers is not "how close is the wording". ROUGE would happily reward a summary
that recovered every phrase and put "stop the recording" at the top of the action plan. The
question is the one somebody actually has after a meeting:

    **would this document lead me to do the right work next?**

So what is measured is task recall, invented tasks, invented owners and dates, whether the blocker
came first, whether speculative ideas stayed out of the plan, and whether in-meeting instructions
were kept out of it. Every expectation comes from the gold record beside the transcript, written
before any model saw it.

Nothing here declares a winner. It prints what each model did and leaves the judgement where it
belongs.

    python scripts/evaluate-meeting-briefs.py --model gemma-4-12b --model gpt-oss-20b

Requires the models to be installed and qualified. It is a local, opt-in harness: no part of the
build depends on it, because it needs several gigabytes of weights and a GPU.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "worker"))

from echoforge_worker.llama_server import (  # noqa: E402
    DEFAULT_LADDER,
    GPT_OSS_LADDER,
    LlamaServer,
    LlamaServerError,
)
from echoforge_worker.local_summary import LocalSummaryBackend  # noqa: E402
from echoforge_worker.measurements import RunMeasurements  # noqa: E402
from echoforge_worker.model_profiles import resolve_model_profile  # noqa: E402
from echoforge_worker.summarize import (  # noqa: E402
    OUTSTANDING_WORK,
    SummaryChunk,
    build_summary,
    read_transcript,
    synthesize,
)

REPO = Path(__file__).resolve().parent.parent
CORPUS = REPO / "tests" / "fixtures" / "summary-benchmark" / "synthetic"
LOCAL = Path(os.environ.get("LOCALAPPDATA", ""))

#: Where each model's verified files were installed. Read rather than searched for: a harness that
#: hunted the disk for a GGUF could measure a file EchoForge would never load.
MODELS: dict[str, Path] = {
    "gemma-4-12b": LOCAL / "EchoForge/models/summary.gemma-4-12b-it-qat-q4-0/29d097773436b69ff9feafd636ab4cf873786537/gemma-4-12b-it-qat-q4_0.gguf",
    "gpt-oss-20b": LOCAL / "EchoForge/models/summary.gpt-oss-20b-mxfp4/ef9b12f2ff56c69cf32153a02784e7a3c88bf524/gpt-oss-20b-MXFP4.gguf",
    "ministral-3-14b": LOCAL / "EchoForge/models/summary.ministral-3-14b-instruct-2512-q4-k-m/74fac473c43357d7fb2671713608183cc72496d0/Ministral-3-14B-Instruct-2512-Q4_K_M.gguf",
}


class Request:
    """The one summary request shape the pipeline reads. No session, no user data."""

    def __init__(self, meeting: dict) -> None:
        self.session_id = meeting["meeting_id"]
        self.summary_revision = 1
        self.transcript_revision = 1
        self.transcript_sha256 = meeting["transcript_sha256"]
        self.created_at_utc = "2026-08-07T12:00:00+00:00"
        self.prompt_version = "meeting-brief-v3"
        self.meeting_date = meeting["meeting_date"]
        self.infer_owners = False
        self.infer_due_dates = False
        self.repair_attempt = 0
        self.synthesis_group_size = 0


#: Words that carry no meaning for matching. Kept short on purpose: an aggressive stop list starts
#: making unrelated sentences look alike, which is the failure mode that inflates a recall number.
_NOISE = frozenset(
    "a an and are as at be before by do for from get given had has have in into is it its of on "
    "once one or our so that the their them then there they this to up us was we were will with "
    "you your".split()
)


def words(text: str) -> set[str]:
    return {word.strip(".,;:!?'\"()") for word in text.casefold().split()} - _NOISE - {""}


def matches(candidate: str, phrases: list[str], threshold: float = 0.55) -> bool:
    """Whether a plan step is recognisably the task a gold entry describes.

    Substring matching was the first attempt and it under-reports badly: a model that writes
    "Remote will add the user to Rebuild today" has plainly recalled "Get access to the Rebuild
    admin console", and no alias list written in advance will contain every good paraphrase. This
    asks instead how much of the gold task's vocabulary the step actually contains.

    It is a coarse instrument and is reported as one. It is here to make a large difference between
    two models visible, not to separate a 0.71 from a 0.68.
    """
    subject = words(candidate)
    if not subject:
        return False

    for phrase in phrases:
        wanted = words(phrase)
        if wanted and len(wanted & subject) / len(wanted) >= threshold:
            return True
    return False


def contains(haystack: str, needle: str) -> bool:
    return needle.casefold() in haystack.casefold()


def score(document: dict, gold: dict) -> dict:
    """What the brief would lead somebody to do, against what the meeting actually asked for."""
    brief = document.get("brief") or {}
    plan = brief.get("action_plan") or []
    plan_text = " \\n".join(f"{step.get('title', '')} {step.get('detail', '')}" for step in plan)
    backlog_text = " \\n".join(block.get("text", "") for block in brief.get("backlog") or [])
    context_text = " \\n".join(block.get("text", "") for block in brief.get("important_context") or [])

    expectations = gold.get("brief") or {}
    wanted = gold.get("action_items") or []

    # Task recall: how many of the commitments the meeting made are in the plan at all.
    steps_text = [f"{step.get('title', '')} {step.get('detail', '')}" for step in plan]
    recalled = []
    missed = []
    for item in wanted:
        phrases = [item["task"], *item.get("aliases", [])]
        found = any(matches(step, phrases) for step in steps_text)
        (recalled if found else missed).append(item["id"])

    # False tasks: things in the plan that the meeting did not ask anybody to do.
    false_tasks = [
        phrase for phrase in expectations.get("must_not_appear_in_plan", [])
        if contains(plan_text, phrase)
    ]

    # Backlog classification: speculative work belongs out of the plan and in the backlog.
    backlog_kept_out = [
        phrase for phrase in expectations.get("expected_backlog", [])
        if not any(matches(step, [phrase], threshold=0.7) for step in steps_text)
    ]
    backlog_present = [
        phrase for phrase in expectations.get("expected_backlog", [])
        if contains(backlog_text, phrase)
    ]

    context_present = [
        phrase for phrase in expectations.get("expected_context", [])
        if contains(context_text + " " + json.dumps(brief), phrase)
    ]

    # Ordering: did the thing that gates everything else come first.
    must_be_first = expectations.get("must_be_first")
    first_right = None
    if must_be_first and plan:
        target = next((item for item in wanted if item["id"] == must_be_first), None)
        if target is not None:
            phrases = [target["task"], *target.get("aliases", [])]
            first_right = matches(steps_text[0], phrases)

    # Owners and dates: never invented, whatever else happened.
    invented_owners = [
        step["id"] for step in plan
        if step.get("owner") and step.get("owner_status") == "unknown"
    ]
    invented_dates = [
        step["id"] for step in plan
        if step.get("due_date") and step.get("due_date_status") == "unknown"
    ]

    return {
        "matching": "bag-of-words overlap against the gold task and its aliases; coarse by design",
        "plan_steps": len(plan),
        "tasks_expected": len(wanted),
        "tasks_recalled": recalled,
        "tasks_missed": missed,
        "false_tasks": false_tasks,
        "blocker_first": first_right,
        "backlog_kept_out_of_plan": backlog_kept_out,
        "backlog_present": backlog_present,
        "context_present": context_present,
        "invented_owners": invented_owners,
        "invented_dates": invented_dates,
        "sections": [name for name, blocks in brief.items() if name != "action_plan" and blocks],
        "summary_words": sum(len((block.get("text") or "").split()) for block in brief.get("summary") or []),
        "structured_actions": len(document.get("action_items") or []),
        "outstanding_actions": sum(
            1 for action in document.get("action_items") or []
            if action.get("classification") in OUTSTANDING_WORK or action.get("classification") is None
        ),
    }


def run(backend_name: str, meeting: dict, binary: Path, timeout: float) -> dict:
    profile = resolve_model_profile(backend_name)
    if profile is None:
        raise SystemExit(f"{backend_name!r} is not a local summary model")

    model = MODELS[backend_name]
    if not model.is_file():
        return {"model": backend_name, "error": f"not installed: {model}"}

    ladder = GPT_OSS_LADDER if backend_name == "gpt-oss-20b" else DEFAULT_LADDER
    document, segments = read_transcript(CORPUS / meeting["transcript_path"])
    request = Request(meeting)

    server = LlamaServer(
        binary_path=binary,
        model_path=model,
        profile=ladder[0],
        model_args=profile.server_args,
        startup_timeout=timeout,
    )

    started = time.perf_counter()
    try:
        server.start()
    except LlamaServerError as error:
        server.stop()
        return {"model": backend_name, "error": error.detail}

    measurements = RunMeasurements(backend=backend_name)
    backend = LocalSummaryBackend(server, profile=profile, model_revision="evaluation")
    backend.measurements = measurements

    try:
        chunk = SummaryChunk(
            index=0,
            first_segment_id=segments[0].id,
            last_segment_id=segments[-1].id,
            overlap_before=0,
            overlap_after=0,
            input_fingerprint="",
        )
        candidates = backend.extract(chunk, segments, request)
        folded = synthesize(candidates, backend, request)
        brief = backend.brief(folded.candidates, segments, request)
        summary = build_summary(request, document, segments, folded.candidates, backend, folded, brief)
    except LlamaServerError as error:
        return {"model": backend_name, "error": error.detail}
    finally:
        server.stop()

    result = score(summary, meeting["gold"])
    result["model"] = backend_name
    result["seconds"] = round(time.perf_counter() - started, 1)
    result["fell_back"] = measurements.fell_back
    result["brief"] = summary.get("brief")
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", action="append", dest="models", choices=sorted(MODELS))
    parser.add_argument("--meeting", action="append", dest="meetings")
    parser.add_argument("--binary", help="path to llama-server.exe")
    parser.add_argument("--timeout", type=float, default=420.0)
    parser.add_argument("--out", default="artifacts/evaluation/meeting-briefs.json")
    arguments = parser.parse_args()

    binary = Path(arguments.binary) if arguments.binary else None
    if binary is None:
        candidates = sorted((LOCAL / "EchoForge/runtime").glob("llama-*/llama-server.exe"))
        if not candidates:
            raise SystemExit("llama-server.exe was not found; pass --binary")
        binary = candidates[-1]

    corpus = json.loads((CORPUS / "corpus.json").read_text(encoding="utf-8"))
    wanted = arguments.meetings or ["synthetic-002-short-test", "synthetic-003-work-meeting"]
    meetings = [entry for entry in corpus["meetings"] if entry["meeting_id"] in wanted]

    results = []
    for meeting in meetings:
        for backend_name in arguments.models or ["gemma-4-12b"]:
            print(f"— {meeting['meeting_id']} / {backend_name}", flush=True)
            outcome = run(backend_name, meeting, binary, arguments.timeout)
            outcome["meeting"] = meeting["meeting_id"]
            results.append(outcome)

            if "error" in outcome:
                print(f"    failed: {outcome['error']}", flush=True)
                continue

            print(
                f"    plan {outcome['plan_steps']} steps · "
                f"recalled {len(outcome['tasks_recalled'])}/{outcome['tasks_expected']} · "
                f"false tasks {len(outcome['false_tasks'])} · "
                f"blocker first {outcome['blocker_first']} · "
                f"invented owners {len(outcome['invented_owners'])} · "
                f"{outcome['seconds']}s",
                flush=True,
            )

    destination = REPO / arguments.out
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8", newline="")
    print(f"\nwritten: {destination}")

    # Deliberately no verdict. Two models that both recall every task and differ in whether they
    # ordered them usefully are not separated by an average, and picking a winner here would put
    # a judgement in a file instead of in front of a person.
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
