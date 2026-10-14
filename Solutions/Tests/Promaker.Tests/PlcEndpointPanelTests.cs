using System;
using System.Linq;
using System.Reflection;
using Ds2.Core.StandardSubmodels;
using Ds2.Core.Store;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests;

/// <summary>
/// System 속성 패널의 PLC 연결 — 고를 수 있는 벤더는 AID 로 표현되는 것뿐이고, 접속은 주소(ApiDef I/O)가 없어도
/// 저장된다. 종전엔 Mitsubishi 가 목록에 있어 고르면 저장이 안 됐고, 새 프로젝트에서 I/O 전에 IP 를 넣으면
/// "저장 실패" 가 떴다.
/// </summary>
public sealed class PlcEndpointPanelTests
{
    [Fact]
    public void Vendor_choices_are_exactly_the_vendors_an_AID_can_describe()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();

            var choices = vm.PropertyPanel.PlcVendorChoices.ToList();
            var describable = Enum.GetValues<PlcVendorChoice>()
                .Where(v => PlcVendorProfile.IsAidXgtVendor(v) || v == PlcVendorChoice.MicrexSx)
                .ToList();

            Assert.Equal(describable, choices);
            Assert.DoesNotContain(PlcVendorChoice.Mitsubishi, choices);
        });
    }

    [Fact]
    public void Saving_an_LS_connection_before_any_io_is_mapped_succeeds_and_marks_the_model_dirty()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);
            StaTestRunner.PumpPendingUi();
            var store = (DsStore)typeof(MainViewModel)
                .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var systemId = Assert.Single(store.Systems.Keys);
            vm.IsDirty = false;

            var profile = PlcVendorProfile.Defaults(PlcVendorChoice.LsXgi);
            profile.IpAddress = "192.168.0.50";
            profile.Port = 2004;

            Assert.True(vm.Simulation.SavePlcEndpointForSystem(systemId, PlcVendorChoice.LsXgi, profile, null, null));

            var saved = store.TryReadXgtEndpoint(systemId);
            Assert.NotNull(saved);
            Assert.Equal("192.168.0.50", saved.IpAddress);
            Assert.True(vm.IsDirty);
        });
    }
}
