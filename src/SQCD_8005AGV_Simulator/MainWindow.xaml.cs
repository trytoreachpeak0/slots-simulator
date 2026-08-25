using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SQCD_8005AGV_Simulator.Core.Configuration;
using SQCD_8005AGV_Simulator.Core.Services;
using SQCD_8005AGV_Simulator.ViewModels;

namespace SQCD_8005AGV_Simulator;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        var configPath = Path.Combine(AppContext.BaseDirectory, "simulator.settings.json");
        try
        {
            _viewModel = new MainViewModel(SimulatorSettings.Load(configPath));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"配置加载失败：{ex.Message}\n\n配置文件：{configPath}", "无法启动", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }

        DataContext = _viewModel;
        _viewModel.Logs.CollectionChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (AutoScrollLogsCheckBox.IsChecked == true && LogList.Items.Count > 0)
                    LogList.ScrollIntoView(LogList.Items[^1]);
            });
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();

    private async void StartServer_Click(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();
    private async void StopServer_Click(object sender, RoutedEventArgs e) => await _viewModel.StopAsync();
    private void DisconnectClients_Click(object sender, RoutedEventArgs e) => _viewModel.DisconnectClients();
    private void Reset_Click(object sender, RoutedEventArgs e) => _viewModel.Reset();
    private void ClearLogs_Click(object sender, RoutedEventArgs e) => _viewModel.Logs.Clear();

    private void PlaceCargo_Click(object sender, RoutedEventArgs e) => ShowResult(_viewModel.PlaceCargo(GetSlotIndex(sender)));
    private void RemoveCargo_Click(object sender, RoutedEventArgs e) => ShowResult(_viewModel.RemoveCargo(GetSlotIndex(sender)));
    private void CloseDoor_Click(object sender, RoutedEventArgs e) => ShowResult(_viewModel.CloseDoor(GetSlotIndex(sender)));

    private void LockNormal_Click(object sender, RoutedEventArgs e) => _viewModel.SetLockOverride(GetSlotIndex(sender), null);
    private void LockZero_Click(object sender, RoutedEventArgs e) => _viewModel.SetLockOverride(GetSlotIndex(sender), false);
    private void LockOne_Click(object sender, RoutedEventArgs e) => _viewModel.SetLockOverride(GetSlotIndex(sender), true);
    private void LightNormal_Click(object sender, RoutedEventArgs e) => _viewModel.SetLightOverride(GetSlotIndex(sender), null);
    private void LightZero_Click(object sender, RoutedEventArgs e) => _viewModel.SetLightOverride(GetSlotIndex(sender), false);
    private void LightOne_Click(object sender, RoutedEventArgs e) => _viewModel.SetLightOverride(GetSlotIndex(sender), true);

    private static int GetSlotIndex(object sender) => Convert.ToInt32(((Button)sender).Tag);

    private static void ShowResult(OperationResult result)
    {
        if (!result.IsSuccess)
            MessageBox.Show(result.Error, "操作未执行", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
            return;

        e.Cancel = true;
        _isClosing = true;
        await _viewModel.DisposeAsync();
        Close();
    }
}
