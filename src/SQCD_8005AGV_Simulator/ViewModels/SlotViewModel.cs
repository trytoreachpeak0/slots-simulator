using System.Windows;
using System.Windows.Media;
using SQCD_8005AGV_Simulator.Core.Models;

namespace SQCD_8005AGV_Simulator.ViewModels;

public sealed class SlotViewModel : ObservableObject
{
    private static readonly Brush NormalCardBrush = CreateBrush("#F9FBFD");
    private static readonly Brush DoorOpenCardBrush = CreateBrush("#FFF8ED");
    private static readonly Brush CargoCardBrush = CreateBrush("#F2FAF5");
    private static readonly Brush FaultCardBrush = CreateBrush("#FFF4F4");
    private static readonly Brush NormalBorderBrush = CreateBrush("#CEDAE5");
    private static readonly Brush FaultBorderBrush = CreateBrush("#C94747");
    private static readonly Brush IdleBadgeBrush = CreateBrush("#E5EDF4");
    private static readonly Brush DoorOpenBadgeBrush = CreateBrush("#F5DDBB");
    private static readonly Brush CargoBadgeBrush = CreateBrush("#CDEAD8");
    private static readonly Brush FaultBadgeBrush = CreateBrush("#F2CACA");
    private static readonly Brush PendingBadgeBrush = CreateBrush("#D8E8F5");
    private static readonly Brush NormalSelectionBrush = CreateBrush("#DDEFE4");
    private static readonly Brush FaultSelectionBrush = CreateBrush("#F3CECE");
    private static readonly Brush UnselectedButtonBrush = CreateBrush("#F0F4F7");

    private SlotSnapshot _snapshot;

    public SlotViewModel(SlotSnapshot snapshot) => _snapshot = snapshot;

    public int SlotIndex => _snapshot.SlotIndex;
    public int DisplayNumber => _snapshot.DisplayNumber;
    public string Title => $"{DisplayNumber} 号仓位  (index {SlotIndex})";
    public string ChannelText => $"DO{_snapshot.UnlockDoChannel} / 锁DI{_snapshot.LockFeedbackDiChannel} / 光幕DI{_snapshot.LightCurtainDiChannel}";
    public string DoText => $"DO：{Convert.ToInt32(_snapshot.UnlockDoRaw)}  {(_snapshot.UnlockDoRaw ? "触点闭合" : "触点断开")}";
    public string LockText => $"锁反馈：{Convert.ToInt32(_snapshot.LockFeedbackDiRaw)}  {(_snapshot.IsLocked ? "已锁" : "未锁")}{(_snapshot.LockFeedbackPending ? "（同步中）" : string.Empty)}";
    public string LightText => $"光幕：{Convert.ToInt32(_snapshot.LightCurtainDiRaw)}  {(_snapshot.IsObstructed ? "遮挡" : "无遮挡")}{(_snapshot.LightCurtainFeedbackPending ? "（同步中）" : string.Empty)}";
    public string DoorText => _snapshot.DoorOpen ? "仓门：打开" : "仓门：关闭";
    public string CargoText => _snapshot.CargoPresent ? "货物事实：有货" : "货物事实：空";

    public bool HasOverride => _snapshot.LockFeedbackOverride.HasValue || _snapshot.LightCurtainOverride.HasValue;
    public bool HasLockMismatch => !_snapshot.LockFeedbackPending && _snapshot.DoorOpen == _snapshot.IsLocked;
    public bool HasLightMismatch => !_snapshot.LightCurtainFeedbackPending && _snapshot.CargoPresent != _snapshot.IsObstructed;
    public bool HasStateMismatch => HasLockMismatch || HasLightMismatch;
    public bool HasFault => HasOverride || HasStateMismatch;
    public bool IsFeedbackPending => _snapshot.LockFeedbackPending || _snapshot.LightCurtainFeedbackPending;

    public string StatusText => HasFault
        ? HasStateMismatch ? "反馈异常" : "故障注入"
        : IsFeedbackPending ? "反馈同步中"
        : _snapshot.DoorOpen ? "仓门打开"
        : _snapshot.CargoPresent ? "已装货"
        : "空闲";

    public string FaultTitle => HasStateMismatch ? "检测到事实与反馈不一致" : "故障注入已启用";

    public string FaultDetails
    {
        get
        {
            var messages = new List<string>();
            if (_snapshot.LockFeedbackOverride is { } lockRaw)
                messages.Add($"锁反馈被强制固定为原始值 {Convert.ToInt32(lockRaw)}");
            if (_snapshot.LightCurtainOverride is { } lightRaw)
                messages.Add($"光幕被强制固定为原始值 {Convert.ToInt32(lightRaw)}");
            if (HasLockMismatch)
                messages.Add(_snapshot.DoorOpen
                    ? "仓门已经打开，但锁反馈仍显示已锁"
                    : "仓门已经关闭，但锁反馈仍显示未锁");
            if (HasLightMismatch)
                messages.Add(_snapshot.CargoPresent
                    ? "货物已经放入，但光幕仍显示无遮挡"
                    : "仓位实际为空，但光幕仍显示遮挡");
            return string.Join(Environment.NewLine, messages.Select(message => $"• {message}"));
        }
    }

    public Visibility FaultVisibility => HasFault ? Visibility.Visible : Visibility.Collapsed;
    public Brush StatusBrush => HasFault ? FaultCardBrush : _snapshot.DoorOpen ? DoorOpenCardBrush : _snapshot.CargoPresent ? CargoCardBrush : NormalCardBrush;
    public Brush StatusBorderBrush => HasFault ? FaultBorderBrush : NormalBorderBrush;
    public double StatusBorderThickness => HasFault ? 2 : 1;
    public Brush StatusBadgeBrush => HasFault ? FaultBadgeBrush : IsFeedbackPending ? PendingBadgeBrush : _snapshot.DoorOpen ? DoorOpenBadgeBrush : _snapshot.CargoPresent ? CargoBadgeBrush : IdleBadgeBrush;
    public Brush LockBrush => _snapshot.IsLocked ? Brushes.SeaGreen : Brushes.DarkOrange;

    public Brush LockNormalButtonBrush => !_snapshot.LockFeedbackOverride.HasValue ? NormalSelectionBrush : UnselectedButtonBrush;
    public Brush LockZeroButtonBrush => _snapshot.LockFeedbackOverride == false ? FaultSelectionBrush : UnselectedButtonBrush;
    public Brush LockOneButtonBrush => _snapshot.LockFeedbackOverride == true ? FaultSelectionBrush : UnselectedButtonBrush;
    public Brush LightNormalButtonBrush => !_snapshot.LightCurtainOverride.HasValue ? NormalSelectionBrush : UnselectedButtonBrush;
    public Brush LightZeroButtonBrush => _snapshot.LightCurtainOverride == false ? FaultSelectionBrush : UnselectedButtonBrush;
    public Brush LightOneButtonBrush => _snapshot.LightCurtainOverride == true ? FaultSelectionBrush : UnselectedButtonBrush;

    public void Update(SlotSnapshot snapshot)
    {
        _snapshot = snapshot;
        RaisePropertyChanged(string.Empty);
    }

    private static Brush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
