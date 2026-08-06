"""Choosing where inference runs, and climbing down when it will not.

Three rules shape this.

**Never silently change profile.** A run that asked for the GPU and finished on the CPU took
ten times longer for a reason the user is entitled to know. Every decision here is recorded -
what was asked for, what actually ran, and why it changed - and the transcript carries it.

**Check before launching, not after.** Loading a 1.6 GB model onto a device that turns out not
to exist wastes minutes and produces a stack trace instead of an explanation.

**Climb down in the documented order.** GPU out of memory retries with a smaller batch on the
same device, because that is usually enough; only when the device itself will not work does it
restart on the CPU.

EchoForge redistributes no NVIDIA runtime libraries. It uses a CUDA runtime the machine already
has, and says so plainly when there is not one.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Callable, Final

CPU_INT8: Final[str] = "cpu-int8"
CUDA_INT8_FLOAT16: Final[str] = "cuda-int8-float16"
CUDA_FP16: Final[str] = "cuda-fp16"

PROFILES: Final[tuple[str, ...]] = (CPU_INT8, CUDA_INT8_FLOAT16, CUDA_FP16)

#: Batch sizes tried in order when a GPU runs out of memory. One is the floor: below that
#: there is nothing left to give up, and the honest answer is the CPU.
GPU_BATCH_LADDER: Final[tuple[int, ...]] = (8, 4, 2, 1)

CPU_BATCH_SIZE: Final[int] = 1


@dataclass(frozen=True, slots=True)
class ComputePlan:
    """One concrete attempt: a device, a compute type, and a batch size."""

    profile: str
    device: str
    compute_type: str
    batch_size: int

    def describe(self) -> str:
        return f"{self.profile} ({self.device}, {self.compute_type}, batch {self.batch_size})"


@dataclass(slots=True)
class ComputeOutcome:
    """What was asked for, what ran, and every step in between."""

    requested_profile: str
    plan: ComputePlan
    fallback_reason: str | None = None
    attempts: list[str] = field(default_factory=list)
    cuda_devices: int = 0
    runtime_versions: dict[str, str] = field(default_factory=dict)

    @property
    def fell_back(self) -> bool:
        return self.plan.profile != self.requested_profile


def cuda_device_count(probe: Callable[[], int] | None = None) -> int:
    """How many CUDA devices CTranslate2 can actually see.

    Asking CTranslate2 rather than inspecting drivers is deliberate: what matters is whether
    the library that will run the model can use a device, not whether one is installed. A
    machine with a GPU and a mismatched cuDNN reports zero here, which is the truth.
    """
    if probe is not None:
        return max(0, probe())

    try:
        import ctranslate2

        return max(0, int(ctranslate2.get_cuda_device_count()))
    except Exception:  # noqa: BLE001 - any failure means the GPU is not usable, not that we crash
        return 0


def plans_for(profile: str, cuda_devices: int) -> list[ComputePlan]:
    """Every attempt to make for a requested profile, best first.

    A GPU profile yields the batch ladder and then the CPU. A CPU profile yields exactly one
    plan: there is nothing to fall back to, and pretending otherwise would hide a real failure.
    """
    if profile not in PROFILES:
        raise ValueError(f"unknown compute profile {profile!r}")

    if profile == CPU_INT8 or cuda_devices <= 0:
        return [ComputePlan(CPU_INT8, "cpu", "int8", CPU_BATCH_SIZE)]

    compute_type = "float16" if profile == CUDA_FP16 else "int8_float16"
    plans = [ComputePlan(profile, "cuda", compute_type, batch) for batch in GPU_BATCH_LADDER]
    plans.append(ComputePlan(CPU_INT8, "cpu", "int8", CPU_BATCH_SIZE))
    return plans


def is_out_of_memory(error: BaseException) -> bool:
    """Whether a failure is the GPU running out of room rather than something else.

    CTranslate2 and CUDA report this as a plain runtime error with a message, so the message
    is what there is to go on. Treating an unrelated failure as an OOM would waste three more
    attempts before reporting it; treating an OOM as unrelated would skip straight to the CPU
    when a smaller batch would have worked.
    """
    text = str(error).lower()
    return any(
        marker in text
        for marker in ("out of memory", "cuda_error_out_of_memory", "cublas_status_alloc_failed", "oom")
    )


def runtime_versions() -> dict[str, str]:
    """What is actually installed, for the record. Absent pieces are named, not guessed."""
    versions: dict[str, str] = {}

    for name, module in (("faster_whisper", "faster_whisper"), ("ctranslate2", "ctranslate2")):
        try:
            imported = __import__(module)
            versions[name] = str(getattr(imported, "__version__", "unknown"))
        except Exception:  # noqa: BLE001
            versions[name] = "not installed"

    return versions


def run_with_fallback(
    requested_profile: str,
    attempt: Callable[[ComputePlan], Any],
    cuda_devices: int | None = None,
    probe: Callable[[], int] | None = None,
) -> tuple[Any, ComputeOutcome]:
    """Run ``attempt`` on the best plan that works, recording every step.

    An out-of-memory failure moves down the ladder. Any other failure on a GPU plan abandons
    the GPU entirely - retrying a smaller batch after an initialisation error would just fail
    three more times slowly. A CPU failure is final and is raised.
    """
    devices = cuda_devices if cuda_devices is not None else cuda_device_count(probe)
    plans = plans_for(requested_profile, devices)

    outcome = ComputeOutcome(
        requested_profile=requested_profile,
        plan=plans[0],
        cuda_devices=devices,
        runtime_versions=runtime_versions(),
    )

    if devices <= 0 and requested_profile != CPU_INT8:
        outcome.fallback_reason = (
            "no CUDA device is available to CTranslate2, so this ran on the CPU. "
            "EchoForge does not install NVIDIA runtime libraries; it uses one already on the machine."
        )

    last: BaseException | None = None

    for index, plan in enumerate(plans):
        try:
            result = attempt(plan)
        except BaseException as error:  # noqa: BLE001 - every failure mode is a routing decision
            last = error
            outcome.attempts.append(f"{plan.describe()}: {type(error).__name__}")

            if plan.device == "cpu":
                raise

            if is_out_of_memory(error):
                remaining = [p for p in plans[index + 1:] if p.device == "cuda"]
                outcome.fallback_reason = (
                    f"the GPU ran out of memory at batch size {plan.batch_size}; "
                    + ("retrying with a smaller batch" if remaining else "falling back to the CPU")
                )
                continue

            # Not memory: the device or its runtime is unusable. Skip the rest of the ladder.
            outcome.fallback_reason = (
                f"the GPU could not be used ({type(error).__name__}), so this ran on the CPU"
            )
            cpu = next(p for p in plans if p.device == "cpu")
            outcome.plan = cpu
            outcome.attempts.append(f"{cpu.describe()}: starting")
            return attempt(cpu), outcome

        outcome.plan = plan
        outcome.attempts.append(f"{plan.describe()}: ok")
        return result, outcome

    raise last if last is not None else RuntimeError("no compute plan was attempted")
