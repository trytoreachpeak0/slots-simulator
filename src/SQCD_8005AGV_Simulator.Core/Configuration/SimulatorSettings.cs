using System.Text.Json;
using System.Text.Json.Serialization;

namespace SQCD_8005AGV_Simulator.Core.Configuration;

public sealed class SimulatorSettings
{
    public string SchemaVersion { get; set; } = "1.0";
    public string InstanceId { get; set; } = "agv-slot-simulator-01";
    public string AgvId { get; set; } = "AGV-01";
    public ModbusSettings Modbus { get; set; } = new();
    public SimulatorDefaults Defaults { get; set; } = new();
    public List<int> PowerOnDoStates { get; set; } = Enumerable.Repeat(0, 16).ToList();
    public List<SlotSettings> Slots { get; set; } = [];

    public static SimulatorSettings Load(string path)
    {
        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<SimulatorSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("配置文件内容为空。");
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        var errors = new List<string>();

        if (Modbus.Port is < 1 or > 65535)
            errors.Add("modbus.port 必须在 1～65535 之间。");
        if (Modbus.UnitId is < 0 or > 255)
            errors.Add("modbus.unitId 必须在 0～255 之间。");
        if (Modbus.DoChannelCount != 16 || Modbus.DiChannelCount != 16)
            errors.Add("当前型号必须配置为 16 路 DO 和 16 路 DI。");
        if (Defaults.PulseWidthMs is < 50 or > 65535)
            errors.Add("defaults.pulseWidthMs 必须在厂家范围 50～65535ms 内。");
        if (Defaults.MaxOpenDoors is < 1 or > 8)
            errors.Add("defaults.maxOpenDoors 必须在 1～8 之间。");
        if (Slots.Count != 8)
            errors.Add("slots 必须包含 8 个仓位。");
        if (PowerOnDoStates.Count != 16 || PowerOnDoStates.Any(value => value is not (0 or 1)))
            errors.Add("powerOnDoStates 必须包含 16 个 0/1 值。");

        ValidateUniqueRange(Slots.Select(x => x.SlotIndex), 0, 7, "slotIndex", errors);
        ValidateUniqueRange(Slots.Select(x => x.DisplayNumber), 1, 8, "displayNumber", errors);
        ValidateUniqueRange(Slots.Select(x => x.UnlockDoChannel), 0, 15, "unlockDoChannel", errors);
        ValidateUniqueRange(Slots.Select(x => x.LockFeedbackDiChannel), 0, 15, "lockFeedbackDiChannel", errors);
        ValidateUniqueRange(Slots.Select(x => x.LightCurtainDiChannel), 0, 15, "lightCurtainDiChannel", errors);

        var allMappedDiChannels = Slots.Select(x => x.LockFeedbackDiChannel)
            .Concat(Slots.Select(x => x.LightCurtainDiChannel))
            .ToList();
        if (allMappedDiChannels.Distinct().Count() != allMappedDiChannels.Count)
            errors.Add("锁反馈和光幕的 DI 通道不允许交叉重复映射。");

        ValidateDelay(Defaults.UnlockFeedbackDelayMs, "defaults.unlockFeedbackDelayMs", errors);
        ValidateDelay(Defaults.LockFeedbackDelayMs, "defaults.lockFeedbackDelayMs", errors);
        ValidateDelay(Defaults.LightCurtainFeedbackDelayMs, "defaults.lightCurtainFeedbackDelayMs", errors);

        foreach (var slot in Slots)
        {
            if (slot.UnlockActiveLevel is not (0 or 1) ||
                slot.LockedActiveLevel is not (0 or 1) ||
                slot.ObstructedActiveLevel is not (0 or 1))
            {
                errors.Add($"slots[{slot.SlotIndex}] 的 ActiveLevel 只能为 0 或 1。");
            }
        }

        if (!Enum.TryParse<OutputMode>(Defaults.OutputMode, true, out _))
            errors.Add("defaults.outputMode 只能是 Level 或 Pulse。");
        if (!Enum.TryParse<LockFeedbackModel>(Defaults.LockFeedbackModel, true, out _))
            errors.Add("defaults.lockFeedbackModel 只能是 DoorLatch 或 FollowOutput。");

        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
    }

    private static void ValidateUniqueRange(IEnumerable<int> values, int min, int max, string name, List<string> errors)
    {
        var list = values.ToList();
        if (list.Any(value => value < min || value > max))
            errors.Add($"{name} 必须在 {min}～{max} 之间。");
        if (list.Distinct().Count() != list.Count)
            errors.Add($"{name} 不允许重复。");
    }

    private static void ValidateDelay(int value, string name, List<string> errors)
    {
        if (value is < 0 or > 60000)
            errors.Add($"{name} 必须在 0～60000ms 之间。");
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class ModbusSettings
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1502;
    public int UnitId { get; set; } = 255;
    public bool StrictUnitId { get; set; } = true;
    public int DoPduBaseAddress { get; set; } = 100;
    public int DiPduBaseAddress { get; set; } = 200;
    public int DoChannelCount { get; set; } = 16;
    public int DiChannelCount { get; set; } = 16;
}

public sealed class SimulatorDefaults
{
    public string OutputMode { get; set; } = nameof(Configuration.OutputMode.Pulse);
    public int PulseWidthMs { get; set; } = 500;
    public int UnlockFeedbackDelayMs { get; set; } = 100;
    public int LockFeedbackDelayMs { get; set; } = 100;
    public int LightCurtainFeedbackDelayMs { get; set; } = 50;
    public string LockFeedbackModel { get; set; } = nameof(Configuration.LockFeedbackModel.DoorLatch);
    public bool AutoPopDoorOnUnlock { get; set; } = true;
    public int MaxOpenDoors { get; set; } = 1;
}

public sealed class SlotSettings
{
    public int SlotIndex { get; set; }
    public int DisplayNumber { get; set; }
    public int UnlockDoChannel { get; set; }
    public int LockFeedbackDiChannel { get; set; }
    public int LightCurtainDiChannel { get; set; }
    public int UnlockActiveLevel { get; set; } = 1;
    public int LockedActiveLevel { get; set; } = 1;
    public int ObstructedActiveLevel { get; set; } = 1;
}

public enum OutputMode
{
    Level,
    Pulse
}

public enum LockFeedbackModel
{
    DoorLatch,
    FollowOutput
}
