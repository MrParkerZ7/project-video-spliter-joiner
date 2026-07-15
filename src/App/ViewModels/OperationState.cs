namespace VideoSplitJoiner.App.ViewModels;

/// <summary>
/// Lifecycle state of a single long-running operation (split/join/detect) as tracked by
/// <see cref="OperationViewModel"/>.
/// </summary>
public enum OperationState
{
    /// <summary>No operation has run, or the VM was reset.</summary>
    Idle,

    /// <summary>An operation is in progress.</summary>
    Running,

    /// <summary>The operation finished successfully.</summary>
    Completed,

    /// <summary>The operation failed; <see cref="OperationViewModel.Error"/> carries the details.</summary>
    Failed,

    /// <summary>The operation was cancelled by the user.</summary>
    Cancelled,
}
