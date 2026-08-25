using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using SQCD_8005AGV_Simulator.Core.Configuration;
using SQCD_8005AGV_Simulator.Core.Services;

namespace SQCD_8005AGV_Simulator.AutomationHost;

/// <summary>
/// 将自动化 HTTP 控制面嵌入现有模拟器进程，不创建或拥有物理状态。
/// </summary>
public sealed class AutomationHttpServer : IAsyncDisposable
{
    private readonly SimulatorSettings _settings;
    private readonly SimulatorEngine _engine;
    private readonly ModbusTcpServer _modbusServer;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private WebApplication? _application;

    public AutomationHttpServer(
        SimulatorSettings settings,
        SimulatorEngine engine,
        ModbusTcpServer modbusServer)
    {
        _settings = settings;
        _engine = engine;
        _modbusServer = modbusServer;
    }

    public string Endpoint => $"http://{_settings.Automation.ListenAddress}:{_settings.Automation.Port}";
    public bool IsRunning => _application is not null;

    public event EventHandler<string>? LogEmitted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_application is not null)
                return;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(AutomationHttpServer).Assembly.GetName().Name,
                EnvironmentName = Environments.Production,
                Args = []
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(Endpoint);
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

            var application = builder.Build();
            AutomationApi.Map(application, _engine, _modbusServer);

            try
            {
                await application.StartAsync(cancellationToken);
                _application = application;
                EmitLog($"HTTP 自动化接口已监听 {Endpoint}/api/v1，OpenAPI={Endpoint}/openapi/v1.json。");
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var application = _application;
            if (application is null)
                return;

            _application = null;
            try
            {
                await application.StopAsync(cancellationToken);
            }
            finally
            {
                await application.DisposeAsync();
            }

            EmitLog("HTTP 自动化接口已停止。");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void EmitLog(string message) =>
        LogEmitted?.Invoke(this, $"{DateTime.Now:HH:mm:ss.fff} {message}");

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleLock.Dispose();
    }
}
