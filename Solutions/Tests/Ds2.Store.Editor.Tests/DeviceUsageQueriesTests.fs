module Ds2.Store.Editor.Tests.DeviceUsageQueriesTests

open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

// Device 사용 현황(#263) — Device 는 독립 System 이라 여러 System 이 같이 쓴다(이름 `{Flow}_{별칭}` 로 공유).
// 그래서 소속이 아니라 어디에 쓰이는지를 보여 주고, 아무도 안 쓰면 미사용으로 표시한다.

let private deviceOf (store: DsStore) (call: Call) =
    store.ApiDefs.[call.ApiCalls.[0].ApiDefId.Value].ParentId

[<Fact>]
let ``usage lists every system and call that uses a shared device including condition use`` () =
    let store = createStore ()
    let project = addProject store "P"
    let systemA = addSystem store "LineA" project.Id true
    let systemB = addSystem store "LineB" project.Id true
    let workA = addWork store "WA" (addFlow store "F" systemA.Id).Id
    let workB = addWork store "WB" (addFlow store "F" systemB.Id).Id
    store.AddCallsWithDevice(project.Id, workA.Id, [ "Cyl.Adv"; "Other.Adv" ], true, None)
    store.AddCallsWithDevice(project.Id, workB.Id, [ "Cyl.Adv" ], true, None)
    let callsA = Queries.callsOf workA.Id store
    let cylA = callsA |> List.find (fun c -> c.Name.StartsWith "Cyl")
    let otherA = callsA |> List.find (fun c -> c.Name.StartsWith "Other")
    let cylDevice = deviceOf store cylA
    Assert.Equal(cylDevice, deviceOf store (Queries.callsOf workB.Id store |> List.head))
    store.AddConditionWithApiCalls(otherA.Id, ConditionType.AutoAux, [ cylA.ApiCalls.[0].Id ]) |> ignore

    let usage = DeviceUsageQueries.usageOf store cylDevice

    Assert.Equal<string list>([ "LineA"; "LineB" ], usage |> List.map (fun u -> u.SystemName) |> List.distinct)
    Assert.Contains(usage, fun u -> u.OwnerId = otherA.Id && u.IsCondition)
    Assert.Contains(usage, fun u -> u.OwnerId = cylA.Id && not u.IsCondition && u.Path = "LineA / F / WA / " + cylA.Name)
    let summary = DeviceUsageQueries.summarize usage
    Assert.False(summary.IsUnused)
    Assert.Equal("사용 현황: LineA 2곳, LineB 1곳", summary.Text)

[<Fact>]
let ``a device that no call uses any more is summarized as unused`` () =
    let store = createStore ()
    let project, _, _, work = setupBasicHierarchy store
    store.AddCallsWithDevice(project.Id, work.Id, [ "Cyl.Adv" ], true, None)
    let call = Queries.callsOf work.Id store |> List.head
    let device = deviceOf store call

    store.RemoveEntities([ (EntityKind.Call, call.Id) ])

    Assert.True(store.Systems.ContainsKey device)
    Assert.Empty(DeviceUsageQueries.usageOf store device)
    Assert.True((DeviceUsageQueries.summariesOf store [ device ]).[device].IsUnused)
