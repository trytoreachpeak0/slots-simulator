using SQCD_8005AGV_Simulator.Core.Configuration;
using SQCD_8005AGV_Simulator.Core.Models;

namespace SQCD_8005AGV_Simulator.Core.Services;

public sealed class SimulatorEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly bool[] _doStates;
    private readonly bool[] _diStates;
    private readonly bool[] _doorOpen;
    private readonly bool[] _cargoPresent;
    private readonly bool?[] _lockOverrides;
    private readonly bool?[] _lightOverrides;
    private readonly CancellationTokenSource?[] _pulseTokens;
    private readonly long[] _lockFeedbackVersions;
    private readonly long[] _lightFeedbackVersions;
    private readonly bool[] _lockFeedbackPending;
    private readonly bool[] _lightFeedbackPending;
    private readonly Dictionary<string, CommandReceipt> _commandReceipts = new(StringComparer.Ordinal);
    private CancellationTokenSource _lifetime = new();
    private ResetReceipt? _lastResetReceipt;
    private long _generation;
    private string _runId = string.Empty;
    private long _revision;
    private ModbusFaultMode _modbusFaultMode;
    private int _modbusDelayMs;

    public SimulatorEngine(SimulatorSettings settings)
    {
        Settings = settings;
        Settings.Validate();
        _doStates = new bool[settings.Modbus.DoChannelCount];
        _diStates = new bool[settings.Modbus.DiChannelCount];
        _doorOpen = new bool[settings.Slots.Count];
        _cargoPresent = new bool[settings.Slots.Count];
        _lockOverrides = new bool?[settings.Slots.Count];
        _lightOverrides = new bool?[settings.Slots.Count];
        _pulseTokens = new CancellationTokenSource?[settings.Modbus.DoChannelCount];
        _lockFeedbackVersions = new long[settings.Slots.Count];
        _lightFeedbackVersions = new long[settings.Slots.Count];
        _lockFeedbackPending = new bool[settings.Slots.Count];
        _lightFeedbackPending = new bool[settings.Slots.Count];

        lock (_sync)
            ResetCoreNoLock();
    }

    public SimulatorSettings Settings { get; }

    public event EventHandler? StateChanged;
    public event EventHandler? ModbusFaultChanged;
    public event EventHandler<string>? LogEmitted;

    public bool ReadDo(int channel)
    {
        lock (_sync)
        {
            ValidateChannel(channel, _doStates.Length, nameof(channel));
            return _doStates[channel];
        }
    }

    public bool ReadDi(int channel)
    {
        lock (_sync)
        {
            ValidateChannel(channel, _diStates.Length, nameof(channel));
            return _diStates[channel];
        }
    }

    public bool[] ReadDoRange(int startChannel, int count)
    {
        lock (_sync)
        {
            ValidateRange(startChannel, count, _doStates.Length);
            return _doStates.Skip(startChannel).Take(count).ToArray();
        }
    }

    public bool[] ReadDiRange(int startChannel, int count)
    {
        lock (_sync)
        {
            ValidateRange(startChannel, count, _diStates.Length);
            return _diStates.Skip(startChannel).Take(count).ToArray();
        }
    }

    public ModbusFaultSnapshot GetModbusFault()
    {
        lock (_sync)
            return new ModbusFaultSnapshot(_modbusFaultMode, _modbusDelayMs);
    }

    public void WriteDo(int channel, bool value)
    {
        List<string> logs = [];
        bool changed;
        lock (_sync)
        {
            ValidateChannel(channel, _doStates.Length, nameof(channel));
            changed = SetDoNoLock(channel, value);
            changed |= ProcessDoBehaviorNoLock(channel, value, logs);
            if (changed)
                _revision++;
        }

        EmitLogs(logs);
        if (changed)
            RaiseStateChanged();
    }

    public void WriteDoBatch(int startChannel, IReadOnlyList<bool> values)
    {
        if (values.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(values));

        List<string> logs = [];
        var changed = false;
        lock (_sync)
        {
            ValidateRange(startChannel, values.Count, _doStates.Length);
            for (var i = 0; i < values.Count; i++)
            {
                var channel = startChannel + i;
                changed |= SetDoNoLock(channel, values[i]);
                changed |= ProcessDoBehaviorNoLock(channel, values[i], logs);
            }

            if (changed)
                _revision++;
        }

        EmitLogs(logs);
        if (changed)
            RaiseStateChanged();
    }

    public OperationResult PlaceCargo(int slotIndex)
    {
        TransitionOutcome outcome;
        lock (_sync)
        {
            outcome = SetCargoNoLock(slotIndex, true, false);
            CommitDirectTransitionNoLock(outcome);
        }

        PublishTransition(outcome);
        return ToOperationResult(outcome);
    }

    public OperationResult RemoveCargo(int slotIndex)
    {
        TransitionOutcome outcome;
        lock (_sync)
        {
            outcome = SetCargoNoLock(slotIndex, false, false);
            CommitDirectTransitionNoLock(outcome);
        }

        PublishTransition(outcome);
        return ToOperationResult(outcome);
    }

    public OperationResult CloseDoor(int slotIndex)
    {
        TransitionOutcome outcome;
        lock (_sync)
        {
            outcome = CloseDoorNoLock(slotIndex, false);
            CommitDirectTransitionNoLock(outcome);
        }

        PublishTransition(outcome);
        return ToOperationResult(outcome);
    }

    public void SetLockFeedbackOverride(int slotIndex, bool? rawValue)
    {
        TransitionOutcome outcome;
        lock (_sync)
        {
            outcome = SetFeedbackOverrideNoLock(slotIndex, true, rawValue);
            CommitDirectTransitionNoLock(outcome);
        }

        PublishTransition(outcome);
    }

    public void SetLightCurtainOverride(int slotIndex, bool? rawValue)
    {
        TransitionOutcome outcome;
        lock (_sync)
        {
            outcome = SetFeedbackOverrideNoLock(slotIndex, false, rawValue);
            CommitDirectTransitionNoLock(outcome);
        }

        PublishTransition(outcome);
    }

    public OperationResult SetModbusFault(ModbusFaultMode mode, int delayMs = 0)
    {
        TransitionOutcome outcome;
        lock (_sync)
        {
            outcome = SetModbusFaultNoLock(mode, delayMs);
            CommitDirectTransitionNoLock(outcome);
        }

        PublishTransition(outcome);
        return ToOperationResult(outcome);
    }

    public void Reset()
    {
        lock (_sync)
        {
            ResetCoreNoLock();
            _lastResetReceipt = null;
        }

        EmitLog("仿真器已执行安全 Reset：全部 DO 断开、仓门关闭、仓位清空、故障覆盖清除。");
        ModbusFaultChanged?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
    }

    public SimulatorCommandResult ExecuteSetCargoCommand(
        SimulatorCommandContext context,
        int slotNo,
        bool cargoPresent)
    {
        var fingerprint = $"PUT|slots/{slotNo}/cargo|{(cargoPresent ? "OCCUPIED" : "EMPTY")}";
        return ExecuteCommand(context, fingerprint, () =>
        {
            var slotIndex = GetSlotIndexByDisplayNumber(slotNo);
            return slotIndex < 0
                ? TransitionOutcome.Fail("INVALID_SLOT_NO", "slotNo 必须在已配置的物理仓号范围内。")
                : SetCargoNoLock(slotIndex, cargoPresent, true);
        });
    }

    public SimulatorCommandResult ExecuteCloseDoorCommand(SimulatorCommandContext context, int slotNo)
    {
        var fingerprint = $"POST|slots/{slotNo}/close-door";
        return ExecuteCommand(context, fingerprint, () =>
        {
            var slotIndex = GetSlotIndexByDisplayNumber(slotNo);
            return slotIndex < 0
                ? TransitionOutcome.Fail("INVALID_SLOT_NO", "slotNo 必须在已配置的物理仓号范围内。")
                : CloseDoorNoLock(slotIndex, true);
        });
    }

    public SimulatorCommandResult ExecuteLockOverrideCommand(
        SimulatorCommandContext context,
        int slotNo,
        bool? rawValue)
    {
        var fingerprint = $"PUT|slots/{slotNo}/lock-feedback-override|{FormatOverride(rawValue)}";
        return ExecuteCommand(context, fingerprint, () =>
        {
            var slotIndex = GetSlotIndexByDisplayNumber(slotNo);
            return slotIndex < 0
                ? TransitionOutcome.Fail("INVALID_SLOT_NO", "slotNo 必须在已配置的物理仓号范围内。")
                : SetFeedbackOverrideNoLock(slotIndex, true, rawValue);
        });
    }

    public SimulatorCommandResult ExecuteLightOverrideCommand(
        SimulatorCommandContext context,
        int slotNo,
        bool? rawValue)
    {
        var fingerprint = $"PUT|slots/{slotNo}/light-curtain-override|{FormatOverride(rawValue)}";
        return ExecuteCommand(context, fingerprint, () =>
        {
            var slotIndex = GetSlotIndexByDisplayNumber(slotNo);
            return slotIndex < 0
                ? TransitionOutcome.Fail("INVALID_SLOT_NO", "slotNo 必须在已配置的物理仓号范围内。")
                : SetFeedbackOverrideNoLock(slotIndex, false, rawValue);
        });
    }

    public SimulatorCommandResult ExecuteModbusFaultCommand(
        SimulatorCommandContext context,
        ModbusFaultMode mode,
        int delayMs)
    {
        var effectiveDelay = mode == ModbusFaultMode.Delay ? delayMs : 0;
        var fingerprint = $"PUT|faults/modbus|{mode.ToString().ToUpperInvariant()}|{effectiveDelay}";
        return ExecuteCommand(context, fingerprint, () => SetModbusFaultNoLock(mode, effectiveDelay));
    }

    public SimulatorCommandResult ExecuteResetCommand(SimulatorCommandContext context)
    {
        const string fingerprint = "POST|reset";
        SimulatorCommandResult result;
        lock (_sync)
        {
            if (!TryValidateCommandContext(context, out var validationCode, out var validationError))
                return CreateCommandFailureNoLock(context.CommandId, validationCode!, validationError!);

            if (_lastResetReceipt is { } resetReceipt &&
                string.Equals(resetReceipt.CommandId, context.CommandId, StringComparison.Ordinal))
            {
                if (string.Equals(resetReceipt.SourceRunId, context.RunId, StringComparison.Ordinal) &&
                    string.Equals(resetReceipt.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return new SimulatorCommandResult(
                        true,
                        null,
                        null,
                        context.CommandId,
                        true,
                        true,
                        resetReceipt.AppliedRevision,
                        CreateSnapshotNoLock());
                }

                return CreateCommandFailureNoLock(
                    context.CommandId,
                    "COMMAND_ID_CONFLICT",
                    "相同Reset commandId已用于不同测试轮次。");
            }

            if (!string.Equals(context.RunId, _runId, StringComparison.Ordinal))
                return CreateCommandFailureNoLock(context.CommandId, "RUN_ID_MISMATCH", "请求runId不是当前测试轮次。");
            if (context.ExpectedRevision != _revision)
                return CreateCommandFailureNoLock(context.CommandId, "REVISION_CONFLICT", "expectedRevision与当前revision不一致。");

            var sourceRunId = _runId;
            ResetCoreNoLock();
            _lastResetReceipt = new ResetReceipt(sourceRunId, context.CommandId, fingerprint, _revision);
            result = new SimulatorCommandResult(
                true,
                null,
                null,
                context.CommandId,
                true,
                false,
                _revision,
                CreateSnapshotNoLock());
        }

        EmitLog("外部自动化接口执行安全 Reset，已建立新的测试轮次。");
        ModbusFaultChanged?.Invoke(this, EventArgs.Empty);
        RaiseStateChanged();
        return result;
    }

    public SimulatorSnapshot GetSnapshot()
    {
        lock (_sync)
            return CreateSnapshotNoLock();
    }

    private SimulatorCommandResult ExecuteCommand(
        SimulatorCommandContext context,
        string fingerprint,
        Func<TransitionOutcome> transitionFactory)
    {
        TransitionOutcome? outcome;
        SimulatorCommandResult result;
        lock (_sync)
        {
            if (!TryValidateCommandContext(context, out var validationCode, out var validationError))
                return CreateCommandFailureNoLock(context.CommandId, validationCode!, validationError!);
            if (!string.Equals(context.RunId, _runId, StringComparison.Ordinal))
                return CreateCommandFailureNoLock(context.CommandId, "RUN_ID_MISMATCH", "请求runId不是当前测试轮次。");

            if (_commandReceipts.TryGetValue(context.CommandId, out var receipt))
            {
                if (!string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return CreateCommandFailureNoLock(context.CommandId, "COMMAND_ID_CONFLICT", "相同commandId对应的命令内容不同。");

                return new SimulatorCommandResult(
                    receipt.IsSuccess,
                    receipt.ReasonCode,
                    receipt.Error,
                    context.CommandId,
                    receipt.Changed,
                    true,
                    receipt.AppliedRevision,
                    CreateSnapshotNoLock());
            }

            if (context.ExpectedRevision != _revision)
                return CreateCommandFailureNoLock(context.CommandId, "REVISION_CONFLICT", "expectedRevision与当前revision不一致。");

            outcome = transitionFactory();
            if (outcome.IsSuccess && outcome.Changed)
                _revision++;

            var appliedRevision = _revision;
            _commandReceipts[context.CommandId] = new CommandReceipt(
                fingerprint,
                outcome.IsSuccess,
                outcome.ReasonCode,
                outcome.Error,
                outcome.Changed,
                appliedRevision);

            result = new SimulatorCommandResult(
                outcome.IsSuccess,
                outcome.ReasonCode,
                outcome.Error,
                context.CommandId,
                outcome.Changed,
                false,
                appliedRevision,
                CreateSnapshotNoLock());
        }

        PublishTransition(outcome);
        return result;
    }

    private SimulatorSnapshot CreateSnapshotNoLock()
    {
        var slots = Settings.Slots.OrderBy(x => x.SlotIndex).Select((slot, arrayIndex) =>
        {
            var doRaw = _doStates[slot.UnlockDoChannel];
            var lockRaw = _diStates[slot.LockFeedbackDiChannel];
            var lightRaw = _diStates[slot.LightCurtainDiChannel];
            return new SlotSnapshot(
                slot.SlotIndex,
                slot.DisplayNumber,
                slot.UnlockDoChannel,
                slot.LockFeedbackDiChannel,
                slot.LightCurtainDiChannel,
                doRaw,
                lockRaw,
                lightRaw,
                Convert.ToInt32(doRaw) == slot.UnlockActiveLevel,
                Convert.ToInt32(lockRaw) == slot.LockedActiveLevel,
                Convert.ToInt32(lightRaw) == slot.ObstructedActiveLevel,
                _doorOpen[arrayIndex],
                _cargoPresent[arrayIndex],
                _lockFeedbackPending[arrayIndex],
                _lightFeedbackPending[arrayIndex],
                _lockOverrides[arrayIndex],
                _lightOverrides[arrayIndex]);
        }).ToArray();

        return new SimulatorSnapshot(
            Settings.SchemaVersion,
            Settings.InstanceId,
            _runId,
            _revision,
            _doStates.ToArray(),
            _diStates.ToArray(),
            slots,
            _doorOpen.Count(value => value),
            Settings.Defaults.MaxOpenDoors,
            new ModbusFaultSnapshot(_modbusFaultMode, _modbusDelayMs),
            DateTimeOffset.UtcNow);
    }

    private TransitionOutcome SetCargoNoLock(int slotIndex, bool cargoPresent, bool allowNoChange)
    {
        var index = GetSlotArrayIndex(slotIndex);
        var displayNumber = GetOrderedSlot(index).DisplayNumber;
        if (_cargoPresent[index] == cargoPresent)
        {
            return allowNoChange
                ? TransitionOutcome.Success(false)
                : TransitionOutcome.Fail(
                    cargoPresent ? "CARGO_ALREADY_OCCUPIED" : "CARGO_ALREADY_EMPTY",
                    cargoPresent ? "仓位已有货物。" : "仓位没有货物。");
        }

        if (!_doorOpen[index])
            return TransitionOutcome.Fail("DOOR_NOT_OPEN", cargoPresent ? "仓门未打开，不能放料。" : "仓门未打开，不能取料。");

        _cargoPresent[index] = cargoPresent;
        ScheduleLightFeedbackNoLock(index, cargoPresent);
        return TransitionOutcome.Success(true, $"仓位 {displayNumber}：模拟{(cargoPresent ? "放料" : "取料")}。");
    }

    private TransitionOutcome CloseDoorNoLock(int slotIndex, bool allowNoChange)
    {
        var index = GetSlotArrayIndex(slotIndex);
        var displayNumber = GetOrderedSlot(index).DisplayNumber;
        if (!_doorOpen[index])
        {
            return allowNoChange
                ? TransitionOutcome.Success(false)
                : TransitionOutcome.Fail("DOOR_ALREADY_CLOSED", "仓门已经关闭。");
        }

        _doorOpen[index] = false;
        ScheduleLockFeedbackNoLock(index, true, Settings.Defaults.LockFeedbackDelayMs);
        return TransitionOutcome.Success(true, $"仓位 {displayNumber}：用户关闭仓门。");
    }

    private TransitionOutcome SetFeedbackOverrideNoLock(int slotIndex, bool isLockFeedback, bool? rawValue)
    {
        var index = GetSlotArrayIndex(slotIndex);
        var displayNumber = GetOrderedSlot(index).DisplayNumber;
        var current = isLockFeedback ? _lockOverrides[index] : _lightOverrides[index];
        if (current == rawValue)
            return TransitionOutcome.Success(false);

        if (isLockFeedback)
        {
            _lockFeedbackVersions[index]++;
            _lockFeedbackPending[index] = false;
            _lockOverrides[index] = rawValue;
            RebuildLockFeedbackNoLock(index);
        }
        else
        {
            _lightFeedbackVersions[index]++;
            _lightFeedbackPending[index] = false;
            _lightOverrides[index] = rawValue;
            RebuildLightFeedbackNoLock(index);
        }

        var signalText = isLockFeedback ? "锁反馈" : "光幕";
        var valueText = rawValue.HasValue ? Convert.ToInt32(rawValue.Value).ToString() : "正常";
        return TransitionOutcome.Success(true, $"仓位 {displayNumber}：{signalText}覆盖={valueText}。");
    }

    private TransitionOutcome SetModbusFaultNoLock(ModbusFaultMode mode, int delayMs)
    {
        if (mode == ModbusFaultMode.Delay && delayMs is < 1 or > 60000)
            return TransitionOutcome.Fail("INVALID_DELAY", "DELAY模式的delayMs必须在1～60000之间。");

        var effectiveDelay = mode == ModbusFaultMode.Delay ? delayMs : 0;
        if (_modbusFaultMode == mode && _modbusDelayMs == effectiveDelay)
            return TransitionOutcome.Success(false);

        _modbusFaultMode = mode;
        _modbusDelayMs = effectiveDelay;
        return TransitionOutcome.Success(
            true,
            $"Modbus故障模式已切换为 {mode}{(mode == ModbusFaultMode.Delay ? $" ({effectiveDelay}ms)" : string.Empty)}。",
            true);
    }

    private bool ProcessDoBehaviorNoLock(int channel, bool rawValue, List<string> logs)
    {
        var indexedSlot = Settings.Slots.OrderBy(x => x.SlotIndex)
            .Select((slot, index) => (slot, index))
            .FirstOrDefault(x => x.slot.UnlockDoChannel == channel);

        if (indexedSlot.slot is null)
            return false;

        var changed = false;
        var active = Convert.ToInt32(rawValue) == indexedSlot.slot.UnlockActiveLevel;
        if (active)
        {
            var outputMode = Enum.Parse<OutputMode>(Settings.Defaults.OutputMode, true);
            changed |= ScheduleLockFeedbackNoLock(indexedSlot.index, false, Settings.Defaults.UnlockFeedbackDelayMs);
            if (Settings.Defaults.AutoPopDoorOnUnlock && !_doorOpen[indexedSlot.index])
            {
                _doorOpen[indexedSlot.index] = true;
                changed = true;
            }

            var modeText = outputMode == OutputMode.Pulse
                ? $"Pulse {Settings.Defaults.PulseWidthMs}ms 开始"
                : "Level 输出保持";
            logs.Add($"仓位 {indexedSlot.slot.DisplayNumber}：DO{channel}={Convert.ToInt32(rawValue)}（继电器{GetRelayStateText(rawValue)}），开锁输出生效，{modeText}。");
            if (outputMode == OutputMode.Pulse)
                StartPulseNoLock(channel, indexedSlot.slot.UnlockActiveLevel != 0);
        }
        else
        {
            logs.Add($"仓位 {indexedSlot.slot.DisplayNumber}：DO{channel}={Convert.ToInt32(rawValue)}（继电器{GetRelayStateText(rawValue)}），开锁输出结束。");
            if (Enum.Parse<LockFeedbackModel>(Settings.Defaults.LockFeedbackModel, true) == LockFeedbackModel.FollowOutput)
                changed |= ScheduleLockFeedbackNoLock(indexedSlot.index, true, Settings.Defaults.LockFeedbackDelayMs);
        }

        return changed;
    }

    private void StartPulseNoLock(int channel, bool activeRawValue)
    {
        _pulseTokens[channel]?.Cancel();
        _pulseTokens[channel]?.Dispose();
        _pulseTokens[channel] = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _pulseTokens[channel]!.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Settings.Defaults.PulseWidthMs, token);
                WriteDo(channel, !activeRawValue);
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private bool ScheduleLockFeedbackNoLock(int slotArrayIndex, bool locked, int delayMs)
    {
        var generation = _generation;
        var version = ++_lockFeedbackVersions[slotArrayIndex];
        if (_lockOverrides[slotArrayIndex].HasValue)
        {
            var pendingChanged = _lockFeedbackPending[slotArrayIndex];
            _lockFeedbackPending[slotArrayIndex] = false;
            return pendingChanged;
        }

        var changed = !_lockFeedbackPending[slotArrayIndex];
        _lockFeedbackPending[slotArrayIndex] = true;
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
                int displayNumber;
                int diChannel;
                bool rawValue;
                bool stateChanged;
                lock (_sync)
                {
                    if (generation != _generation ||
                        version != _lockFeedbackVersions[slotArrayIndex] ||
                        _lockOverrides[slotArrayIndex].HasValue)
                    {
                        return;
                    }

                    stateChanged = SetLockRawNoLock(slotArrayIndex, locked);
                    stateChanged |= _lockFeedbackPending[slotArrayIndex];
                    _lockFeedbackPending[slotArrayIndex] = false;
                    if (stateChanged)
                        _revision++;
                    var slot = GetOrderedSlot(slotArrayIndex);
                    displayNumber = slot.DisplayNumber;
                    diChannel = slot.LockFeedbackDiChannel;
                    rawValue = _diStates[diChannel];
                }

                EmitLog($"仓位 {displayNumber}：锁反馈 DI{diChannel}={Convert.ToInt32(rawValue)}，仓门{(locked ? "已锁上" : "已解锁")}。");
                if (stateChanged)
                    RaiseStateChanged();
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
        return changed;
    }

    private bool ScheduleLightFeedbackNoLock(int slotArrayIndex, bool obstructed)
    {
        var generation = _generation;
        var version = ++_lightFeedbackVersions[slotArrayIndex];
        if (_lightOverrides[slotArrayIndex].HasValue)
        {
            var pendingChanged = _lightFeedbackPending[slotArrayIndex];
            _lightFeedbackPending[slotArrayIndex] = false;
            return pendingChanged;
        }

        var changed = !_lightFeedbackPending[slotArrayIndex];
        _lightFeedbackPending[slotArrayIndex] = true;
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Settings.Defaults.LightCurtainFeedbackDelayMs, token);
                bool stateChanged;
                lock (_sync)
                {
                    if (generation != _generation ||
                        version != _lightFeedbackVersions[slotArrayIndex] ||
                        _lightOverrides[slotArrayIndex].HasValue)
                    {
                        return;
                    }

                    stateChanged = SetLightRawNoLock(slotArrayIndex, obstructed);
                    stateChanged |= _lightFeedbackPending[slotArrayIndex];
                    _lightFeedbackPending[slotArrayIndex] = false;
                    if (stateChanged)
                        _revision++;
                }

                if (stateChanged)
                    RaiseStateChanged();
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
        return changed;
    }

    private bool RebuildLockFeedbackNoLock(int slotArrayIndex)
    {
        if (_lockOverrides[slotArrayIndex] is { } raw)
        {
            var slot = GetOrderedSlot(slotArrayIndex);
            return SetDiNoLock(slot.LockFeedbackDiChannel, raw);
        }

        return SetLockRawNoLock(slotArrayIndex, !_doorOpen[slotArrayIndex]);
    }

    private bool RebuildLightFeedbackNoLock(int slotArrayIndex)
    {
        if (_lightOverrides[slotArrayIndex] is { } raw)
        {
            var slot = GetOrderedSlot(slotArrayIndex);
            return SetDiNoLock(slot.LightCurtainDiChannel, raw);
        }

        return SetLightRawNoLock(slotArrayIndex, _cargoPresent[slotArrayIndex]);
    }

    private bool SetLockRawNoLock(int slotArrayIndex, bool locked)
    {
        var slot = GetOrderedSlot(slotArrayIndex);
        return SetDiNoLock(slot.LockFeedbackDiChannel, locked == (slot.LockedActiveLevel != 0));
    }

    private bool SetLightRawNoLock(int slotArrayIndex, bool obstructed)
    {
        var slot = GetOrderedSlot(slotArrayIndex);
        return SetDiNoLock(slot.LightCurtainDiChannel, obstructed == (slot.ObstructedActiveLevel != 0));
    }

    private bool SetDiNoLock(int channel, bool value)
    {
        if (_diStates[channel] == value)
            return false;
        _diStates[channel] = value;
        return true;
    }

    private bool SetDoNoLock(int channel, bool value)
    {
        _pulseTokens[channel]?.Cancel();
        _pulseTokens[channel]?.Dispose();
        _pulseTokens[channel] = null;
        if (_doStates[channel] == value)
            return false;
        _doStates[channel] = value;
        return true;
    }

    private void ResetCoreNoLock()
    {
        _generation++;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();

        foreach (var pulse in _pulseTokens)
        {
            pulse?.Cancel();
            pulse?.Dispose();
        }

        Array.Clear(_pulseTokens);
        Array.Clear(_doStates);
        Array.Clear(_doorOpen);
        Array.Clear(_cargoPresent);
        Array.Clear(_lockOverrides);
        Array.Clear(_lightOverrides);
        Array.Clear(_lockFeedbackVersions);
        Array.Clear(_lightFeedbackVersions);
        Array.Clear(_lockFeedbackPending);
        Array.Clear(_lightFeedbackPending);
        Array.Clear(_diStates);

        for (var i = 0; i < Settings.Slots.Count; i++)
        {
            SetLockRawNoLock(i, true);
            SetLightRawNoLock(i, false);
        }

        _modbusFaultMode = ModbusFaultMode.Normal;
        _modbusDelayMs = 0;
        _commandReceipts.Clear();
        _runId = $"run-{Guid.NewGuid():N}";
        _revision = 1;
    }

    private void CommitDirectTransitionNoLock(TransitionOutcome outcome)
    {
        if (outcome.IsSuccess && outcome.Changed)
            _revision++;
    }

    private SimulatorCommandResult CreateCommandFailureNoLock(string commandId, string reasonCode, string error) =>
        new(false, reasonCode, error, commandId, false, false, _revision, CreateSnapshotNoLock());

    private static bool TryValidateCommandContext(
        SimulatorCommandContext context,
        out string? reasonCode,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(context.RunId))
        {
            reasonCode = "INVALID_RUN_ID";
            error = "runId不能为空。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.CommandId) || context.CommandId.Length > 128)
        {
            reasonCode = "INVALID_COMMAND_ID";
            error = "commandId必须为1～128字符。";
            return false;
        }

        if (context.ExpectedRevision < 1)
        {
            reasonCode = "INVALID_EXPECTED_REVISION";
            error = "expectedRevision必须大于等于1。";
            return false;
        }

        reasonCode = null;
        error = null;
        return true;
    }

    private int GetSlotArrayIndex(int slotIndex)
    {
        var ordered = Settings.Slots.OrderBy(x => x.SlotIndex).ToList();
        var index = ordered.FindIndex(x => x.SlotIndex == slotIndex);
        return index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(slotIndex));
    }

    private int GetSlotIndexByDisplayNumber(int displayNumber)
    {
        var slot = Settings.Slots.SingleOrDefault(x => x.DisplayNumber == displayNumber);
        return slot?.SlotIndex ?? -1;
    }

    private SlotSettings GetOrderedSlot(int arrayIndex) =>
        Settings.Slots.OrderBy(x => x.SlotIndex).ElementAt(arrayIndex);

    private static string GetRelayStateText(bool rawValue) => rawValue ? "闭合" : "断开";
    private static string FormatOverride(bool? value) => value.HasValue ? (value.Value ? "FIXED_1" : "FIXED_0") : "AUTO";

    private static void ValidateChannel(int channel, int length, string paramName)
    {
        if (channel < 0 || channel >= length)
            throw new ArgumentOutOfRangeException(paramName);
    }

    private static void ValidateRange(int start, int count, int length)
    {
        if (start < 0 || count < 1 || start > length - count)
            throw new ArgumentOutOfRangeException(nameof(start));
    }

    private static OperationResult ToOperationResult(TransitionOutcome outcome) =>
        outcome.IsSuccess ? OperationResult.Success() : OperationResult.Fail(outcome.Error ?? "操作失败。");

    private void PublishTransition(TransitionOutcome? outcome)
    {
        if (outcome is null)
            return;
        if (!string.IsNullOrWhiteSpace(outcome.LogMessage))
            EmitLog(outcome.LogMessage);
        if (outcome.FaultChanged)
            ModbusFaultChanged?.Invoke(this, EventArgs.Empty);
        if (outcome.IsSuccess && outcome.Changed)
            RaiseStateChanged();
    }

    private void EmitLogs(IEnumerable<string> messages)
    {
        foreach (var message in messages)
            EmitLog(message);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    private void EmitLog(string message) => LogEmitted?.Invoke(this, $"{DateTime.Now:HH:mm:ss.fff}  {message}");

    public void Dispose()
    {
        lock (_sync)
        {
            _lifetime.Cancel();
            foreach (var pulse in _pulseTokens)
            {
                pulse?.Cancel();
                pulse?.Dispose();
            }
            _lifetime.Dispose();
        }
    }

    private sealed record CommandReceipt(
        string Fingerprint,
        bool IsSuccess,
        string? ReasonCode,
        string? Error,
        bool Changed,
        long AppliedRevision);

    private sealed record ResetReceipt(
        string SourceRunId,
        string CommandId,
        string Fingerprint,
        long AppliedRevision);

    private sealed record TransitionOutcome(
        bool IsSuccess,
        bool Changed,
        string? ReasonCode,
        string? Error,
        string? LogMessage,
        bool FaultChanged)
    {
        public static TransitionOutcome Success(bool changed, string? logMessage = null, bool faultChanged = false) =>
            new(true, changed, null, null, logMessage, faultChanged);

        public static TransitionOutcome Fail(string reasonCode, string error) =>
            new(false, false, reasonCode, error, null, false);
    }
}

public readonly record struct OperationResult(bool IsSuccess, string? Error)
{
    public static OperationResult Success() => new(true, null);
    public static OperationResult Fail(string error) => new(false, error);
}
