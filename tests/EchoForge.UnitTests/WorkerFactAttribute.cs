namespace EchoForge.UnitTests;

/// <summary>
/// A fact that needs a real Python worker, and skips with a useful message when there is
/// none.
///
/// <para>
/// The reason is resolved at discovery, so the skip says what is missing and what to install
/// rather than failing in a way that reads like a defect in EchoForge. A developer without
/// Python 3.12 still gets a green run and a clear note; a build that is supposed to have it
/// will notice the skip.
/// </para>
/// </summary>
public sealed class WorkerFactAttribute : FactAttribute
{
    public WorkerFactAttribute()
    {
        string? reason = WorkerTestEnvironment.UnavailableReason;
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}

/// <summary>The theory counterpart of <see cref="WorkerFactAttribute"/>.</summary>
public sealed class WorkerTheoryAttribute : TheoryAttribute
{
    public WorkerTheoryAttribute()
    {
        string? reason = WorkerTestEnvironment.UnavailableReason;
        if (reason is not null)
        {
            Skip = reason;
        }
    }
}
