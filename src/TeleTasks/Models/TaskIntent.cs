namespace TeleTasks.Models;

/// <summary>
/// The verb the user wants to perform. Determined by the matcher and used by
/// <c>MessageRouter</c> to dispatch to the correct handler without inspecting
/// virtual-route task names. <see cref="Run"/> is the default when the model
/// omits the field or the message is an exact task-name hit.
/// </summary>
public enum TaskIntent
{
    Run,
    Show,
    Status,
    Stop,
    Restart,
    Cancel,
    Help
}
