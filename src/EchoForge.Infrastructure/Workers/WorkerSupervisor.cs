using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EchoForge.Contracts.Workers;

namespace EchoForge.Infrastructure.Workers;

/// <summary>
/// Starts one worker, has one conversation with it, and makes sure nothing survives.
///
/// <para>
/// The supervisor is written on the assumption that the child may be broken. It may print
/// half a line and die, answer a version it does not speak, report a result it did not
/// write, or refuse to stop. Every one of those is a defined outcome here rather than an
/// exception escaping into the recorder's process, because the one thing a transcription
/// failure must never do is disturb a recording.
/// </para>
/// </summary>
public sealed class WorkerSupervisor
{
    private readonly WorkerLaunchOptions _options;
    private readonly ICaptureActivityGate _captureGate;

    public WorkerSupervisor(WorkerLaunchOptions options, ICaptureActivityGate? captureGate = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _captureGate = captureGate ?? NoRecordingInProgressGate.Instance;
    }

    /// <summary>
    /// Runs one transcription job.
    ///
    /// <para>
    /// Never throws for a worker fault: the outcome is the return value. Cancellation is
    /// likewise an outcome rather than an <see cref="OperationCanceledException"/>, because
    /// a cancelled job still has to report what it left behind.
    /// </para>
    /// </summary>
    public async Task<WorkerRunResult> TranscribeAsync(
        string jobId,
        TranscriptionRequest request,
        IProgress<ProgressMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(request);

        // Recording always has priority. This is checked before launch rather than after,
        // so a job that must wait costs nothing at all.
        if (_captureGate.IsCaptureActive)
        {
            return WorkerRunResult.Busy();
        }

        using Run run = new(
            _options,
            jobId,
            new StartJobMessage
            {
                JobId = jobId,
                JobKind = WorkerProtocol.TranscribeJobKind,
                Request = request,
            },
            progress);

        return await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one summarisation job.
    ///
    /// <para>
    /// The same supervisor, the same Job Object, the same failure taxonomy. A worker runs one job
    /// of one kind and exits, so the two job kinds share everything except what they are asked to
    /// do — which is why summarisation needed no second process model.
    /// </para>
    /// </summary>
    public async Task<WorkerRunResult> SummarizeAsync(
        string jobId,
        Contracts.Summaries.SummaryRequest request,
        IProgress<ProgressMessage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(request);

        if (_captureGate.IsCaptureActive)
        {
            return WorkerRunResult.Busy();
        }

        using Run run = new(
            _options,
            jobId,
            new StartJobMessage
            {
                JobId = jobId,
                JobKind = WorkerProtocol.SummarizeJobKind,
                SummaryRequest = request,
            },
            progress);

        return await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One worker lifetime, and everything that has to be released when it ends.</summary>
    private sealed class Run(
        WorkerLaunchOptions options,
        string jobId,
        StartJobMessage startJob,
        IProgress<ProgressMessage>? progress) : IDisposable
    {
        private readonly Lock _terminateLock = new();
        private readonly Lock _writeLock = new();
        private readonly StringBuilder _standardError = new();
        private readonly List<WarningMessage> _warnings = [];
        private readonly List<string> _violations = [];

        private Process? _process;
        private WindowsJobObject? _job;
        private CancellationTokenRegistration _timeoutRegistration;
        private CancellationTokenRegistration _cancelRegistration;
        private CancellationTokenSource? _timeoutSource;
        private CancellationTokenSource? _graceSource;
        private CancellationTokenRegistration _graceRegistration;

        private WorkerEnvironment? _environment;
        private WorkerMessage? _terminal;
        private WorkerStage _stage = WorkerStage.Handshake;
        private bool _readyReceived;
        private bool _timedOut;
        private bool _cancelRequested;
        private bool _terminated;
        private string? _protocolFailure;

        public async Task<WorkerRunResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            if (!TryStart(out WorkerRunResult? launchFailure))
            {
                return launchFailure!;
            }

            Process process = _process!;

            _timeoutSource = new CancellationTokenSource(options.Timeout);
            _timeoutRegistration = _timeoutSource.Token.Register(OnTimeout);
            _cancelRegistration = cancellationToken.Register(OnCancelRequested);

            Task<string> stderrDrain = DrainStandardErrorAsync(process);

            Send(new HelloMessage
            {
                HostVersion = options.HostVersion,
                SupportedProtocolVersions = WorkerProtocol.SupportedVersions,
            });

            await ReadUntilTheChildStopsTalkingAsync(process).ConfigureAwait(false);

            Terminate();

            int? exitCode = await WaitForExitAsync(process).ConfigureAwait(false);
            string stderr = await CollectStandardErrorAsync(stderrDrain).ConfigureAwait(false);

            return Resolve(exitCode, stderr, cancellationToken);
        }

        // -- launch ------------------------------------------------------------------------

        private bool TryStart(out WorkerRunResult? failure)
        {
            failure = null;

            ProcessStartInfo startInfo = new()
            {
                FileName = options.PythonExecutable,
                WorkingDirectory = options.WorkerRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Utf8NoBom,
                StandardOutputEncoding = Utf8NoBom,
                StandardErrorEncoding = Utf8NoBom,
            };

            // ArgumentList, never a command line built by hand: a worker root containing a
            // space or a quote must not be able to change what gets executed.
            startInfo.ArgumentList.Add("-X");
            startInfo.ArgumentList.Add("utf8");
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(options.ModuleName);

            options.ApplyEnvironment(startInfo.Environment);

            // The job exists before the process does, so there is no window in which a
            // started child is unowned for longer than a single assignment call.
            _job = WindowsJobObject.TryCreate();

            try
            {
                _process = Process.Start(startInfo);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                failure = new WorkerRunResult
                {
                    Outcome = WorkerOutcome.LaunchFailed,
                    Error = new WorkerError(WorkerErrorCodes.InternalError, WorkerStage.Handshake, ex.Message),
                };
                return false;
            }

            if (_process is null)
            {
                failure = new WorkerRunResult
                {
                    Outcome = WorkerOutcome.LaunchFailed,
                    Error = new WorkerError(
                        WorkerErrorCodes.InternalError,
                        WorkerStage.Handshake,
                        "the worker process could not be started"),
                };
                return false;
            }

            // The worker's first act is to wait for hello on stdin, so it does nothing at
            // all between starting and being contained.
            if (_job is not null && !_job.TryAssign(_process))
            {
                _violations.Add("the worker could not be assigned to a Job Object; falling back to killing the process tree");
            }

            return true;
        }

        // -- the conversation --------------------------------------------------------------

        private async Task ReadUntilTheChildStopsTalkingAsync(Process process)
        {
            StreamReader reader = process.StandardOutput;

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The pipe went away, which is what terminating the tree does.
                    break;
                }

                if (line is null)
                {
                    break;
                }

                WorkerMessageParse parse = WorkerMessageCodec.Parse(line);

                if (parse.IsIgnorable)
                {
                    continue;
                }

                if (!parse.IsMessage)
                {
                    // Strict: a line this host cannot make sense of ends the conversation,
                    // unless the job was already over, in which case it is only evidence
                    // that the worker build is suspect.
                    string description = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{parse.Failure}: {parse.Detail}");

                    if (_terminal is not null)
                    {
                        _violations.Add(description);
                        continue;
                    }

                    _protocolFailure = description;
                    Terminate();
                    break;
                }

                if (_terminal is not null)
                {
                    _violations.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{parse.Message!.Type}' arrived after the job had already finished"));
                    continue;
                }

                if (!Handle(parse.Message!))
                {
                    Terminate();
                    break;
                }
            }
        }

        /// <summary>Applies one message. Returns false when the conversation must stop.</summary>
        private bool Handle(WorkerMessage message)
        {
            switch (message)
            {
                case ReadyMessage ready:
                    return HandleReady(ready);

                case StartedMessage started:
                    if (!_readyReceived)
                    {
                        _protocolFailure = "the worker started a job before completing the handshake";
                        return false;
                    }

                    if (!string.Equals(started.JobId, jobId, StringComparison.Ordinal))
                    {
                        _protocolFailure = "the worker acknowledged a different job";
                        return false;
                    }

                    _stage = WorkerStage.Preparing;
                    return true;

                case ProgressMessage update:
                    if (WorkerStages.TryParse(update.Stage, out WorkerStage stage))
                    {
                        _stage = stage;
                    }

                    progress?.Report(update);
                    return true;

                case WarningMessage warning:
                    _warnings.Add(warning);
                    return true;

                case ResultMessage or ErrorMessage or CancelledMessage:
                    _terminal = message;
                    RecordTerminalStage(message);

                    // Closing stdin tells a well-behaved worker there is nothing more to
                    // read; the exit grace period covers the case where it does not care.
                    CloseStandardInput();
                    StartExitGrace();
                    return true;

                case HelloMessage or StartJobMessage or CancelMessage:
                    _protocolFailure = string.Create(
                        CultureInfo.InvariantCulture,
                        $"the worker sent a host-only message '{message.Type}'");
                    return false;

                default:
                    _protocolFailure = string.Create(
                        CultureInfo.InvariantCulture,
                        $"unexpected message '{message.Type}'");
                    return false;
            }
        }

        private bool HandleReady(ReadyMessage ready)
        {
            if (_readyReceived)
            {
                _protocolFailure = "the worker completed the handshake twice";
                return false;
            }

            // Both sides must agree. A worker that speaks only a version this host does not
            // is refused here rather than discovered halfway through a job.
            if (!WorkerProtocol.IsSupported(ready.ProtocolVersion) ||
                !ready.SupportedProtocolVersions.Any(WorkerProtocol.IsSupported))
            {
                _protocolFailure = string.Create(
                    CultureInfo.InvariantCulture,
                    $"the worker speaks protocol {string.Join(",", ready.SupportedProtocolVersions)}; this host speaks {string.Join(",", WorkerProtocol.SupportedVersions)}");
                return false;
            }

            _readyReceived = true;
            _stage = WorkerStage.Accepting;
            _environment = new WorkerEnvironment(
                ready.WorkerVersion,
                ready.PythonVersion,
                ready.ProtocolVersion,
                ready.Backends);

            Send(startJob);

            return true;
        }

        private void RecordTerminalStage(WorkerMessage message)
        {
            string? wire = message switch
            {
                ErrorMessage error => error.Stage,
                CancelledMessage cancelled => cancelled.Stage,
                _ => null,
            };

            if (wire is not null && WorkerStages.TryParse(wire, out WorkerStage stage))
            {
                _stage = stage;
            }
            else if (message is ResultMessage)
            {
                _stage = WorkerStage.Finished;
            }
        }

        // -- outcome -----------------------------------------------------------------------

        private WorkerRunResult Resolve(int? exitCode, string stderr, CancellationToken cancellationToken)
        {
            WorkerRunResult Build(WorkerOutcome outcome, TranscriptionOutput? output = null, WorkerError? error = null) => new()
            {
                Outcome = outcome,
                Output = output,
                Error = error,
                ExitCode = exitCode,
                StandardErrorTail = stderr,
                Environment = _environment,
                Warnings = _warnings,
                ProtocolViolations = _violations,
            };

            if (_protocolFailure is not null)
            {
                return Build(
                    WorkerOutcome.ProtocolError,
                    error: new WorkerError(WorkerErrorCodes.ProtocolError, _stage, _protocolFailure));
            }

            switch (_terminal)
            {
                case ResultMessage result:
                {
                    string? mismatch = VerifyOutput(result);
                    if (mismatch is not null)
                    {
                        // The worker described a file it did not write. That is a broken
                        // worker, not a failed transcription, and the difference matters.
                        return Build(
                            WorkerOutcome.ProtocolError,
                            error: new WorkerError(WorkerErrorCodes.ProtocolError, WorkerStage.WritingOutput, mismatch));
                    }

                    return Build(
                        WorkerOutcome.Succeeded,
                        new TranscriptionOutput(
                            result.OutputPath,
                            result.Sha256,
                            result.SegmentCount,
                            result.DurationSeconds));
                }

                case CancelledMessage:
                    return Build(WorkerOutcome.Cancelled);

                case ErrorMessage error:
                {
                    // The codec refuses an error message with an unknown stage, so this
                    // always parses; the fallback only exists so the switch is total.
                    WorkerStage stage = WorkerStages.TryParse(error.Stage, out WorkerStage parsed)
                        ? parsed
                        : _stage;

                    return Build(
                        WorkerOutcome.Failed,
                        error: new WorkerError(error.Code, stage, error.Detail, error.Retryable));
                }
            }

            if (_timedOut)
            {
                return Build(WorkerOutcome.TimedOut);
            }

            if (_cancelRequested || cancellationToken.IsCancellationRequested)
            {
                return Build(WorkerOutcome.Cancelled);
            }

            return Build(
                WorkerOutcome.Crashed,
                error: new WorkerError(
                    WorkerErrorCodes.InternalError,
                    _stage,
                    _readyReceived
                        ? "the worker exited without a terminal message"
                        : "the worker exited before completing the handshake"));
        }

        /// <summary>
        /// Checks that the transcript on disk is the one the worker claims to have written.
        ///
        /// <para>
        /// The host does not take the worker's word for its own output. A digest that does
        /// not match means the bytes on disk are not what was reported, and activating a
        /// revision on that basis would put an unverified file where a canonical one belongs.
        /// </para>
        /// </summary>
        private static string? VerifyOutput(ResultMessage result)
        {
            try
            {
                if (!File.Exists(result.OutputPath))
                {
                    return "the worker reported an output file that does not exist";
                }

                using FileStream stream = File.OpenRead(result.OutputPath);
                string actual = Convert.ToHexStringLower(SHA256.HashData(stream));

                return string.Equals(actual, result.Sha256, StringComparison.Ordinal)
                    ? null
                    : "the transcript on disk does not match the digest the worker reported";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // Deliberately does not quote the path: this string reaches a log, and a
                // session path is private even when the message is only a diagnostic.
                return string.Create(CultureInfo.InvariantCulture, $"the transcript could not be verified: {ex.GetType().Name}");
            }
        }

        // -- stopping ----------------------------------------------------------------------

        private void OnTimeout()
        {
            _timedOut = true;

            // No grace period. It has already had all the time it was given.
            Terminate();
        }

        private void OnCancelRequested()
        {
            _cancelRequested = true;

            if (_readyReceived && _terminal is null)
            {
                Send(new CancelMessage { JobId = jobId, Reason = "user" });
            }

            // Ask first, kill second. A worker that stops at a safe boundary leaves its
            // sources and any previous revision exactly as they were.
            _graceSource = new CancellationTokenSource(options.CancelGracePeriod);
            _graceRegistration = _graceSource.Token.Register(Terminate);
        }

        private void StartExitGrace()
        {
            _graceSource?.Dispose();
            _graceSource = new CancellationTokenSource(options.ExitGracePeriod);
            _graceRegistration.Dispose();
            _graceRegistration = _graceSource.Token.Register(Terminate);
        }

        private void Terminate()
        {
            lock (_terminateLock)
            {
                if (_terminated)
                {
                    return;
                }

                _terminated = true;
            }

            // The Job Object first: it takes the whole tree, including anything the worker
            // started that a plain kill would leave holding a GPU.
            _job?.TerminateAll();

            Process? process = _process;
            if (process is null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // It exited between the check and the kill, which is the outcome we wanted.
            }
        }

        private async Task<int?> WaitForExitAsync(Process process)
        {
            try
            {
                using CancellationTokenSource bound = new(options.ExitGracePeriod);
                await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or SystemException)
            {
                return null;
            }
        }

        // -- streams -----------------------------------------------------------------------

        private void Send(WorkerMessage message)
        {
            string line = WorkerMessageCodec.Serialize(message);

            // Writes can come from the read loop and from a cancellation callback on the
            // thread pool. Half a line on the worker's stdin would be unrecoverable.
            lock (_writeLock)
            {
                try
                {
                    StreamWriter? writer = _process?.StandardInput;
                    if (writer is null)
                    {
                        return;
                    }

                    writer.Write(line);
                    writer.Write('\n');
                    writer.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
                {
                    // The worker is gone. The read loop will see the closed pipe and stop.
                }
            }
        }

        private void CloseStandardInput()
        {
            lock (_writeLock)
            {
                try
                {
                    _process?.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // Already closed, which is the state we were after.
                }
            }
        }

        /// <summary>
        /// Reads stderr continuously and keeps a bounded tail.
        ///
        /// <para>
        /// It must be drained rather than merely redirected: a child that fills the stderr
        /// pipe while nobody reads it blocks forever, and the deadlock would look exactly
        /// like a slow model.
        /// </para>
        /// </summary>
        private async Task<string> DrainStandardErrorAsync(Process process)
        {
            char[] buffer = new char[4096];
            StreamReader reader = process.StandardError;

            while (true)
            {
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    break;
                }

                if (read <= 0)
                {
                    break;
                }

                lock (_standardError)
                {
                    _standardError.Append(buffer, 0, read);
                    int excess = _standardError.Length - options.StandardErrorCharacterLimit;
                    if (excess > 0)
                    {
                        _standardError.Remove(0, excess);
                    }
                }
            }

            lock (_standardError)
            {
                return _standardError.ToString();
            }
        }

        private async Task<string> CollectStandardErrorAsync(Task<string> drain)
        {
            try
            {
                using CancellationTokenSource bound = new(options.ExitGracePeriod);
                return await drain.WaitAsync(bound.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                lock (_standardError)
                {
                    return _standardError.ToString();
                }
            }
        }

        public void Dispose()
        {
            // Terminate before releasing anything: the last thing to go is the Job Object
            // handle, and nothing may outlive this method.
            Terminate();

            _timeoutRegistration.Dispose();
            _cancelRegistration.Dispose();
            _graceRegistration.Dispose();
            _timeoutSource?.Dispose();
            _graceSource?.Dispose();
            _process?.Dispose();
            _job?.Dispose();
        }

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    }
}
