module Ds2.Store.Editor.Tests.DeviceImportTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// Device CSV 불러오기(#253) — Device·Api 선언을 기존 프로젝트에 더한다. 프로젝트에 같은 이름의 System 이 있으면
// 사용자가 고른 대로 그 Device 를 통째로 건너뛰거나 새 이름(Cyl_1 …)의 Device 로 만든다. Undo 한 번에 모두 돌아간다.

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

    let result = store.ImportDevices(project.Id, specs, DeviceNameConflictMode.Skip)

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

/// 기존 Device 안에 Api 를 섞어 넣지 않는다 — CSV 의 Device 는 그 자체로 하나의 Device 라서, 이름이 겹치면 통째로
/// 건너뛰거나 새 이름의 Device 로 만든다. 예전엔 Api 단위로 겹침을 봐서 기존 Device 안에 Adv_1 같은 Api 가 생겼다.
[<Fact>]
let ``an existing device name is skipped as a whole or imported as a new device with a free name`` () =
    let store = createStore ()
    let project = addProject store "P"
    store.ImportDevices(project.Id, [ spec "Cyl" "Adv"; spec "Cyl" "Ret" ], DeviceNameConflictMode.Skip) |> ignore
    let original = Queries.passiveSystemsOf project.Id store |> List.find (fun s -> s.Name = "Cyl")
    let devices () = Queries.passiveSystemsOf project.Id store |> List.map (fun s -> s.Name) |> List.sort
    let incoming = [ spec "Cyl" "Adv"; spec "Cyl" "Hold"; spec "Lamp" "On" ]

    Assert.Equal<string list>([ "Cyl" ], store.FindExistingDeviceNames(project.Id, incoming))

    let skipped = store.ImportDevices(project.Id, incoming, DeviceNameConflictMode.Skip)
    Assert.Equal<string list>([ "Cyl" ], skipped.SkippedDevices)
    Assert.Equal<string list>([ "Cyl"; "Lamp" ], devices ())
    Assert.Equal<string list>([ "Adv"; "Ret" ], apiNames store original.Id)

    store.Undo()
    let renamed = store.ImportDevices(project.Id, incoming, DeviceNameConflictMode.Rename)
    Assert.Equal<(string * string) list>([ ("Cyl", "Cyl_1") ], renamed.RenamedDevices)
    Assert.Equal<string list>([ "Cyl"; "Cyl_1"; "Lamp" ], devices ())
    let copy = Queries.passiveSystemsOf project.Id store |> List.find (fun s -> s.Name = "Cyl_1")
    Assert.Equal<string list>([ "Adv"; "Hold" ], apiNames store copy.Id)
    Assert.Equal<string list>([ "Adv"; "Ret" ], apiNames store original.Id)

    store.Undo()
    Assert.Equal<string list>([ "Cyl" ], devices ())

/// Active System 과 이름이 같은 Device 도 겹침으로 본다 — 같은 이름의 System 이 둘이면 트리와 CSV 에서 구분이 안 된다.
[<Fact>]
let ``a device named like an active system is treated as an existing name`` () =
    let store = createStore ()
    let project = addProject store "P"
    addSystem store "Line" project.Id true |> ignore

    Assert.Equal<string list>([ "Line" ], store.FindExistingDeviceNames(project.Id, [ spec "Line" "On" ]))
