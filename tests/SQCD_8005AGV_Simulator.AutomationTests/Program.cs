using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using SQCD_8005AGV_Simulator.AutomationHost;
using SQCD_8005AGV_Simulator.Core.Configuration;
using SQCD_8005AGV_Simulator.Core.Services;

await using var environment = await TestEnvironment.StartAsync();

var tests = new (string Name, Func<TestEnvironment, Task> Run)[]
{
    ("HTTP健康、快照与安全Reset", TestHealthSnapshotAndResetAsync),
    ("FC05开锁可被HTTP快照观察", TestModbusUnlockSnapshotAsync),
    ("HTTP放货关门与Modbus DI闭环", TestCargoDoorAndDiAsync),
    ("expectedRevision冲突不改变状态", TestRevisionConflictAsync),
    ("传感器覆盖不改变物理事实", TestSensorOverrideKeepsTruthAsync),
    ("NO_RESPONSE可触发并恢复", TestNoResponseAsync),
    ("DISCONNECT持续拒绝并恢复", TestDisconnectAsync),
    ("DELAY确定性延迟并恢复", TestDelayAsync),
    ("Reset取消旧反馈任务", TestResetCancelsOldTasksAsync),
    ("并发请求不丢更新", TestConcurrentRevisionAsync),
    ("HTTP不提供开锁接口", TestNoHttpUnlockAsync),
    ("commandId幂等与内容冲突", TestCommandIdempotencyAsync),
    ("无变化命令不递增revision", TestNoChangeCommandAsync),
    ("非loopback HTTP配置被拒绝", TestRejectNonLoopbackAsync)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run(environment);
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

static async Task TestHealthSnapshotAndResetAsync(TestEnvironment environment)
{
    var health = await environment.Client.GetFromJsonAsync<HealthResponse>("health") ?? throw new InvalidOperationException("health响应为空。");
    Assert(health.Status == "READY" && health.Modbus.IsRunning, "Host和Modbus应处于READY状态。");

    var before = await environment.GetSnapshotAsync();
    Assert(before.Revision == 1 && before.Slots.Count == 8, "进程初始轮次应为revision=1且包含8仓。 ");
    Assert(before.Slots.All(slot => slot.DoorState == "CLOSED" && slot.CargoState == "EMPTY"), "初始状态应全部门关、空仓。");

    var request = new ResetRequest(before.RunId, "reset-health-001", before.Revision);
    var first = await environment.PostAsync<ResetRequest, WriteResponse>("reset", request);
    Assert(first.Revision == 1 && first.RunId != before.RunId && !first.Replayed, "首次Reset应创建新runId并回到revision=1。");

    var replay = await environment.PostAsync<ResetRequest, WriteResponse>("reset", request);
    Assert(replay.RunId == first.RunId && replay.Replayed && replay.AppliedRevision == 1, "Reset重放必须返回同一个新runId。");
}

static async Task TestModbusUnlockSnapshotAsync(TestEnvironment environment)
{
    await environment.ResetAsync("reset-unlock");
    await environment.SendModbusAsync(TestEnvironment.WriteDo0On, 12);
    var snapshot = await environment.WaitForSnapshotAsync(value => value.Slots[0].DoorState == "OPEN");
    Assert(snapshot.Slots[0].DoorState == "OPEN", "FC05后1号仓门应打开。");
    Assert(snapshot.Slots[0].UnlockOutputRaw == 1, "Pulse有效期内DO0应为1。");
}

static async Task TestCargoDoorAndDiAsync(TestEnvironment environment)
{
    await environment.ResetAsync("reset-cargo-door");
    await environment.SendModbusAsync(TestEnvironment.WriteDo0On, 12);
    var opened = await environment.WaitForSnapshotAsync(value => value.Slots[0].DoorState == "OPEN");

    var cargo = await environment.PutAsync<CargoRequest, WriteResponse>(
        "slots/1/cargo",
        new CargoRequest(opened.RunId, "cargo-occupied-001", opened.Revision, "OCCUPIED"));
    Assert(cargo.Changed, "放货应改变状态。");

    var cargoSettled = await environment.WaitForSnapshotAsync(value =>
        value.Slots[0].CargoState == "OCCUPIED" && !value.Slots[0].LightCurtainFeedbackPending);
    await environment.PostAsync<ResetRequest, WriteResponse>(
        "slots/1/close-door",
        new ResetRequest(cargoSettled.RunId, "close-door-001", cargoSettled.Revision));

    await environment.WaitForSnapshotAsync(value =>
        value.Slots[0].DoorState == "CLOSED" && !value.Slots[0].LockFeedbackPending);
    var response = await environment.SendModbusAsync(TestEnvironment.ReadAllDi, 11);
    Assert(response[9] == 0xFF, "DI0～DI7应全部为已锁原始值1。");
    Assert(response[10] == 0xFE, "有货的1号仓光幕DI8应为0，其余光幕应为1。");
}

static async Task TestRevisionConflictAsync(TestEnvironment environment)
{
    var snapshot = await environment.ResetAsync("reset-revision-conflict");
    using var response = await environment.Client.PutAsJsonAsync(
        "slots/1/lock-feedback-override",
        new OverrideRequest(snapshot.RunId, "revision-conflict-001", snapshot.Revision + 1, "FIXED_0"));
    Assert(response.StatusCode == HttpStatusCode.Conflict, "错误revision应返回409。");
    var error = await response.Content.ReadFromJsonAsync<ErrorResponse>() ?? throw new InvalidOperationException("错误响应为空。");
    Assert(error.ReasonCode == "REVISION_CONFLICT", "应返回稳定REVISION_CONFLICT。");
    var after = await environment.GetSnapshotAsync();
    Assert(after.Revision == snapshot.Revision && after.Slots[0].LockFeedbackRaw == 1, "冲突请求不得改变状态。");
}

static async Task TestSensorOverrideKeepsTruthAsync(TestEnvironment environment)
{
    await environment.ResetAsync("reset-sensor-override");
    await environment.SendModbusAsync(TestEnvironment.WriteDo0On, 12);
    var opened = await environment.WaitForSnapshotAsync(value => value.Slots[0].DoorState == "OPEN");
    await environment.PutAsync<CargoRequest, WriteResponse>(
        "slots/1/cargo",
        new CargoRequest(opened.RunId, "cargo-before-override", opened.Revision, "OCCUPIED"));
    var settled = await environment.WaitForSnapshotAsync(value => !value.Slots[0].LightCurtainFeedbackPending);
    var overridden = await environment.PutAsync<OverrideRequest, WriteResponse>(
        "slots/1/light-curtain-override",
        new OverrideRequest(settled.RunId, "light-fixed-one", settled.Revision, "FIXED_1"));
    var slot = overridden.Snapshot.Slots[0];
    Assert(slot.CargoState == "OCCUPIED" && slot.LightCurtainRaw == 1, "覆盖只应改变DI，货物事实必须保持有货。");
}

static async Task TestNoResponseAsync(TestEnvironment environment)
{
    var snapshot = await environment.ResetAsync("reset-no-response");
    await environment.PutAsync<ModbusFaultRequest, WriteResponse>(
        "faults/modbus",
        new ModbusFaultRequest(snapshot.RunId, "fault-no-response", snapshot.Revision, "NO_RESPONSE", null));

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, environment.ModbusPort);
    using var stream = client.GetStream();
    await stream.WriteAsync(TestEnvironment.ReadAllDi);
    using var timeout = new CancellationTokenSource(200);
    await AssertThrowsAsync<OperationCanceledException>(
        async () => _ = await stream.ReadAsync(new byte[11], timeout.Token),
        "NO_RESPONSE下读取应超时。");

    var faulted = await environment.GetSnapshotAsync();
    await environment.PutAsync<ModbusFaultRequest, WriteResponse>(
        "faults/modbus",
        new ModbusFaultRequest(faulted.RunId, "fault-normal-after-no-response", faulted.Revision, "NORMAL", null));
    var response = await environment.SendModbusAsync(TestEnvironment.ReadAllDi, 11);
    Assert(response[7] == 0x02, "恢复NORMAL后Modbus应重新响应。");
}

static async Task TestDisconnectAsync(TestEnvironment environment)
{
    var snapshot = await environment.ResetAsync("reset-disconnect");
    await environment.PutAsync<ModbusFaultRequest, WriteResponse>(
        "faults/modbus",
        new ModbusFaultRequest(snapshot.RunId, "fault-disconnect", snapshot.Revision, "DISCONNECT", null));

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, environment.ModbusPort);
    using var stream = client.GetStream();
    var disconnected = false;
    try
    {
        await stream.WriteAsync(TestEnvironment.ReadAllDi);
        using var timeout = new CancellationTokenSource(1000);
        disconnected = await stream.ReadAsync(new byte[11], timeout.Token) == 0;
    }
    catch (IOException)
    {
        disconnected = true;
    }
    catch (SocketException)
    {
        disconnected = true;
    }
    Assert(disconnected, "DISCONNECT期间新连接应立即关闭或被TCP重置。");

    var faulted = await environment.GetSnapshotAsync();
    await environment.PutAsync<ModbusFaultRequest, WriteResponse>(
        "faults/modbus",
        new ModbusFaultRequest(faulted.RunId, "fault-normal-after-disconnect", faulted.Revision, "NORMAL", null));
    var response = await environment.SendModbusAsync(TestEnvironment.ReadAllDi, 11);
    Assert(response[7] == 0x02, "恢复NORMAL后应允许连接和读取。");
}

static async Task TestDelayAsync(TestEnvironment environment)
{
    var snapshot = await environment.ResetAsync("reset-delay");
    await environment.PutAsync<ModbusFaultRequest, WriteResponse>(
        "faults/modbus",
        new ModbusFaultRequest(snapshot.RunId, "fault-delay", snapshot.Revision, "DELAY", 180));

    var stopwatch = Stopwatch.StartNew();
    await environment.SendModbusAsync(TestEnvironment.ReadAllDi, 11);
    stopwatch.Stop();
    Assert(stopwatch.ElapsedMilliseconds >= 150, "DELAY应施加确定性延迟。");

    var delayed = await environment.GetSnapshotAsync();
    await environment.PutAsync<ModbusFaultRequest, WriteResponse>(
        "faults/modbus",
        new ModbusFaultRequest(delayed.RunId, "fault-normal-after-delay", delayed.Revision, "NORMAL", null));

    var normal = await environment.GetSnapshotAsync();
    await environment.PutAsync<ModbusFaultRequest, WriteResponse>(
        "faults/modbus",
        new ModbusFaultRequest(normal.RunId, "fault-delay-cancel", normal.Revision, "DELAY", 1000));
    var cancelStopwatch = Stopwatch.StartNew();
    var pendingRead = environment.SendModbusAsync(TestEnvironment.ReadAllDi, 11);
    await Task.Delay(100);
    var longDelay = await environment.GetSnapshotAsync();
    await environment.PutAsync<ModbusFaultRequest, WriteResponse>(
        "faults/modbus",
        new ModbusFaultRequest(longDelay.RunId, "fault-normal-cancel-delay", longDelay.Revision, "NORMAL", null));
    await pendingRead;
    cancelStopwatch.Stop();
    Assert(cancelStopwatch.ElapsedMilliseconds < 800, "切换故障模式必须取消旧的1000ms延迟。");
}

static async Task TestResetCancelsOldTasksAsync(TestEnvironment environment)
{
    await environment.ResetAsync("reset-before-old-task");
    await environment.SendModbusAsync(TestEnvironment.WriteDo0On, 12);
    var opened = await environment.WaitForSnapshotAsync(value => value.Slots[0].DoorState == "OPEN");
    var reset = await environment.PostAsync<ResetRequest, WriteResponse>(
        "reset",
        new ResetRequest(opened.RunId, "reset-cancel-old-task", opened.Revision));
    await Task.Delay(350);
    var after = await environment.GetSnapshotAsync();
    Assert(after.RunId == reset.RunId && after.Slots[0].DoorState == "CLOSED" && after.Slots[0].LockFeedbackRaw == 1, "旧反馈任务不得污染新runId。");
}

static async Task TestConcurrentRevisionAsync(TestEnvironment environment)
{
    var snapshot = await environment.ResetAsync("reset-concurrent");
    var first = environment.Client.PutAsJsonAsync(
        "slots/1/lock-feedback-override",
        new OverrideRequest(snapshot.RunId, "concurrent-1", snapshot.Revision, "FIXED_0"));
    var second = environment.Client.PutAsJsonAsync(
        "slots/2/lock-feedback-override",
        new OverrideRequest(snapshot.RunId, "concurrent-2", snapshot.Revision, "FIXED_0"));
    var responses = await Task.WhenAll(first, second);
    Assert(responses.Count(response => response.StatusCode == HttpStatusCode.OK) == 1, "并发同revision请求只能有一个成功。");
    Assert(responses.Count(response => response.StatusCode == HttpStatusCode.Conflict) == 1, "另一个并发请求必须返回409。");
    foreach (var response in responses)
        response.Dispose();
    var after = await environment.GetSnapshotAsync();
    Assert(after.Revision == snapshot.Revision + 1, "并发冲突不得造成revision跳跃或丢更新。");
}

static async Task TestNoHttpUnlockAsync(TestEnvironment environment)
{
    var openApi = await environment.OpenApiClient.GetStringAsync("openapi/v1.json");
    Assert(!openApi.Contains("open-door", StringComparison.OrdinalIgnoreCase) &&
           !openApi.Contains("/unlock", StringComparison.OrdinalIgnoreCase), "OpenAPI不得包含开锁接口。");

    using var unknown = await environment.Client.PostAsJsonAsync("slots/1/open-door", new { });
    Assert(unknown.StatusCode == HttpStatusCode.NotFound, "未知开门路径应返回404。");
    var notFoundError = await unknown.Content.ReadFromJsonAsync<ErrorResponse>() ?? throw new InvalidOperationException("404响应为空。");
    Assert(notFoundError.ReasonCode == "PATH_NOT_FOUND", "404必须返回稳定PATH_NOT_FOUND。");
    using var wrongMethod = await environment.Client.PutAsJsonAsync("slots/1/close-door", new { });
    Assert(wrongMethod.StatusCode == HttpStatusCode.MethodNotAllowed, "已知路径错误方法应返回405。");
    var methodError = await wrongMethod.Content.ReadFromJsonAsync<ErrorResponse>() ?? throw new InvalidOperationException("405响应为空。");
    Assert(methodError.ReasonCode == "METHOD_NOT_ALLOWED", "405必须返回稳定METHOD_NOT_ALLOWED。");

    var snapshot = await environment.GetSnapshotAsync();
    using var invalidEnum = await environment.Client.PutAsJsonAsync(
        "faults/modbus",
        new ModbusFaultRequest(snapshot.RunId, "invalid-enum", snapshot.Revision, "999", null));
    var enumError = await invalidEnum.Content.ReadFromJsonAsync<ErrorResponse>() ?? throw new InvalidOperationException("非法枚举响应为空。");
    Assert(invalidEnum.StatusCode == HttpStatusCode.BadRequest && enumError.ReasonCode == "INVALID_MODBUS_FAULT_MODE", "未定义数字枚举必须被拒绝。");

    using var malformed = await environment.Client.PostAsync(
        "reset",
        new StringContent("{", Encoding.UTF8, "application/json"));
    var malformedError = await malformed.Content.ReadFromJsonAsync<ErrorResponse>() ?? throw new InvalidOperationException("畸形JSON响应为空。");
    Assert(
        malformed.StatusCode == HttpStatusCode.BadRequest && malformedError.ReasonCode == "INVALID_HTTP_REQUEST",
        $"畸形JSON必须返回稳定错误结构，实际状态={(int)malformed.StatusCode}，reasonCode={malformedError.ReasonCode}。");
}

static async Task TestCommandIdempotencyAsync(TestEnvironment environment)
{
    var snapshot = await environment.ResetAsync("reset-idempotency");
    var request = new OverrideRequest(snapshot.RunId, "same-command-id", snapshot.Revision, "FIXED_0");
    var first = await environment.PutAsync<OverrideRequest, WriteResponse>("slots/1/lock-feedback-override", request);
    var replay = await environment.PutAsync<OverrideRequest, WriteResponse>("slots/1/lock-feedback-override", request);
    Assert(first.Changed && !first.Replayed && replay.Replayed && replay.AppliedRevision == first.AppliedRevision, "同内容重试必须重放首次结果。");

    using var conflict = await environment.Client.PutAsJsonAsync(
        "slots/1/lock-feedback-override",
        request with { Mode = "FIXED_1" });
    Assert(conflict.StatusCode == HttpStatusCode.Conflict, "相同commandId不同内容应返回409。");
    var error = await conflict.Content.ReadFromJsonAsync<ErrorResponse>() ?? throw new InvalidOperationException("命令冲突响应为空。");
    Assert(error.ReasonCode == "COMMAND_ID_CONFLICT", "应返回COMMAND_ID_CONFLICT。");
}

static async Task TestNoChangeCommandAsync(TestEnvironment environment)
{
    var snapshot = await environment.ResetAsync("reset-no-change");
    var cargo = await environment.PutAsync<CargoRequest, WriteResponse>(
        "slots/1/cargo",
        new CargoRequest(snapshot.RunId, "no-change-empty", snapshot.Revision, "EMPTY"));
    Assert(!cargo.Changed && cargo.Revision == snapshot.Revision, "设置已有空仓状态不应增加revision。");

    var close = await environment.PostAsync<ResetRequest, WriteResponse>(
        "slots/1/close-door",
        new ResetRequest(cargo.RunId, "no-change-close", cargo.Revision));
    Assert(!close.Changed && close.Revision == cargo.Revision, "关闭已关闭仓门不应增加revision。");
}

static Task TestRejectNonLoopbackAsync(TestEnvironment environment)
{
    var settings = TestEnvironment.CreateSettings(1502, 58006);
    settings.Automation.ListenAddress = "0.0.0.0";
    try
    {
        settings.Validate();
    }
    catch (InvalidDataException)
    {
        return Task.CompletedTask;
    }

    throw new InvalidOperationException("AutomationHost必须拒绝非loopback HTTP监听地址。");
}

static void Assert(bool condition, string message)
{
    if (!condition)
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

file sealed class TestEnvironment : IAsyncDisposable
{
    public static readonly byte[] WriteDo0On =
    [
        0x12, 0x34, 0x00, 0x00, 0x00, 0x06, 0xFF,
        0x05, 0x00, 0x64, 0xFF, 0x00
    ];

    public static readonly byte[] ReadAllDi =
    [
        0x12, 0x35, 0x00, 0x00, 0x00, 0x06, 0xFF,
        0x02, 0x00, 0xC8, 0x00, 0x10
    ];

    private readonly SimulatorEngine _engine;
    private readonly ModbusTcpServer _modbusServer;
    private readonly AutomationHttpServer _httpServer;

    private TestEnvironment(
        int modbusPort,
        HttpClient client,
        HttpClient openApiClient,
        SimulatorEngine engine,
        ModbusTcpServer modbusServer,
        AutomationHttpServer httpServer)
    {
        ModbusPort = modbusPort;
        Client = client;
        OpenApiClient = openApiClient;
        _engine = engine;
        _modbusServer = modbusServer;
        _httpServer = httpServer;
    }

    public int ModbusPort { get; }
    public HttpClient Client { get; }
    public HttpClient OpenApiClient { get; }

    public static async Task<TestEnvironment> StartAsync()
    {
        var modbusPort = GetFreeTcpPort();
        var httpPort = GetFreeTcpPort();
        while (httpPort == modbusPort)
            httpPort = GetFreeTcpPort();

        var settings = CreateSettings(modbusPort, httpPort);
        var engine = new SimulatorEngine(settings);
        var modbusServer = new ModbusTcpServer(engine);
        var httpServer = new AutomationHttpServer(settings, engine, modbusServer);
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/api/v1/") };
        var openApiClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/") };
        var environment = new TestEnvironment(modbusPort, client, openApiClient, engine, modbusServer, httpServer);
        try
        {
            await modbusServer.StartAsync();
            await httpServer.StartAsync();
            await environment.WaitForHostAsync();
            return environment;
        }
        catch
        {
            await environment.DisposeAsync();
            throw;
        }
    }

    public async Task<SnapshotResponse> ResetAsync(string commandId)
    {
        var current = await GetSnapshotAsync();
        var response = await PostAsync<ResetRequest, WriteResponse>(
            "reset",
            new ResetRequest(current.RunId, commandId, current.Revision));
        return response.Snapshot;
    }

    public async Task<SnapshotResponse> GetSnapshotAsync() =>
        await Client.GetFromJsonAsync<SnapshotResponse>("snapshot") ?? throw new InvalidOperationException("snapshot响应为空。");

    public async Task<SnapshotResponse> WaitForSnapshotAsync(Func<SnapshotResponse, bool> predicate)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(4);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            var snapshot = await GetSnapshotAsync();
            if (predicate(snapshot))
                return snapshot;
            await Task.Delay(20);
        }
        throw new TimeoutException("等待模拟器状态变化超时。");
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(string path, TRequest request)
    {
        using var response = await Client.PostAsJsonAsync(path, request);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<TResponse>() ?? throw new InvalidOperationException($"{path}响应为空。");
    }

    public async Task<TResponse> PutAsync<TRequest, TResponse>(string path, TRequest request)
    {
        using var response = await Client.PutAsJsonAsync(path, request);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<TResponse>() ?? throw new InvalidOperationException($"{path}响应为空。");
    }

    public async Task<byte[]> SendModbusAsync(byte[] request, int responseLength)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ModbusPort);
        using var stream = client.GetStream();
        await stream.WriteAsync(request);
        var response = new byte[responseLength];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var offset = 0;
        while (offset < response.Length)
        {
            var read = await stream.ReadAsync(response.AsMemory(offset), timeout.Token);
            if (read == 0)
                throw new IOException("Modbus连接在完整响应前关闭。");
            offset += read;
        }
        return response;
    }

    private async Task WaitForHostAsync()
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            try
            {
                using var response = await Client.GetAsync("health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("AutomationHost启动超时。");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var content = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {content}");
    }

    public static SimulatorSettings CreateSettings(int modbusPort, int httpPort) => new()
    {
        InstanceId = "automation-test-instance",
        Modbus = new ModbusSettings
        {
            ListenAddress = "127.0.0.1",
            Port = modbusPort,
            UnitId = 255,
            StrictUnitId = true,
            DoPduBaseAddress = 100,
            DiPduBaseAddress = 200,
            DoChannelCount = 16,
            DiChannelCount = 16
        },
        Automation = new AutomationSettings
        {
            ListenAddress = "127.0.0.1",
            Port = httpPort
        },
        Defaults = new SimulatorDefaults
        {
            OutputMode = "Pulse",
            PulseWidthMs = 1000,
            UnlockFeedbackDelayMs = 250,
            LockFeedbackDelayMs = 50,
            LightCurtainFeedbackDelayMs = 50,
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

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        OpenApiClient.Dispose();
        await _httpServer.DisposeAsync();
        await _modbusServer.DisposeAsync();
        _engine.Dispose();
    }
}
