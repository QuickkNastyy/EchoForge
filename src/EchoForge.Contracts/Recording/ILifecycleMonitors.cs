namespace EchoForge.Contracts.Recording;

/// <summary>Why an endpoint stopped being usable.</summary>
public enum EndpointChange
{
    Removed,
    Disabled,
    Unplugged,
    NotPresent,
}

/// <summary>One endpoint changed state.</summary>
public sealed class EndpointChangedEventArgs(string endpointId, EndpointChange change) : EventArgs
{
    public string EndpointId { get; } = endpointId;

    public EndpointChange Change { get; } = change;
}

/// <summary>The system default endpoint moved. EchoForge reports this and never follows it.</summary>
public sealed class DefaultEndpointChangedEventArgs(string endpointId, bool isRender) : EventArgs
{
    public string EndpointId { get; } = endpointId;

    public bool IsRender { get; } = isRender;
}

/// <summary>
/// Watches Windows audio endpoints.
///
/// <para>
/// Failure detection must not depend on the UI poll loop: a removed endpoint should degrade the
/// session as soon as Windows says so. Abstracted so the behaviour can be driven synthetically in
/// tests without unplugging anything.
/// </para>
/// </summary>
public interface IEndpointMonitor : IDisposable
{
    /// <summary>Raised when an endpoint is removed, disabled, unplugged, or otherwise unusable.</summary>
    event EventHandler<EndpointChangedEventArgs>? EndpointLost;

    /// <summary>
    /// Raised when Windows changes its default endpoint. Informational only — EchoForge keeps
    /// recording the endpoints that were pinned at Start.
    /// </summary>
    event EventHandler<DefaultEndpointChangedEventArgs>? DefaultChanged;

    /// <summary>Begins watching. Safe to call once.</summary>
    void Start();
}

/// <summary>System power transitions.</summary>
public interface IPowerMonitor : IDisposable
{
    /// <summary>The machine is about to suspend. Handlers must finish quickly.</summary>
    event EventHandler? Suspending;

    /// <summary>The machine has resumed. EchoForge never restarts capture on its own.</summary>
    event EventHandler? Resumed;

    void Start();
}
