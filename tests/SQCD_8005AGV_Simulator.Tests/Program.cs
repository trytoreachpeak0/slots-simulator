using SQCD_8005AGV_Simulator.Core.Configuration;
using SQCD_8005AGV_Simulator.Core.Services;
using System.Net;
using System.Net.Sockets;

var tests = new (string Name, Func<Task> Run)[]
{
    ("默认安全状态", TestDefaultStateAsync),
    ("Pulse 自动断开", TestPulseAsync),
    ("装料关门反馈闭环", TestCargoAndDoorAsync),
    ("0x05 单线圈写入", TestWriteSingleCoilAsync),
    ("0x02 DI读取与LSB位序", TestReadDiPackingAsync),
    ("0x0F 批量写与数量回显", TestWriteMultipleCoilsAsync),
    ("非法写入值异常响应", TestIllegalCoilValueAsync),
    ("真实TCP与MBAP组帧", TestTcpRoundTripAsync),
    ("快速关门不受旧反馈任务覆盖", TestLatestLockFeedbackWinsAsync),
    ("Pulse重触发重新计时", TestPulseRetriggerAsync),
    ("光幕固定1保留真实货物状态", TestLightFaultKeepsCargoTruthAsync),
    ("Reset取消延迟反馈", TestResetCancelsDelayedFeedbackAsync),
    ("配置拒绝DI交叉重复", TestRejectsCrossDiMappingAsync),
    ("配置拒绝非法反馈延迟", TestRejectsInvalidDelayAsync),
    ("端口启动失败后可以重试", TestServerCanRetryAfterStartFailureAsync),
    ("多门打开计数超过配置", TestOpenDoorLimitSnapshotAsync),
    ("周期读取日志默认关闭且可开启", TestReadRequestLoggingAsync),
    ("FC05日志按命令和动作顺序记录", TestWriteLogSequenceAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL  {test.Name} - {ex.Message}");
    }
}

Console.WriteLine($"\n结果：{tests.Length - failures.Count}/{tests.Length} 通过");
if (failures.Count > 0)
{
    Console.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}
return 0;

static Task TestDefaultStateAsync()
{
    using var engine = new SimulatorEngine(CreateSettings("Level"));
    var snapshot = engine.GetSnapshot();
    Assert(snapshot.DoStates.All(value => !value), "Reset 后 DO 应全部为 0。");
    Assert(snapshot.DiStates.Take(8).All(value => value), "默认锁反馈 DI0～DI7 应为 1。");
    Assert(snapshot.DiStates.Skip(8).All(value => value), "空仓时光幕 DI8～DI15 应为 1（模块灯亮）。");
    Assert(snapshot.Slots.All(slot => !slot.DoorOpen && !slot.CargoPresent), "仓门应关闭且仓位应为空。");
    return Task.CompletedTask;
}

static Task TestReadRequestLoggingAsync()
{
    using var engine = new SimulatorEngine(CreateSettings("Level"));
    var server = new ModbusTcpServer(engine);
    var logs = new List<string>();
    server.LogEmitted += (_, message) => logs.Add(message);

    server.ProcessPdu([0x01, 0x00, 0x64, 0x00, 0x08]);
    server.ProcessPdu([0x02, 0x00, 0xC8, 0x00, 0x10]);
    Assert(logs.Count == 0, "默认不应逐条记录 FC01/FC02 周期读取。");

    server.LogReadRequests = true;
    server.ProcessPdu([0x01, 0x00, 0x64, 0x00, 0x08]);
    Assert(logs.Count == 1 && logs[0].Contains("FC=0x01"), "开启后应记录 FC01 读取请求。");
    return Task.CompletedTask;
}

static Task TestWriteLogSequenceAsync()
{
    using var engine = new SimulatorEngine(CreateSettings("Level"));
    var server = new ModbusTcpServer(engine);
    var logs = new List<string>();
    server.LogEmitted += (_, message) => logs.Add(message);
    engine.LogEmitted += (_, message) => logs.Add(message);

    server.ProcessPdu([0x05, 0x00, 0x64, 0xFF, 0x00]);

    Assert(logs.Count >= 2, "FC05 写入应记录命令和 DO 动作。");
    Assert(logs[0].Contains("收到 FC05") && logs[0].Contains("0xFF00"), "第一条应记录收到的 FC05 命令和原始协议值。");
    Assert(logs[1].Contains("DO0=1") && logs[1].Contains("继电器闭合"), "第二条应记录 DO0 动作及继电器状态。");
    return Task.CompletedTask;
}

static async Task TestPulseAsync()
{
    var settings = CreateSettings("Pulse");
    settings.Defaults.PulseWidthMs = 60;
    using var engine = new SimulatorEngine(settings);
    engine.WriteDo(0, true);
    Assert(engine.ReadDo(0), "写 ON 后 DO0 应立即为 1。");
    await Task.Delay(130);
    Assert(!engine.ReadDo(0), "Pulse 到期后 DO0 应自动恢复为 0。");
    Assert(engine.GetSnapshot().Slots[0].DoorOpen, "开锁后仓门应模拟弹开。");
}

static async Task TestCargoAndDoorAsync()
{
    var settings = CreateSettings("Pulse");
    settings.Defaults.PulseWidthMs = 60;
    using var engine = new SimulatorEngine(settings);
    engine.WriteDo(0, true);
    await Task.Delay(20);
    Assert(!engine.GetSnapshot().Slots[0].IsLocked, "开锁反馈应变为未锁。");
    Assert(engine.PlaceCargo(0).IsSuccess, "开门后应允许放料。");
    await Task.Delay(20);
    Assert(engine.GetSnapshot().Slots[0].IsObstructed, "放料后光幕应为遮挡。");
    Assert(engine.CloseDoor(0).IsSuccess, "应允许用户关闭仓门。");
    await Task.Delay(20);
    var slot = engine.GetSnapshot().Slots[0];
    Assert(slot.IsLocked && slot.IsObstructed && !slot.DoorOpen && slot.CargoPresent, "关门后应得到已锁、有货、门关状态。");
}

static Task TestWriteSingleCoilAsync()
{
    using var engine = new SimulatorEngine(CreateSettings("Level"));
    var server = new ModbusTcpServer(engine);
    var response = server.ProcessPdu([0x05, 0x00, 0x64, 0xFF, 0x00]);
    Assert(response.SequenceEqual(new byte[] { 0x05, 0x00, 0x64, 0xFF, 0x00 }), "0x05 应原样回显地址和值。");
    Assert(engine.ReadDo(0), "PDU 100 应映射 DO0 并闭合继电器。");
    return Task.CompletedTask;
}

static Task TestReadDiPackingAsync()
{
    using var engine = new SimulatorEngine(CreateSettings("Level"));
    var server = new ModbusTcpServer(engine);
    var response = server.ProcessPdu([0x02, 0x00, 0xC8, 0x00, 0x10]);
    Assert(response.SequenceEqual(new byte[] { 0x02, 0x02, 0xFF, 0xFF }), "初始锁反馈和空仓光幕均为1，应返回 0xFF 0xFF。");
    return Task.CompletedTask;
}

static Task TestWriteMultipleCoilsAsync()
{
    using var engine = new SimulatorEngine(CreateSettings("Level"));
    var server = new ModbusTcpServer(engine);
    var response = server.ProcessPdu([0x0F, 0x00, 0x64, 0x00, 0x10, 0x02, 0x03, 0x01]);
    Assert(response.SequenceEqual(new byte[] { 0x0F, 0x00, 0x64, 0x00, 0x10 }), "批量写 16 路应按标准回显数量 0x0010。");
    var states = engine.ReadDoRange(0, 16);
    Assert(states[0] && states[1] && states[8], "LSB-first 数据应使 DO0、DO1、DO8 为 1。");
    Assert(states.Where((_, index) => index is not (0 or 1 or 8)).All(value => !value), "其他 DO 应保持 0。");
    return Task.CompletedTask;
}

static Task TestIllegalCoilValueAsync()
{
    using var engine = new SimulatorEngine(CreateSettings("Level"));
    var server = new ModbusTcpServer(engine);
    var response = server.ProcessPdu([0x05, 0x00, 0x64, 0x12, 0x34]);
    Assert(response.SequenceEqual(new byte[] { 0x85, 0x03 }), "非法线圈值应返回 Illegal Data Value。");
    Assert(!engine.ReadDo(0), "非法写入不得改变 DO。");
    return Task.CompletedTask;
}

static async Task TestTcpRoundTripAsync()
{
    var settings = CreateSettings("Level");
    settings.Modbus.Port = GetFreeTcpPort();
    using var engine = new SimulatorEngine(settings);
    await using var server = new ModbusTcpServer(engine);
    await server.StartAsync();

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, settings.Modbus.Port);
    using var stream = client.GetStream();
    var request = new byte[]
    {
        0x12, 0x34,
        0x00, 0x00,
        0x00, 0x06,
        0xFF,
        0x05, 0x00, 0x64, 0xFF, 0x00
    };
    await stream.WriteAsync(request);
    var response = new byte[12];
    await ReadExactAsync(stream, response);
    Assert(response.SequenceEqual(request), "0x05 TCP 响应应正确回显 Transaction ID、MBAP 和 PDU。");
    Assert(engine.ReadDo(0), "TCP 请求应实际更新 DO0。");
}

static async Task TestLatestLockFeedbackWinsAsync()
{
    var settings = CreateSettings("Level");
    settings.Defaults.UnlockFeedbackDelayMs = 60;
    settings.Defaults.LockFeedbackDelayMs = 5;
    using var engine = new SimulatorEngine(settings);
    engine.WriteDo(0, true);
    Assert(engine.CloseDoor(0).IsSuccess, "开锁后应能立即模拟关门。");
    await Task.Delay(100);
    var slot = engine.GetSnapshot().Slots[0];
    Assert(slot.IsLocked && !slot.DoorOpen, "较早的开锁反馈任务不得在关门后把锁反馈重新改为未锁。");
    Assert(!slot.LockFeedbackPending, "最终不应残留锁反馈同步任务。");
}

static async Task TestPulseRetriggerAsync()
{
    var settings = CreateSettings("Pulse");
    settings.Defaults.PulseWidthMs = 80;
    using var engine = new SimulatorEngine(settings);
    engine.WriteDo(0, true);
    await Task.Delay(50);
    engine.WriteDo(0, true);
    await Task.Delay(50);
    Assert(engine.ReadDo(0), "第二次ON后50ms仍应保持激活，不能沿用第一次计时。");
    await Task.Delay(50);
    Assert(!engine.ReadDo(0), "第二次ON后的完整脉宽到期后应恢复非激活值。");
}

static async Task TestLightFaultKeepsCargoTruthAsync()
{
    using var engine = new SimulatorEngine(CreateSettings("Level"));
    engine.WriteDo(0, true);
    await Task.Delay(10);
    engine.SetLightCurtainOverride(0, true);
    Assert(engine.PlaceCargo(0).IsSuccess, "光幕故障不应阻止模拟实际放料。");
    await Task.Delay(20);
    var slot = engine.GetSnapshot().Slots[0];
    Assert(slot.CargoPresent, "内部货物事实应为有货。");
    Assert(!slot.IsObstructed && slot.LightCurtainOverride == true, "有货时光幕DI固定1应形成可测试的不一致状态。");
}

static async Task TestResetCancelsDelayedFeedbackAsync()
{
    var settings = CreateSettings("Level");
    settings.Defaults.UnlockFeedbackDelayMs = 80;
    using var engine = new SimulatorEngine(settings);
    engine.WriteDo(0, true);
    engine.Reset();
    await Task.Delay(120);
    var slot = engine.GetSnapshot().Slots[0];
    Assert(slot.IsLocked && !slot.DoorOpen && !slot.LockFeedbackPending, "Reset后旧延迟任务不得污染安全初始状态。");
}

static Task TestRejectsCrossDiMappingAsync()
{
    var settings = CreateSettings("Level");
    settings.Slots[0].LightCurtainDiChannel = settings.Slots[0].LockFeedbackDiChannel;
    AssertThrows<InvalidDataException>(settings.Validate, "锁反馈和光幕映射到同一DI时应拒绝配置。");
    return Task.CompletedTask;
}

static Task TestRejectsInvalidDelayAsync()
{
    var settings = CreateSettings("Level");
    settings.Defaults.UnlockFeedbackDelayMs = -1;
    AssertThrows<InvalidDataException>(settings.Validate, "负数反馈延迟应在启动前被拒绝。");
    return Task.CompletedTask;
}

static async Task TestServerCanRetryAfterStartFailureAsync()
{
    var settings = CreateSettings("Level");
    settings.Modbus.Port = GetFreeTcpPort();
    var occupied = new TcpListener(IPAddress.Loopback, settings.Modbus.Port);
    occupied.Start();
    using var engine = new SimulatorEngine(settings);
    await using var server = new ModbusTcpServer(engine);
    try
    {
        await AssertThrowsAsync<SocketException>(() => server.StartAsync(), "端口被占用时应报告启动失败。");
        Assert(!server.IsRunning, "启动失败后服务状态必须恢复为未运行。");
    }
    finally
    {
        occupied.Stop();
    }

    await server.StartAsync();
    Assert(server.IsRunning, "端口释放后同一个服务对象应能再次启动。");
}

static Task TestOpenDoorLimitSnapshotAsync()
{
    var settings = CreateSettings("Level");
    settings.Defaults.MaxOpenDoors = 1;
    using var engine = new SimulatorEngine(settings);
    engine.WriteDo(0, true);
    engine.WriteDo(1, true);
    var snapshot = engine.GetSnapshot();
    Assert(snapshot.OpenDoorCount == 2 && snapshot.MaxOpenDoors == 1, "快照应保留多门同时打开事实供界面告警。");
    return Task.CompletedTask;
}

static int GetFreeTcpPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
{
    var offset = 0;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset), timeout.Token);
        if (read == 0)
            throw new IOException("连接在收到完整响应前关闭。");
        offset += read;
    }
}

static SimulatorSettings CreateSettings(string outputMode)
{
    return new SimulatorSettings
    {
        Defaults = new SimulatorDefaults
        {
            OutputMode = outputMode,
            PulseWidthMs = 500,
            UnlockFeedbackDelayMs = 5,
            LockFeedbackDelayMs = 5,
            LightCurtainFeedbackDelayMs = 5,
            LockFeedbackModel = "DoorLatch",
            AutoPopDoorOnUnlock = true,
            MaxOpenDoors = 1
        },
        Slots = Enumerable.Range(0, 8).Select(index => new SlotSettings
        {
            SlotIndex = index,
            DisplayNumber = index + 1,
            UnlockDoChannel = index,
            LockFeedbackDiChannel = index,
            LightCurtainDiChannel = index + 8,
            UnlockActiveLevel = 1,
            LockedActiveLevel = 1,
            ObstructedActiveLevel = 0
        }).ToList()
    };
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}
