using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Editor;

namespace Promaker.ViewModels;

/// <summary>
/// Device(Passive) System 속성의 "사용 현황"(#263) — 어느 System 의 어느 Call 이 이 Device 를 쓰는지.
/// Device 는 독립 System 이라 여러 System 이 같이 쓸 수 있다. 계산은 F#(DeviceUsageQueries).
/// </summary>
public partial class PropertyPanelState
{
    public ObservableCollection<DeviceUsageQueries.DeviceUsageItem> DeviceUsages { get; } = [];

    [ObservableProperty] private bool _showDeviceUsage;

    public string DeviceUsageHeader =>
        DeviceUsages.Count == 0 ? "사용 현황 — 미사용" : $"사용 현황 [{DeviceUsages.Count}]";

    private void RefreshDeviceUsagePanel(Guid systemId, bool isPassiveSystem)
    {
        ShowDeviceUsage = isPassiveSystem;
        DeviceUsages.Clear();
        if (isPassiveSystem
            && _host.TryRef(() => DeviceUsageQueries.usageOf(Store, systemId), out var items))
        {
            foreach (var item in items)
                DeviceUsages.Add(item);
        }
        OnPropertyChanged(nameof(DeviceUsageHeader));
    }
}
