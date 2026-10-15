module Ds2.Store.Editor.Tests.DeviceImportTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// Device CSV 불러오기(#253) — Device·Api 선언을 기존 프로젝트에 더한다. 같은 이름의 Api 는 사용자가 고른 대로
// 건너뛰거나 새 이름(Api_1 …)으로 넣는다. Undo 한 번에 모두 돌아간다.

let private spec device api =
    DeviceApiSpec(device, api, ActionType.Normal None, SensingType.Normal None, "")

let private apiNames (store: DsStore) (deviceId: System.Guid) =
    Queries.apiDefsOf deviceId store |> List.map (fun d -> d.Name) |> List.sort

[<Fact>]
let ``a new device gets a flow, one work per api and api defs pointing at their work`` () =
    let store = createStore ()
    let project = addProject store "P"
    let specs =
        [ DeviceApiSpec("Cyl", "Adv", ActionType.Pulse (Some 200), SensingType.Latch 300, "전진")
          spec "Cyl" "Ret" ]

    let result = store.ImportDeviceApis(project.Id, specs, DeviceApiConflictMode.Skip)

    Assert.Equal((1, 2), (result.AddedDevices, result.AddedApis))
    let device = Queries.passiveSystemsOf project.Id store |> List.find (fun s -> s.Name = "Cyl")
    let flow = Queries.flowsOf device.Id store |> List.exactlyOne
    Assert.Equal("Cyl_Flow", flow.Name)
    let adv = Queries.apiDefsOf device.Id store |> List.find (fun d -> d.Name = "Adv")
    Assert.Equal(ActionType.Pulse (Some 200), adv.ActionType)
    Assert.Equal(SensingType.Latch 300, adv.SensingType)
    Assert.Equal(Some "전진", adv.Description)
    let advWork = store.Works.[adv.TxGuid.Value]
    Assert.Equal("Adv", advWork.LocalName)
    Assert.Equal(adv.TxGuid, adv.RxGuid)
    Assert.Contains(Queries.arrowWorksOf device.Id store, fun a -> a.ArrowType = ArrowType.ResetReset)

[<Fact>]
let ``existing api names are skipped or renamed as chosen and undo restores the device`` () =
    let store = createStore ()
    let project = addProject store "P"
    store.ImportDeviceApis(project.Id, [ spec "Cyl" "Adv"; spec "Cyl" "Ret" ], DeviceApiConflictMode.Skip) |> ignore
    let device = Queries.passiveSystemsOf project.Id store |> List.find (fun s -> s.Name = "Cyl")
    let incoming = [ spec "Cyl" "Adv"; spec "Cyl" "Hold" ]

    let conflicts = store.FindDeviceApiConflicts(project.Id, incoming)
    Assert.Equal<string list>([ "Cyl.Adv" ], conflicts |> List.map (fun c -> c.FullName))

    let skipped = store.ImportDeviceApis(project.Id, incoming, DeviceApiConflictMode.Skip)
    Assert.Equal<string list>([ "Cyl.Adv" ], skipped.Skipped)
    Assert.Equal<string list>([ "Adv"; "Hold"; "Ret" ], apiNames store device.Id)

    store.Undo()
    let renamed = store.ImportDeviceApis(project.Id, incoming, DeviceApiConflictMode.Rename)
    Assert.Equal<(string * string) list>([ ("Cyl.Adv", "Cyl.Adv_1") ], renamed.Renamed)
    Assert.Equal<string list>([ "Adv"; "Adv_1"; "Hold"; "Ret" ], apiNames store device.Id)

    store.Undo()
    Assert.Equal<string list>([ "Adv"; "Ret" ], apiNames store device.Id)
