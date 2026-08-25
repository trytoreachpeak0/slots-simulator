namespace SQCD_8005AGV_Simulator.Core.Models;

public sealed record SimulatorSnapshot(
    string SchemaVersion,
    string InstanceId,
    string RunId,
    long Revision,
    IReadOnlyList<bool> DoStates,
    IReadOnlyList<bool> DiStates,
    IReadOnlyList<SlotSnapshot> Slots,
    int OpenDoorCount,
    int MaxOpenDoors,
    ModbusFaultSnapshot ModbusFault,
    DateTimeOffset UpdatedAt);

public sealed record SlotSnapshot(
    int SlotIndex,
    int DisplayNumber,
    int UnlockDoChannel,
    int LockFeedbackDiChannel,
    int LightCurtainDiChannel,
    bool UnlockDoRaw,
    bool LockFeedbackDiRaw,
    bool LightCurtainDiRaw,
    bool IsUnlockOutputActive,
    bool IsLocked,
    bool IsObstructed,
    bool DoorOpen,
    bool CargoPresent,
    bool LockFeedbackPending,
    bool LightCurtainFeedbackPending,
    bool? LockFeedbackOverride,
    bool? LightCurtainOverride);

public sealed record ModbusFaultSnapshot(ModbusFaultMode Mode, int DelayMs);

public enum ModbusFaultMode
{
    Normal,
    NoResponse,
    Disconnect,
    Delay
}
