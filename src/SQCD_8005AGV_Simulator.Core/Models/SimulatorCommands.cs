namespace SQCD_8005AGV_Simulator.Core.Models;

public sealed record SimulatorCommandContext(
    string RunId,
    string CommandId,
    long ExpectedRevision);

public sealed record SimulatorCommandResult(
    bool IsSuccess,
    string? ReasonCode,
    string? Error,
    string CommandId,
    bool Changed,
    bool Replayed,
    long AppliedRevision,
    SimulatorSnapshot Snapshot)
{
    public bool IsConflict => ReasonCode is
        "RUN_ID_MISMATCH" or
        "REVISION_CONFLICT" or
        "COMMAND_ID_CONFLICT" or
        "DOOR_NOT_OPEN";
}
