namespace MQuark.Actionful.Client;

/// <summary>Status of an in-flight or completed endpoint invocation.</summary>
public enum InvocationStatus
{
    /// <summary>Submitted but not yet picked up.</summary>
    Pending,
    /// <summary>Currently executing.</summary>
    Running,
    /// <summary>Completed successfully. <see cref="InvocationJob.ResultJson"/> is populated.</summary>
    Succeeded,
    /// <summary>Completed with an error. <see cref="InvocationJob.Error"/> is populated.</summary>
    Failed,
    /// <summary>Cancelled before completion.</summary>
    Cancelled
}
