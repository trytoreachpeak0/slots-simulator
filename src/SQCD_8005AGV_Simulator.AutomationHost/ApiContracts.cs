namespace SQCD_8005AGV_Simulator.AutomationHost;

public sealed record ResetRequest(string? RunId, string? CommandId, long? ExpectedRevision);

public sealed record CargoRequest(
    string? RunId,
    string? CommandId,
    long? ExpectedRevision,
    string? State);

public sealed record OverrideRequest(
    string? RunId,
    string? CommandId,
    long? ExpectedRevision,
    string? Mode);

public sealed record ModbusFaultRequest(
    string? RunId,
    string? CommandId,
    long? ExpectedRevision,
    string? Mode,
    int? DelayMs);

public sealed record HealthResponse(
    string SchemaVersion,
    string InstanceId,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    string Status,
    ApiModbusState Modbus);

public sealed record SnapshotResponse(
    string SchemaVersion,
    string InstanceId,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    ApiModbusState Modbus,
    IReadOnlyList<int> DoStates,
    IReadOnlyList<int> DiStates,
    IReadOnlyList<ApiSlotSnapshot> Slots,
    int OpenDoorCount,
    int MaxOpenDoors,
    IReadOnlyList<string> Faults);

public sealed record WriteResponse(
    string SchemaVersion,
    string InstanceId,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    string CommandId,
    bool Changed,
    bool Replayed,
    long AppliedRevision,
    SnapshotResponse Snapshot);

public sealed record ErrorResponse(
    string SchemaVersion,
    string InstanceId,
    string RunId,
    long Revision,
    DateTimeOffset ObservedAt,
    string? CommandId,
    string ReasonCode,
    string Message);

public sealed record ApiModbusState(
    string ListenAddress,
    int Port,
    int UnitId,
    bool IsRunning,
    int ClientCount,
    string FaultMode,
    int DelayMs);

public sealed record ApiSlotSnapshot(
    int SlotNo,
    string DoorState,
    string CargoState,
    int UnlockOutputRaw,
    int LockFeedbackRaw,
    int LightCurtainRaw,
    bool LockFeedbackPending,
    bool LightCurtainFeedbackPending,
    IReadOnlyList<string> Faults);
