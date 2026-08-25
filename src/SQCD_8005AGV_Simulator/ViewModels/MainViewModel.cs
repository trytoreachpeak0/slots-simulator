using System.Collections.ObjectModel;
using System.Windows;
using SQCD_8005AGV_Simulator.AutomationHost;
using SQCD_8005AGV_Simulator.Core.Configuration;
using SQCD_8005AGV_Simulator.Core.Models;
using SQCD_8005AGV_Simulator.Core.Services;

namespace SQCD_8005AGV_Simulator.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SynchronizationContext _uiContext;
    private readonly SimulatorEngine _engine;
    private readonly ModbusTcpServer _server;
    private readonly AutomationHttpServer _automationServer;
    private string _serverStatus = "未启动";
    private string _rawDo = string.Empty;
    private string _rawDi = string.Empty;
    private bool _ignoreRequests;
    private bool _logReadRequests;
    private string _globalFaultSummary = string.Empty;
    private Visibility _globalFaultVisibility = Visibility.Collapsed;
    private int _openDoorCount;
    private int _maxOpenDoors;
    private ModbusFaultMode _modbusFaultMode;
    private int _modbusDelayMs;

    public MainViewModel(SimulatorSettings settings)
    {
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        Settings = settings;
        _engine = new SimulatorEngine(settings);
        _server = new ModbusTcpServer(_engine);
        _automationServer = new AutomationHttpServer(settings, _engine, _server);
        _engine.StateChanged += OnStateChanged;
        _engine.LogEmitted += OnLog;
        _server.LogEmitted += OnLog;
        _automationServer.LogEmitted += OnLog;
        _server.ConnectionStateChanged += OnConnectionStateChanged;

        var snapshot = _engine.GetSnapshot();
        foreach (var slot in snapshot.Slots)
            Slots.Add(new SlotViewModel(slot));
        ApplySnapshot(snapshot);
    }

    public SimulatorSettings Settings { get; }
    public ObservableCollection<SlotViewModel> Slots { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public string Endpoint => $"{Settings.Modbus.ListenAddress}:{Settings.Modbus.Port}";
    public string HttpEndpoint => $"http://{Settings.Automation.ListenAddress}:{Settings.Automation.Port}";
    public string UnitIdText => $"0x{Settings.Modbus.UnitId:X2}";

    public string ServerStatus
    {
        get => _serverStatus;
        private set => SetProperty(ref _serverStatus, value);
    }

    public string RawDo
    {
        get => _rawDo;
        private set => SetProperty(ref _rawDo, value);
    }

    public string RawDi
    {
        get => _rawDi;
        private set => SetProperty(ref _rawDi, value);
    }

    public bool IgnoreRequests
    {
        get => _ignoreRequests;
        set
        {
            if (SetProperty(ref _ignoreRequests, value))
            {
                _server.IgnoreRequests = value;
                AppendLog(value ? "已启用“不响应请求”故障。" : "已清除“不响应请求”故障。");
                UpdateGlobalFaultSummary();
            }
        }
    }

    public bool LogReadRequests
    {
        get => _logReadRequests;
        set
        {
            if (SetProperty(ref _logReadRequests, value))
            {
                _server.LogReadRequests = value;
                AppendLog(value
                    ? "已开启周期读取日志：FC01/FC02 的每次轮询都会显示。"
                    : "已关闭周期读取日志：继续正常响应 FC01/FC02，但不逐条显示。");
            }
        }
    }

    public string GlobalFaultSummary
    {
        get => _globalFaultSummary;
        private set => SetProperty(ref _globalFaultSummary, value);
    }

    public Visibility GlobalFaultVisibility
    {
        get => _globalFaultVisibility;
        private set => SetProperty(ref _globalFaultVisibility, value);
    }

    public async Task StartAsync()
    {
        try
        {
            await _server.StartAsync();
            await _automationServer.StartAsync();
            RefreshServerStatus();
        }
        catch (Exception ex)
        {
            await _automationServer.StopAsync();
            await _server.StopAsync();
            ServerStatus = "启动失败";
            AppendLog($"服务启动失败：{ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        await _automationServer.StopAsync();
        await _server.StopAsync();
        RefreshServerStatus();
    }

    public void DisconnectClients() => _server.DisconnectAllClients();
    public void Reset()
    {
        IgnoreRequests = false;
        _engine.Reset();
    }
    public OperationResult PlaceCargo(int slotIndex) => _engine.PlaceCargo(slotIndex);
    public OperationResult RemoveCargo(int slotIndex) => _engine.RemoveCargo(slotIndex);
    public OperationResult CloseDoor(int slotIndex) => _engine.CloseDoor(slotIndex);
    public void SetLockOverride(int slotIndex, bool? value) => _engine.SetLockFeedbackOverride(slotIndex, value);
    public void SetLightOverride(int slotIndex, bool? value) => _engine.SetLightCurtainOverride(slotIndex, value);

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var snapshot = _engine.GetSnapshot();
        _uiContext.Post(_ => ApplySnapshot(snapshot), null);
    }

    private void OnLog(object? sender, string message) => _uiContext.Post(_ => AppendLog(message), null);

    private void OnConnectionStateChanged(object? sender, EventArgs e) => _uiContext.Post(_ => RefreshServerStatus(), null);

    private void ApplySnapshot(SimulatorSnapshot snapshot)
    {
        for (var i = 0; i < Slots.Count && i < snapshot.Slots.Count; i++)
            Slots[i].Update(snapshot.Slots[i]);
        RawDo = "DO  " + string.Join("  ", snapshot.DoStates.Select((value, index) => $"{index}:{Convert.ToInt32(value)}"));
        RawDi = "DI   " + string.Join("  ", snapshot.DiStates.Select((value, index) => $"{index}:{Convert.ToInt32(value)}"));
        _openDoorCount = snapshot.OpenDoorCount;
        _maxOpenDoors = snapshot.MaxOpenDoors;
        _modbusFaultMode = snapshot.ModbusFault.Mode;
        _modbusDelayMs = snapshot.ModbusFault.DelayMs;
        SetProperty(ref _ignoreRequests, _modbusFaultMode == ModbusFaultMode.NoResponse, nameof(IgnoreRequests));
        UpdateGlobalFaultSummary();
    }

    private void UpdateGlobalFaultSummary()
    {
        var messages = new List<string>();
        switch (_modbusFaultMode)
        {
            case ModbusFaultMode.NoResponse:
                messages.Add("通信不响应故障已启用：客户端连接会保持，但所有 Modbus 请求都不会收到响应。");
                break;
            case ModbusFaultMode.Disconnect:
                messages.Add("通信断开故障已启用：现有和新建 Modbus 连接都会被断开。");
                break;
            case ModbusFaultMode.Delay:
                messages.Add($"通信延迟故障已启用：每个 Modbus 请求延迟 {_modbusDelayMs}ms 后处理。");
                break;
        }

        var abnormalSlots = Slots.Where(slot => slot.HasFault).Select(slot => slot.DisplayNumber).ToArray();
        if (abnormalSlots.Length > 0)
            messages.Add($"仓位 {string.Join("、", abnormalSlots)} 正在进行故障注入，或存在内部事实与传感器反馈不一致。");

        if (_openDoorCount > _maxOpenDoors)
            messages.Add($"当前同时打开 {_openDoorCount} 个仓门，超过配置允许值 {_maxOpenDoors}；仿真器保留真实输出结果，不会自动替车载端关门。");

        GlobalFaultSummary = string.Join(Environment.NewLine, messages.Select(message => $"⚠ {message}"));
        GlobalFaultVisibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshServerStatus()
    {
        ServerStatus = _server.IsRunning && _automationServer.IsRunning
            ? $"运行中 · Modbus {_server.ClientCount} 个客户端 · HTTP 已就绪"
            : "已停止";
    }

    private void AppendLog(string message)
    {
        Logs.Add(message);
        while (Logs.Count > 500)
            Logs.RemoveAt(0);
    }

    public async ValueTask DisposeAsync()
    {
        _engine.StateChanged -= OnStateChanged;
        _engine.LogEmitted -= OnLog;
        _server.LogEmitted -= OnLog;
        _automationServer.LogEmitted -= OnLog;
        _server.ConnectionStateChanged -= OnConnectionStateChanged;
        await _automationServer.DisposeAsync();
        await _server.DisposeAsync();
        _engine.Dispose();
    }
}
