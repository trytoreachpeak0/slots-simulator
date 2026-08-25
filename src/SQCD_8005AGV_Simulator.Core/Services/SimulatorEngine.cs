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
    private CancellationTokenSource _lifetime = new();
    private long _generation;

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
        Reset();
    }

    public SimulatorSettings Settings { get; }

    public event EventHandler? StateChanged;
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

    public void WriteDo(int channel, bool value)
    {
        lock (_sync)
        {
            ValidateChannel(channel, _doStates.Length, nameof(channel));
            SetDoNoLock(channel, value);
        }

        ProcessDoBehavior(channel, value);
        RaiseStateChanged();
    }

    public void WriteDoBatch(int startChannel, IReadOnlyList<bool> values)
    {
        if (values.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(values));

        lock (_sync)
        {
            ValidateRange(startChannel, values.Count, _doStates.Length);
            for (var i = 0; i < values.Count; i++)
                SetDoNoLock(startChannel + i, values[i]);
        }

        for (var i = 0; i < values.Count; i++)
            ProcessDoBehavior(startChannel + i, values[i]);

        RaiseStateChanged();
    }

    public OperationResult PlaceCargo(int slotIndex)
    {
        int displayNumber;
        lock (_sync)
        {
            var index = GetSlotArrayIndex(slotIndex);
            displayNumber = GetOrderedSlot(index).DisplayNumber;
            if (!_doorOpen[index])
                return OperationResult.Fail("仓门未打开，不能放料。");
            if (_cargoPresent[index])
                return OperationResult.Fail("仓位已有货物。");

            _cargoPresent[index] = true;
            ScheduleLightFeedbackNoLock(index, true);
        }

        EmitLog($"仓位 {displayNumber}：模拟放料。");
        RaiseStateChanged();
        return OperationResult.Success();
    }

    public OperationResult RemoveCargo(int slotIndex)
    {
        int displayNumber;
        lock (_sync)
        {
            var index = GetSlotArrayIndex(slotIndex);
            displayNumber = GetOrderedSlot(index).DisplayNumber;
            if (!_doorOpen[index])
                return OperationResult.Fail("仓门未打开，不能取料。");
            if (!_cargoPresent[index])
                return OperationResult.Fail("仓位没有货物。");

            _cargoPresent[index] = false;
            ScheduleLightFeedbackNoLock(index, false);
        }

        EmitLog($"仓位 {displayNumber}：模拟取料。");
        RaiseStateChanged();
        return OperationResult.Success();
    }

    public OperationResult CloseDoor(int slotIndex)
    {
        int displayNumber;
        lock (_sync)
        {
            var index = GetSlotArrayIndex(slotIndex);
            displayNumber = GetOrderedSlot(index).DisplayNumber;
            if (!_doorOpen[index])
                return OperationResult.Fail("仓门已经关闭。");

            _doorOpen[index] = false;
            ScheduleLockFeedbackNoLock(index, true, Settings.Defaults.LockFeedbackDelayMs);
        }

        EmitLog($"仓位 {displayNumber}：用户关闭仓门。");
        RaiseStateChanged();
        return OperationResult.Success();
    }

    public void SetLockFeedbackOverride(int slotIndex, bool? rawValue)
    {
        lock (_sync)
        {
            var index = GetSlotArrayIndex(slotIndex);
            _lockFeedbackVersions[index]++;
            _lockFeedbackPending[index] = false;
            _lockOverrides[index] = rawValue;
            RebuildLockFeedbackNoLock(index);
        }

        EmitLog($"仓位 {GetDisplayNumber(slotIndex)}：锁反馈覆盖={(rawValue.HasValue ? Convert.ToInt32(rawValue.Value) : "正常")}。");
        RaiseStateChanged();
    }

    public void SetLightCurtainOverride(int slotIndex, bool? rawValue)
    {
        lock (_sync)
        {
            var index = GetSlotArrayIndex(slotIndex);
            _lightFeedbackVersions[index]++;
            _lightFeedbackPending[index] = false;
            _lightOverrides[index] = rawValue;
            RebuildLightFeedbackNoLock(index);
        }

        EmitLog($"仓位 {GetDisplayNumber(slotIndex)}：光幕覆盖={(rawValue.HasValue ? Convert.ToInt32(rawValue.Value) : "正常")}。");
        RaiseStateChanged();
    }

    public void Reset()
    {
        lock (_sync)
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

            for (var i = 0; i < Settings.Slots.Count; i++)
            {
                SetLockRawNoLock(i, true);
                SetLightRawNoLock(i, false);
            }
        }

        EmitLog("仿真器已执行安全 Reset：全部 DO 断开、仓门关闭、仓位清空、故障覆盖清除。");
        RaiseStateChanged();
    }

    public SimulatorSnapshot GetSnapshot()
    {
        lock (_sync)
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
                _doStates.ToArray(),
                _diStates.ToArray(),
                slots,
                _doorOpen.Count(value => value),
                Settings.Defaults.MaxOpenDoors,
                DateTimeOffset.Now);
        }
    }

    private void ProcessDoBehavior(int channel, bool rawValue)
    {
        var indexedSlot = Settings.Slots.OrderBy(x => x.SlotIndex)
            .Select((slot, index) => (slot, index))
            .FirstOrDefault(x => x.slot.UnlockDoChannel == channel);

        if (indexedSlot.slot is null)
            return;

        var active = Convert.ToInt32(rawValue) == indexedSlot.slot.UnlockActiveLevel;
        if (active)
        {
            var outputMode = Enum.Parse<OutputMode>(Settings.Defaults.OutputMode, true);
            lock (_sync)
            {
                ScheduleLockFeedbackNoLock(indexedSlot.index, false, Settings.Defaults.UnlockFeedbackDelayMs);
                if (Settings.Defaults.AutoPopDoorOnUnlock)
                    _doorOpen[indexedSlot.index] = true;
            }

            var modeText = outputMode == OutputMode.Pulse
                ? $"Pulse {Settings.Defaults.PulseWidthMs}ms 开始"
                : "Level 输出保持";
            EmitLog($"仓位 {indexedSlot.slot.DisplayNumber}：DO{channel}={Convert.ToInt32(rawValue)}（继电器{GetRelayStateText(rawValue)}），开锁输出生效，{modeText}。");
            if (outputMode == OutputMode.Pulse)
                StartPulse(channel, indexedSlot.slot.UnlockActiveLevel != 0);
        }
        else
        {
            EmitLog($"仓位 {indexedSlot.slot.DisplayNumber}：DO{channel}={Convert.ToInt32(rawValue)}（继电器{GetRelayStateText(rawValue)}），开锁输出结束。");
            if (Enum.Parse<LockFeedbackModel>(Settings.Defaults.LockFeedbackModel, true) == LockFeedbackModel.FollowOutput)
            {
                lock (_sync)
                    ScheduleLockFeedbackNoLock(indexedSlot.index, true, Settings.Defaults.LockFeedbackDelayMs);
            }
        }
    }

    private void StartPulse(int channel, bool activeRawValue)
    {
        CancellationToken token;
        lock (_sync)
        {
            _pulseTokens[channel]?.Cancel();
            _pulseTokens[channel]?.Dispose();
            _pulseTokens[channel] = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _pulseTokens[channel]!.Token;
        }

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

    private void ScheduleLockFeedbackNoLock(int slotArrayIndex, bool locked, int delayMs)
    {
        var generation = _generation;
        var version = ++_lockFeedbackVersions[slotArrayIndex];
        if (_lockOverrides[slotArrayIndex].HasValue)
        {
            _lockFeedbackPending[slotArrayIndex] = false;
            return;
        }
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
                lock (_sync)
                {
                    if (generation != _generation ||
                        version != _lockFeedbackVersions[slotArrayIndex] ||
                        _lockOverrides[slotArrayIndex].HasValue)
                        return;
                    SetLockRawNoLock(slotArrayIndex, locked);
                    _lockFeedbackPending[slotArrayIndex] = false;
                    var slot = GetOrderedSlot(slotArrayIndex);
                    displayNumber = slot.DisplayNumber;
                    diChannel = slot.LockFeedbackDiChannel;
                    rawValue = _diStates[diChannel];
                }
                EmitLog($"仓位 {displayNumber}：锁反馈 DI{diChannel}={Convert.ToInt32(rawValue)}，仓门{(locked ? "已锁上" : "已解锁")}。");
                RaiseStateChanged();
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void ScheduleLightFeedbackNoLock(int slotArrayIndex, bool obstructed)
    {
        var generation = _generation;
        var version = ++_lightFeedbackVersions[slotArrayIndex];
        if (_lightOverrides[slotArrayIndex].HasValue)
        {
            _lightFeedbackPending[slotArrayIndex] = false;
            return;
        }
        _lightFeedbackPending[slotArrayIndex] = true;
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Settings.Defaults.LightCurtainFeedbackDelayMs, token);
                lock (_sync)
                {
                    if (generation != _generation ||
                        version != _lightFeedbackVersions[slotArrayIndex] ||
                        _lightOverrides[slotArrayIndex].HasValue)
                        return;
                    SetLightRawNoLock(slotArrayIndex, obstructed);
                    _lightFeedbackPending[slotArrayIndex] = false;
                }
                RaiseStateChanged();
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void RebuildLockFeedbackNoLock(int slotArrayIndex)
    {
        if (_lockOverrides[slotArrayIndex] is { } raw)
        {
            var slot = Settings.Slots.OrderBy(x => x.SlotIndex).ElementAt(slotArrayIndex);
            _diStates[slot.LockFeedbackDiChannel] = raw;
            return;
        }
        SetLockRawNoLock(slotArrayIndex, !_doorOpen[slotArrayIndex]);
    }

    private void RebuildLightFeedbackNoLock(int slotArrayIndex)
    {
        if (_lightOverrides[slotArrayIndex] is { } raw)
        {
            var slot = Settings.Slots.OrderBy(x => x.SlotIndex).ElementAt(slotArrayIndex);
            _diStates[slot.LightCurtainDiChannel] = raw;
            return;
        }
        SetLightRawNoLock(slotArrayIndex, _cargoPresent[slotArrayIndex]);
    }

    private void SetLockRawNoLock(int slotArrayIndex, bool locked)
    {
        var slot = Settings.Slots.OrderBy(x => x.SlotIndex).ElementAt(slotArrayIndex);
        _diStates[slot.LockFeedbackDiChannel] = locked == (slot.LockedActiveLevel != 0);
    }

    private void SetLightRawNoLock(int slotArrayIndex, bool obstructed)
    {
        var slot = Settings.Slots.OrderBy(x => x.SlotIndex).ElementAt(slotArrayIndex);
        _diStates[slot.LightCurtainDiChannel] = obstructed == (slot.ObstructedActiveLevel != 0);
    }

    private static string GetRelayStateText(bool rawValue) => rawValue ? "闭合" : "断开";

    private void SetDoNoLock(int channel, bool value)
    {
        _pulseTokens[channel]?.Cancel();
        _pulseTokens[channel]?.Dispose();
        _pulseTokens[channel] = null;
        _doStates[channel] = value;
    }

    private int GetSlotArrayIndex(int slotIndex)
    {
        var ordered = Settings.Slots.OrderBy(x => x.SlotIndex).ToList();
        var index = ordered.FindIndex(x => x.SlotIndex == slotIndex);
        return index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(slotIndex));
    }

    private SlotSettings GetOrderedSlot(int arrayIndex) =>
        Settings.Slots.OrderBy(x => x.SlotIndex).ElementAt(arrayIndex);

    private int GetDisplayNumber(int slotIndex)
    {
        lock (_sync)
            return GetOrderedSlot(GetSlotArrayIndex(slotIndex)).DisplayNumber;
    }

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
}

public readonly record struct OperationResult(bool IsSuccess, string? Error)
{
    public static OperationResult Success() => new(true, null);
    public static OperationResult Fail(string error) => new(false, error);
}
