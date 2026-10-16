module Ds2.Store.Editor.Tests.DsStoreEntityMutationTests

open System
open Xunit
open Ds2.Core
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module RemoveTests =

    [<Fact>]
    let ``RemoveEntities deletes project and cascades`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        store.RemoveEntities([ (EntityKind.Project, project.Id) ])
        Assert.Equal(0, store.Projects.Count)
        Assert.Equal(0, store.Systems.Count)
        Assert.Equal(0, store.Flows.Count)
        Assert.Equal(0, store.Works.Count)

    /// 삭제된 System 을 프로젝트 목록에서 빼는 일은 System 마다가 아니라 **프로젝트당 1회** mutate 다
    /// (trackMutate 스냅샷이 Project 를 통째로 JSON 왕복 복제해 AID 큰 모델에서 1회 ≈ 1초).
    /// 배치로 묶어도 목록 정리와 undo 원복이 정확한지 못 박는다.
    [<Fact>]
    let ``RemoveEntities detaches every selected system from the project in one pass`` () =
        let store = createStore ()
        let project = addProject store "P"
        let systems = [ for i in 1 .. 4 -> addSystem store $"Sys{i}" project.Id (i % 2 = 1) ]
        Assert.Equal(2, project.ActiveSystemIds.Count)
        Assert.Equal(2, project.PassiveSystemIds.Count)

        store.RemoveEntities(systems |> List.map (fun s -> (EntityKind.System, s.Id)))
        Assert.Empty(project.ActiveSystemIds)
        Assert.Empty(project.PassiveSystemIds)
        Assert.Equal(0, store.Systems.Count)

        store.Undo()
        let restored = store.Projects.[project.Id]
        Assert.Equal(4, store.Systems.Count)
        Assert.Equal(2, restored.ActiveSystemIds.Count)
        Assert.Equal(2, restored.PassiveSystemIds.Count)
        // 목록에 남은 id 가 전부 실재해야 한다 (dangling 금지)
        Assert.Empty(
            Seq.append restored.ActiveSystemIds restored.PassiveSystemIds
            |> Seq.filter (fun id -> not (store.Systems.ContainsKey id)))

    /// 프로젝트와 그 소속 System 을 같은 선택으로 지우는 경우. 목록 정리를 배치 말미로 미뤘으므로
    /// 이미 지워진 Project 를 mutate 하면 trackMutate 가 "Entity not found" 로 던진다 — skip 가드 확인.
    [<Fact>]
    let ``RemoveEntities handles a project deleted together with its own system`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "Sys1" project.Id true
        addSystem store "Sys2" project.Id false |> ignore

        store.RemoveEntities([ (EntityKind.System, system.Id); (EntityKind.Project, project.Id) ])
        Assert.Equal(0, store.Projects.Count)
        Assert.Equal(0, store.Systems.Count)

        store.Undo()
        Assert.Equal(1, store.Projects.Count)
        Assert.Equal(2, store.Systems.Count)

    [<Fact>]
    let ``RemoveEntities with work removes descendant calls`` () =
        let store = createStore ()
        let _, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(Guid.Empty, work.Id, ["Dev.Api"], false, None)
        Assert.True(store.Calls.Count > 0)
        store.RemoveEntities([ (EntityKind.Work, work.Id) ])
        Assert.Equal(0, store.Calls.Count)

    [<Fact>]
    let ``RemoveEntities keeps nested condition api call reference alive`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Src.Api"; "Target.Api" ], true, None)

        let sourceCall = Queries.callsOf work.Id store |> List.find (fun c -> c.Name = "Src.Api")
        let targetCall = Queries.callsOf work.Id store |> List.find (fun c -> c.Name = "Target.Api")
        let sourceApiCallId = sourceCall.ApiCalls |> Seq.head |> fun ac -> ac.Id

        store.AddCallCondition(targetCall.Id, ConditionType.ComAux)
        let parentCondition = targetCall.Conditions |> Seq.head
        store.AddChildCondition(targetCall.Id, parentCondition.Id, false)
        let childCondition = parentCondition.Children |> Seq.head

        let added = store.AddApiCallsToConditionBatch(targetCall.Id, childCondition.Id, [ sourceApiCallId ])
        Assert.Equal(1, added)

        store.RemoveEntities([ (EntityKind.Call, sourceCall.Id) ])

        Assert.True(store.ApiCalls.ContainsKey(sourceApiCallId))
        let nestedApiCallId =
            childCondition.ApiCalls
            |> Seq.head
            |> fun apiCall -> apiCall.Id
        Assert.Equal(sourceApiCallId, nestedApiCallId)

    [<Fact>]
    let ``RemoveEntities ApiDef detaches referencing ApiCall and removes it`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        let apiCallId = apiCall.Id
        Assert.True(apiCall.ApiDefId.IsSome)
        let apiDefId = apiCall.ApiDefId.Value

        // ApiDef(Device) 제거 → 이 ApiDef 를 참조하던 ApiCall 이 Call 직접 참조에서 떨어지고
        // orphan 정리로 store 에서도 제거됨 (ApiDef 만 남고 ApiCall 이 dangling = UNKNOWN I/O 방지). Call 자체는 보존.
        store.RemoveEntities([ (EntityKind.ApiDef, apiDefId) ])

        Assert.False(store.ApiDefs.ContainsKey(apiDefId))
        Assert.False(store.ApiCalls.ContainsKey(apiCallId))
        Assert.True(store.Calls.ContainsKey(call.Id))
        Assert.False(store.Calls.[call.Id].ApiCalls |> Seq.exists (fun ac -> ac.Id = apiCallId))

    /// ApiDef 여러 개를 한 선택으로 지울 때 — ApiCall 떼어내기는 ApiDef 마다가 아니라
    /// **배치당 Call 1회 순회**다(ApiDef 마다 훑으면 디바이스 다중 삭제가 O(ApiDef수 × 전체Call수)).
    /// 묶어도 선택한 ApiDef 전부의 ApiCall 이 떨어지는지 못 박는다.
    [<Fact>]
    let ``RemoveEntities detaches ApiCalls of every selected ApiDef in one pass`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "DevA.Api"; "DevB.Api"; "DevC.Api" ], true, None)
        let calls = Queries.callsOf work.Id store
        let apiCalls = calls |> List.collect (fun c -> c.ApiCalls |> List.ofSeq)
        let apiDefIds = apiCalls |> List.choose (fun ac -> ac.ApiDefId) |> List.distinct
        Assert.Equal(3, calls.Length)
        Assert.Equal(3, apiDefIds.Length)

        store.RemoveEntities(apiDefIds |> List.map (fun id -> (EntityKind.ApiDef, id)))

        for id in apiDefIds do
            Assert.False(store.ApiDefs.ContainsKey id)
        for ac in apiCalls do
            Assert.False(store.ApiCalls.ContainsKey ac.Id)
        // Call 자체는 보존되고 직접 참조 목록만 비워진다.
        for c in calls do
            Assert.True(store.Calls.ContainsKey c.Id)
            Assert.Empty(store.Calls.[c.Id].ApiCalls)

    /// ApiDef 와, 그 ApiCall 을 물고 있던 Call 의 부모 Work 를 같은 선택으로 지우는 경우.
    /// ApiCall 떼어내기는 역인덱스가 아니라 **살아있는** Calls 를 훑어야 한다 — 캐스케이드로 이미
    /// 지워진 Call 에 trackMutate 를 걸면 "Entity not found" 로 던진다. undo 도 온전해야 한다
    /// (그 Call 의 제거 스냅샷은 ApiCalls 를 그대로 안고 있다).
    [<Fact>]
    let ``RemoveEntities handles ApiDef selected together with the work owning its calls`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = Queries.callsOf work.Id store |> List.head
        let apiCall = call.ApiCalls |> Seq.head
        let apiCallId = apiCall.Id
        let apiDefId = apiCall.ApiDefId.Value

        store.RemoveEntities([ (EntityKind.Work, work.Id); (EntityKind.ApiDef, apiDefId) ])

        Assert.False(store.Works.ContainsKey work.Id)
        Assert.False(store.Calls.ContainsKey call.Id)
        Assert.False(store.ApiDefs.ContainsKey apiDefId)
        Assert.False(store.ApiCalls.ContainsKey apiCallId)

        store.Undo()
        Assert.True(store.Works.ContainsKey work.Id)
        Assert.True(store.ApiDefs.ContainsKey apiDefId)
        Assert.True(store.ApiCalls.ContainsKey apiCallId)
        Assert.True(store.Calls.ContainsKey call.Id)
        Assert.True(store.Calls.[call.Id].ApiCalls |> Seq.exists (fun ac -> ac.Id = apiCallId))

    [<Fact>]
    let ``Undo restores deleted entities`` () =
        let store = createStore ()
        let project, _, _, _ = setupBasicHierarchy store
        let projectCount = store.Projects.Count
        store.RemoveEntities([ (EntityKind.Project, project.Id) ])
        Assert.Equal(0, store.Projects.Count)
        store.Undo()
        Assert.Equal(projectCount, store.Projects.Count)

// =============================================================================
// Rename
// =============================================================================

module RenameTests =

    [<Fact>]
    let ``RenameEntity changes project name`` () =
        let store = createStore ()
        let project = addProject store "OldName"
        store.RenameEntity(project.Id, EntityKind.Project, "NewName")
        Assert.Equal("NewName", store.Projects.[project.Id].Name)

    [<Fact>]
    let ``RenameEntity with same name does not create extra undo step`` () =
        let store = createStore ()
        let project = addProject store "Same"
        store.RenameEntity(project.Id, EntityKind.Project, "Same")
        store.Undo() // should undo AddProject directly (rename no-op must not push)
        Assert.False(store.Projects.ContainsKey(project.Id))

    [<Fact>]
    let ``RenameEntity for Call changes only DevicesAlias`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api" ], true, None)
        let call = store.Calls |> Seq.head |> (fun kv -> kv.Value)
        Assert.Equal("Dev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)

        // UI는 전체 이름("NewDev.Api")을 전달 — RenameEntity가 alias만 추출
        store.RenameEntity(call.Id, EntityKind.Call, "NewDev.Api")
        Assert.Equal("NewDev", call.DevicesAlias)
        Assert.Equal("Api", call.ApiName)  // ApiName 불변
        Assert.Equal("NewDev.Api", call.Name)

// =============================================================================
// Arrows
// =============================================================================

module ArrowTests =

    [<Fact>]
    let ``RemoveArrows deletes arrows`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset) |> ignore
        let arrowId = store.ArrowWorks.Values |> Seq.head |> fun a -> a.Id
        let removed = store.RemoveArrows([ arrowId ])
        Assert.Equal(1, removed)
        Assert.Equal(0, store.ArrowWorks.Count)

    [<Fact>]
    let ``ConnectSelectionInOrder creates ArrowBetweenWorks with parentId = systemId`` () =
        let store = createStore ()
        let _, system, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset)
        Assert.Equal(1, count)
        Assert.Equal(1, store.ArrowWorks.Count)
        let arrow = store.ArrowWorks.Values |> Seq.head
        Assert.Equal(system.Id, arrow.ParentId)

    [<Fact>]
    let ``ConnectSelectionInOrder creates ArrowBetweenCalls with parentId = workId for allowed call arrow type`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf work.Id store |> List.map (fun c -> c.Id)
        let count = store.ConnectSelectionInOrder(callIds, ArrowType.Start)
        Assert.Equal(1, count)
        Assert.Equal(1, store.ArrowCalls.Count)
        let arrow = store.ArrowCalls.Values |> Seq.head
        Assert.Equal(work.Id, arrow.ParentId)
        Assert.Equal(ArrowType.Start, arrow.ArrowType)

    [<Fact>]
    let ``wouldCreateCallCycle detects simple cycle among calls`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "D.A1"; "D.A2"; "D.A3" ], true, None)
        let calls = Queries.callsOf work.Id store
        let c1, c2, c3 = calls.[0], calls.[1], calls.[2]
        // c1 → c2 → c3, then check if c3 → c1 would create cycle
        store.ConnectSelectionInOrder([ c1.Id; c2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ c2.Id; c3.Id ], ArrowType.Start) |> ignore
        Assert.True(ConnectionQueries.wouldCreateCallCycle store c3.Id c1.Id)
        // c1 → c3 직접 연결은 사이클 아님 (c3에서 c1로 가는 기존 경로 없음)
        Assert.False(ConnectionQueries.wouldCreateCallCycle store c1.Id c3.Id)

    [<Fact>]
    let ``wouldCreateCallCycle returns false for non-cycle connection`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "D.A1"; "D.A2" ], true, None)
        let calls = Queries.callsOf work.Id store
        Assert.False(ConnectionQueries.wouldCreateCallCycle store calls.[0].Id calls.[1].Id)

    [<Fact>]
    let ``ConnectSelectionInOrder blocks disallowed call arrow types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf work.Id store |> List.map (fun c -> c.Id)

        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.Reset))
        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.StartReset))
        Assert.Equal(0, store.ConnectSelectionInOrder(callIds, ArrowType.ResetReset))
        Assert.Equal(0, store.ArrowCalls.Count)

    [<Fact>]
    let ``UpdateArrowType blocks disallowed call arrow types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf work.Id store |> List.map (fun c -> c.Id)
        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore

        let arrowId = store.ArrowCalls.Values |> Seq.head |> fun a -> a.Id

        Assert.False(store.UpdateArrowType(arrowId, ArrowType.Reset))
        Assert.False(store.UpdateArrowType(arrowId, ArrowType.StartReset))
        Assert.False(store.UpdateArrowType(arrowId, ArrowType.ResetReset))
        Assert.Equal(ArrowType.Start, store.ArrowCalls.[arrowId].ArrowType)

    [<Fact>]
    let ``UpdateArrowTypesBatch updates all selected work arrows in single transaction`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        let work4 = addWork store "W4" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work3.Id; work4.Id ], ArrowType.Start) |> ignore
        let arrowIds = store.ArrowWorks.Values |> Seq.map (fun a -> a.Id) |> Seq.toList
        Assert.Equal(3, arrowIds.Length)

        let changed = store.UpdateArrowTypesBatch(arrowIds, ArrowType.StartReset)

        Assert.Equal(3, changed)
        for id in arrowIds do
            Assert.Equal(ArrowType.StartReset, store.ArrowWorks.[id].ArrowType)

        // 단일 트랜잭션 검증: Undo 1회로 3개 화살표 모두 원래 타입으로 복구
        store.Undo()
        for id in arrowIds do
            Assert.Equal(ArrowType.Start, store.ArrowWorks.[id].ArrowType)

    [<Fact>]
    let ``UpdateArrowTypesBatch skips arrows already at target type`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.StartReset) |> ignore
        let arrowIds = store.ArrowWorks.Values |> Seq.map (fun a -> a.Id) |> Seq.toList

        let changed = store.UpdateArrowTypesBatch(arrowIds, ArrowType.StartReset)

        // 1개는 이미 StartReset이므로 1개만 변경
        Assert.Equal(1, changed)

    [<Fact>]
    let ``toggleCallArrowType toggles between Start and Group`` () =
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.Start)
        Assert.Equal(ArrowType.Start, EntityKindRules.toggleCallArrowType ArrowType.Group)

    [<Fact>]
    let ``toggleCallArrowType normalizes disallowed inputs to Group`` () =
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.Reset)
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.StartReset)
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.ResetReset)
        Assert.Equal(ArrowType.Group, EntityKindRules.toggleCallArrowType ArrowType.Unspecified)

    [<Fact>]
    let ``UpdateArrowTypesBatch skips disallowed call arrow types`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "Dev.Api1"; "Dev.Api2"; "Dev.Api3" ], true, None)
        let callIds = Queries.callsOf work.Id store |> List.map (fun c -> c.Id)
        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore
        let arrowIds = store.ArrowCalls.Values |> Seq.map (fun a -> a.Id) |> Seq.toList
        Assert.True(arrowIds.Length >= 2)

        let changed = store.UpdateArrowTypesBatch(arrowIds, ArrowType.StartReset)

        Assert.Equal(0, changed)
        for id in arrowIds do
            Assert.Equal(ArrowType.Start, store.ArrowCalls.[id].ArrowType)

    [<Fact>]
    let ``ReverseArrowsBatch reverses all selected work arrows in single transaction`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        let work4 = addWork store "W4" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work3.Id; work4.Id ], ArrowType.Start) |> ignore
        let arrows = store.ArrowWorks.Values |> Seq.toList
        let original = arrows |> List.map (fun a -> a.Id, a.SourceId, a.TargetId)
        let arrowIds = arrows |> List.map (fun a -> a.Id)

        let changed = store.ReverseArrowsBatch(arrowIds)

        Assert.Equal(3, changed)
        for (id, oldSrc, oldTgt) in original do
            let a = store.ArrowWorks.[id]
            Assert.Equal(oldTgt, a.SourceId)
            Assert.Equal(oldSrc, a.TargetId)

        // 단일 트랜잭션 검증: Undo 1회로 3개 모두 원위치
        store.Undo()
        for (id, oldSrc, oldTgt) in original do
            let a = store.ArrowWorks.[id]
            Assert.Equal(oldSrc, a.SourceId)
            Assert.Equal(oldTgt, a.TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch skips arrow when reversed key conflicts with non-batch arrow`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work1.Id ], ArrowType.Start) |> ignore
        // batch에는 W1→W2 1개만 포함; W2→W1는 store에 별도 존재
        let forward =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work1.Id && a.TargetId = work2.Id)

        let changed = store.ReverseArrowsBatch([ forward.Id ])

        Assert.Equal(0, changed)
        // W1→W2 그대로 유지
        let after = store.ArrowWorks.[forward.Id]
        Assert.Equal(work1.Id, after.SourceId)
        Assert.Equal(work2.Id, after.TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch accepts symmetric pair both in batch`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work1.Id ], ArrowType.Start) |> ignore
        let forward =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work1.Id && a.TargetId = work2.Id)
        let backward =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work2.Id && a.TargetId = work1.Id)

        let changed = store.ReverseArrowsBatch([ forward.Id; backward.Id ])

        Assert.Equal(2, changed)
        // forward는 W2→W1, backward는 W1→W2 (서로 swap)
        Assert.Equal(work2.Id, store.ArrowWorks.[forward.Id].SourceId)
        Assert.Equal(work1.Id, store.ArrowWorks.[forward.Id].TargetId)
        Assert.Equal(work1.Id, store.ArrowWorks.[backward.Id].SourceId)
        Assert.Equal(work2.Id, store.ArrowWorks.[backward.Id].TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch handles mixed work and call arrows`` () =
        let store = createStore ()
        let project, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        let workArrowId = (store.ArrowWorks.Values |> Seq.head).Id

        store.AddCallsWithDevice(project.Id, work1.Id, [ "Dev.Api1"; "Dev.Api2" ], true, None)
        let callIds = Queries.callsOf work1.Id store |> List.map (fun c -> c.Id)
        store.ConnectSelectionInOrder(callIds, ArrowType.Start) |> ignore
        let callArrowId = (store.ArrowCalls.Values |> Seq.head).Id

        let workSrcBefore = store.ArrowWorks.[workArrowId].SourceId
        let workTgtBefore = store.ArrowWorks.[workArrowId].TargetId
        let callSrcBefore = store.ArrowCalls.[callArrowId].SourceId
        let callTgtBefore = store.ArrowCalls.[callArrowId].TargetId

        let changed = store.ReverseArrowsBatch([ workArrowId; callArrowId ])

        Assert.Equal(2, changed)
        Assert.Equal(workTgtBefore, store.ArrowWorks.[workArrowId].SourceId)
        Assert.Equal(workSrcBefore, store.ArrowWorks.[workArrowId].TargetId)
        Assert.Equal(callTgtBefore, store.ArrowCalls.[callArrowId].SourceId)
        Assert.Equal(callSrcBefore, store.ArrowCalls.[callArrowId].TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch dedupes duplicate ids`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        let arrowId = (store.ArrowWorks.Values |> Seq.head).Id

        let changed = store.ReverseArrowsBatch([ arrowId; arrowId; arrowId ])

        Assert.Equal(1, changed)
        // 1번만 반전 — 3번 반전되어 원위치하면 안됨
        Assert.Equal(work2.Id, store.ArrowWorks.[arrowId].SourceId)
        Assert.Equal(work1.Id, store.ArrowWorks.[arrowId].TargetId)

    [<Fact>]
    let ``ReverseArrowsBatch reverses non-conflicting arrows when some conflict`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        let work4 = addWork store "W4" flow.Id
        // 충돌 쌍: W1→W2 + W2→W1 (둘 다 존재, batch에는 W1→W2만 포함)
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work2.Id; work1.Id ], ArrowType.Start) |> ignore
        // 정상 화살표: W3→W4 (반전 충돌 없음)
        store.ConnectSelectionInOrder([ work3.Id; work4.Id ], ArrowType.Start) |> ignore

        let forward12 =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work1.Id && a.TargetId = work2.Id)
        let arrow34 =
            store.ArrowWorks.Values
            |> Seq.find (fun a -> a.SourceId = work3.Id && a.TargetId = work4.Id)

        let changed = store.ReverseArrowsBatch([ forward12.Id; arrow34.Id ])

        // W3→W4만 반전, W1→W2는 W2→W1와 충돌해서 skip
        Assert.Equal(1, changed)
        Assert.Equal(work1.Id, store.ArrowWorks.[forward12.Id].SourceId)
        Assert.Equal(work2.Id, store.ArrowWorks.[forward12.Id].TargetId)
        Assert.Equal(work4.Id, store.ArrowWorks.[arrow34.Id].SourceId)
        Assert.Equal(work3.Id, store.ArrowWorks.[arrow34.Id].TargetId)

    [<Fact>]
    let ``ConnectSelectionInOrder creates cross-flow ArrowBetweenWorks in same system`` () =
        let store = createStore ()
        let _, system, _, work1 = setupBasicHierarchy store
        let flow2 = addFlow store "Flow2" system.Id
        let work2 = addWork store "W2" flow2.Id
        let count = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.ResetReset)
        Assert.Equal(1, count)
        let arrow = store.ArrowWorks.Values |> Seq.head
        Assert.Equal(system.Id, arrow.ParentId)


    /// 한 쌍에 함께 둘 수 없는 같은 계열(같은 방향 Start · StartReset, ResetReset · Reset)은 나중에 그은 화살표가 대체하고,
    /// 나머지 종류는 함께 둔다. 같은 화살표는 새로 만들지 않는다. 예전엔 연결 메뉴가 Start 기준으로만 따져 Start 가 있는
    /// 쌍에는 어떤 종류도 그을 수 없었고, 규칙상으로는 Start 와 StartReset 이 같은 쌍에 겹쳐 쌓였다.
    [<Fact>]
    let ``ConnectSelectionInOrder replaces the same family on a pair and keeps other types`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let forward, backward = [ work1.Id; work2.Id ], [ work2.Id; work1.Id ]
        let connect ids arrowType = store.ConnectSelectionInOrder(ids, arrowType)
        let types () = store.ArrowWorks.Values |> Seq.map (fun a -> a.ArrowType) |> Seq.sort |> Seq.toList
        Assert.Equal(1, connect forward ArrowType.Start)
        Assert.Equal<ArrowType list>(
            [ ArrowType.Reset; ArrowType.StartReset; ArrowType.ResetReset; ArrowType.Group ],
            ConnectionQueries.connectableArrowTypesForSelection store forward)

        Assert.Equal(1, connect forward ArrowType.StartReset)
        Assert.Equal<ArrowType list>([ ArrowType.StartReset ], types ())
        store.Undo()
        Assert.Equal<ArrowType list>([ ArrowType.Start ], types ())

        Assert.Equal(1, connect forward ArrowType.Reset)
        Assert.Equal(1, connect forward ArrowType.ResetReset)
        Assert.Equal<ArrowType list>([ ArrowType.Start; ArrowType.ResetReset ], types ())
        Assert.Equal(0, connect backward ArrowType.ResetReset)
        Assert.Equal(1, connect backward ArrowType.Reset)
        Assert.Equal<ArrowType list>([ ArrowType.Start; ArrowType.Reset ], types ())

        // 방향 전환은 대체하지 않고 거절한다 — W2→W1 StartReset 을 뒤집으면 W1→W2 Start 와 함께 둘 수 없다.
        Assert.Equal(1, connect backward ArrowType.StartReset)
        let startReset = store.ArrowWorks.Values |> Seq.find (fun a -> a.ArrowType = ArrowType.StartReset)
        Assert.False(store.ReverseArrow(startReset.Id))

    /// 종류 바꾸기와 상호 리셋도 같은 규칙이다 — 바꾼 종류와 함께 둘 수 없는 화살표는 대체된다.
    /// 예전엔 ResetReset 끼리만 합쳐, 같은 쌍의 Reset 을 Start 로 바꾸면 Start 가 둘이 됐다.
    [<Fact>]
    let ``UpdateArrowType and mutual reset replace arrows that cannot share the pair`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id
        let pairTypes (a: Guid) (b: Guid) =
            store.ArrowWorks.Values
            |> Seq.filter (fun x -> (x.SourceId = a && x.TargetId = b) || (x.SourceId = b && x.TargetId = a))
            |> Seq.map (fun x -> x.ArrowType)
            |> Seq.sort
            |> Seq.toList
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Reset) |> ignore
        let reset = store.ArrowWorks.Values |> Seq.find (fun a -> a.ArrowType = ArrowType.Reset)

        Assert.True(store.UpdateArrowType(reset.Id, ArrowType.Start))
        Assert.Equal<ArrowType list>([ ArrowType.Start ], pairTypes work1.Id work2.Id)

        store.ConnectSelectionInOrder([ work2.Id; work3.Id ], ArrowType.Reset) |> ignore
        Assert.Equal(1, store.ConnectWorksWithMutualReset([ work2.Id; work3.Id ]))
        Assert.Equal<ArrowType list>([ ArrowType.ResetReset ], pairTypes work2.Id work3.Id)

    [<Fact>]
    let ``ConnectSelectionInOrder blocks same ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let count1 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start)
        Assert.Equal(1, count1)
        // 동일 소스/타겟/타입 → 중복 차단
        let count2 = store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start)
        Assert.Equal(0, count2)
        Assert.Equal(1, store.ArrowWorks.Count)

    [<Fact>]
    let ``ReconnectArrow allows different ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work1.Id; work3.Id ], ArrowType.Reset) |> ignore

        let arrowToReconnect =
            store.ArrowWorks.Values
            |> Seq.find (fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work3.Id && arrow.ArrowType = ArrowType.Reset)

        let changed = store.ReconnectArrow(arrowToReconnect.Id, false, work2.Id)

        Assert.True(changed)
        Assert.Equal(2, store.ArrowWorks.Count)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work2.Id && arrow.ArrowType = ArrowType.Start)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work2.Id && arrow.ArrowType = ArrowType.Reset)

    [<Fact>]
    let ``ReconnectArrow blocks same ArrowType between same nodes`` () =
        let store = createStore ()
        let _, _, flow, work1 = setupBasicHierarchy store
        let work2 = addWork store "W2" flow.Id
        let work3 = addWork store "W3" flow.Id

        store.ConnectSelectionInOrder([ work1.Id; work2.Id ], ArrowType.Start) |> ignore
        store.ConnectSelectionInOrder([ work1.Id; work3.Id ], ArrowType.Start) |> ignore

        let arrowToReconnect =
            store.ArrowWorks.Values
            |> Seq.find (fun arrow -> arrow.SourceId = work1.Id && arrow.TargetId = work3.Id && arrow.ArrowType = ArrowType.Start)

        let changed = store.ReconnectArrow(arrowToReconnect.Id, false, work2.Id)

        Assert.False(changed)
        Assert.Contains(store.ArrowWorks.Values, fun arrow -> arrow.Id = arrowToReconnect.Id && arrow.TargetId = work3.Id)


/// 편집 무결성 회귀 — Call 순환, Undo 스냅샷, ApiCall 편집·공유 삭제, 이력 위치.
module EditIntegrityRegressionTests =

    /// Work 하나에 Call c1→c2→c3 Start 체인. arrow s t = s→t 화살표.
    let private chainOfThreeCalls () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "D.A1"; "D.A2"; "D.A3" ], true, None)
        let calls = Queries.callsOf work.Id store
        let c1, c2, c3 = calls.[0], calls.[1], calls.[2]
        store.ConnectSelectionInOrder([ c1.Id; c2.Id; c3.Id ], ArrowType.Start) |> ignore
        let arrow (s: Call) (t: Call) =
            store.ArrowCalls.Values |> Seq.find (fun a -> a.SourceId = s.Id && a.TargetId = t.Id)
        store, c1, c2, c3, arrow

    /// 방향 전환도 새 연결과 같은 순환 금지 규칙을 따라야 한다. 예전엔 중복만 검사해 c1→c3 를 뒤집으면 c3→c1 이 생겨
    /// 순환이 됐고, 그 Work 를 클릭하면 critical path 재귀로 앱이 흔적 없이 죽었다.
    [<Fact>]
    let ``reversing a call arrow that would close a cycle is rejected`` () =
        let store, c1, _, c3, arrow = chainOfThreeCalls ()
        store.ConnectSelectionInOrder([ c1.Id; c3.Id ], ArrowType.Start) |> ignore
        let shortcut = arrow c1 c3

        Assert.True(ConnectionQueries.wouldReverseCreateCallCycle store shortcut.Id)
        Assert.False(store.ReverseArrow(shortcut.Id))
        Assert.Equal(c1.Id, store.ArrowCalls.[shortcut.Id].SourceId)
        Assert.Equal(c3.Id, store.ArrowCalls.[shortcut.Id].TargetId)

    /// 재연결로 끝점을 상류 Call 로 옮기는 것도 순환이다(c2→c3 의 끝을 c1 로 → c1→c2→c1).
    [<Fact>]
    let ``reconnecting a call arrow onto an upstream call is rejected`` () =
        let store, c1, c2, c3, arrow = chainOfThreeCalls ()
        let second = arrow c2 c3

        Assert.False(store.ReconnectArrow(second.Id, false, c1.Id))
        Assert.Equal(c3.Id, store.ArrowCalls.[second.Id].TargetId)

    /// 여러 화살표를 함께 뒤집을 때는 앞에서 받아들인 전환까지 반영해 하나씩 검사한다.
    [<Fact>]
    let ``batch reversal only accepts call reversals that keep the graph acyclic`` () =
        let store, c1, c2, c3, arrow = chainOfThreeCalls ()
        store.ConnectSelectionInOrder([ c1.Id; c3.Id ], ArrowType.Start) |> ignore
        let shortcut, second = arrow c1 c3, arrow c2 c3

        let changed = store.ReverseArrowsBatch([ shortcut.Id; second.Id ])

        // c1→c3 를 뒤집으면 c1→c2→c3→c1 순환이라 거절, c2→c3 를 뒤집는 것은 순환이 없다.
        Assert.Equal(1, changed)
        Assert.Equal(c1.Id, store.ArrowCalls.[shortcut.Id].SourceId)
        Assert.Equal(c3.Id, store.ArrowCalls.[second.Id].SourceId)
        Assert.Equal(c2.Id, store.ArrowCalls.[second.Id].TargetId)

    /// "선택 노드 연결" 은 앞쌍에서 더할 화살표까지 누적해 검사해야 한다. 기존 c3→c1 이 있을 때 c1,c2,c3 순서 연결은
    /// 쌍마다 따로 보면 둘 다 통과하지만 합치면 c1→c2→c3→c1 순환이다.
    [<Fact>]
    let ``ordered connect detects a cycle formed together with existing arrows`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "D.A1"; "D.A2"; "D.A3" ], true, None)
        let calls = Queries.callsOf work.Id store
        let c1, c2, c3 = calls.[0], calls.[1], calls.[2]
        store.ConnectSelectionInOrder([ c3.Id; c1.Id ], ArrowType.Start) |> ignore

        Assert.True(ConnectionQueries.hasCallCycleInSequence store [ c1.Id; c2.Id; c3.Id ])
        Assert.False(ConnectionQueries.hasCallCycleInSequence store [ c3.Id; c2.Id ])

    /// Undo/Redo 는 넣을 때마다 스냅샷 사본을 넣어야 한다. 예전엔 Redo 가 스냅샷 객체 자체를 넣어, 다음 편집이 그 객체를
    /// 제자리에서 바꿔 버렸다 — 아래 순서 끝에서 이력은 "A 로 변경" 만 적용된 상태인데 이름은 B 였다.
    [<Fact>]
    let ``redo after a later edit restores the redone value, not the later one`` () =
        let store = createStore ()
        let project = addProject store "Original"
        store.RenameEntity(project.Id, EntityKind.Project, "A")
        store.Undo()
        store.Redo()
        store.RenameEntity(project.Id, EntityKind.Project, "B")
        store.Undo()
        store.Undo()
        store.Redo()

        Assert.Equal("A", store.Projects.[project.Id].Name)

    /// 패널에서 ApiCall 을 고치면 고친 칸만 바뀌어야 한다. 예전엔 지웠다 새로 만들어 OriginFlowId · 태그 설명/데이터 타입이
    /// 사라지고 목록 끝으로 밀려 IO 주소 배정 순서까지 바뀌었다.
    [<Fact>]
    let ``panel edit of an ApiCall keeps its other fields and its position`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "D.A1" ], true, None)
        let call = Queries.callsOf work.Id store |> List.head
        let firstId = call.ApiCalls.[0].Id
        let apiDefId = store.ApiCalls.[firstId].ApiDefId.Value
        store.AddApiCallFromPanel(call.Id, apiDefId, "", "", "", "", 0, "", 0, "") |> ignore
        let first = store.ApiCalls.[firstId]
        let tag = IOTag("Out1", "%QX0.0", "솔레노이드")
        tag.DataType <- IOTagDataType.INT
        first.OutTag <- Some tag
        let originBefore = first.OriginFlowId
        Assert.True(originBefore.IsSome)

        Assert.True(store.UpdateApiCallFromPanel(call.Id, firstId, apiDefId, "Renamed", "Out2", "%QX0.1", "", "", 0, "", 0, ""))

        let apiCalls = store.Calls.[call.Id].ApiCalls
        Assert.Equal(2, apiCalls.Count)
        Assert.Equal(firstId, apiCalls.[0].Id)
        let edited = apiCalls.[0]
        Assert.Equal("Renamed", edited.Name)
        Assert.Equal("%QX0.1", edited.OutTag.Value.Address)
        Assert.Equal("솔레노이드", edited.OutTag.Value.Description)
        Assert.Equal(IOTagDataType.INT, edited.OutTag.Value.DataType)
        Assert.Equal(originBefore, edited.OriginFlowId)

    /// ApiDef 선택으로 만든 Call 은 기존 ApiCall 을 함께 쓴다. 한 Call 에서 지워도 다른 Call 의 것은 남아야 하고 Undo/Redo 를
    /// 거쳐도 사라지면 안 된다 — 예전엔 사전에서 지워져, 다음 Undo/Redo 의 재연결 때 다른 Call 에서도 빠졌다.
    [<Fact>]
    let ``removing a shared ApiCall from one call keeps it on the other call`` () =
        let store = createStore ()
        let project, _, _, work = setupBasicHierarchy store
        store.AddCallsWithDevice(project.Id, work.Id, [ "D.A1" ], true, None)
        let callA = Queries.callsOf work.Id store |> List.head
        let sharedId = callA.ApiCalls.[0].Id
        let callBId = store.AddCallWithLinkedApiDefs(work.Id, "E", "A1", [ store.ApiCalls.[sharedId].ApiDefId.Value ])
        Assert.Contains(store.Calls.[callBId].ApiCalls, fun ac -> ac.Id = sharedId)

        store.RemoveApiCallFromCall(callA.Id, sharedId)
        store.Undo()
        store.Redo()

        Assert.DoesNotContain(store.Calls.[callA.Id].ApiCalls, fun ac -> ac.Id = sharedId)
        Assert.Contains(store.Calls.[callBId].ApiCalls, fun ac -> ac.Id = sharedId)
        Assert.True(store.ApiCalls.ContainsKey sharedId)

    /// "저장 안 됨" 판정 기준 — 이력 위치는 되돌렸다 다시 실행하면 저장 시점과 같아지고, 되돌린 뒤 새로 편집하면 달라진다.
    /// undo 개수로 판정하던 때는 저장 후 되돌리기나, 개수만 같은 다른 상태를 "저장됨" 으로 봤다.
    [<Fact>]
    let ``history position identifies the saved state across undo, redo and new edits`` () =
        let store = createStore ()
        let project = addProject store "Original"
        store.RenameEntity(project.Id, EntityKind.Project, "Saved")
        let saved = store.HistoryPosition()

        store.Undo()
        Assert.False(obj.ReferenceEquals(saved, store.HistoryPosition()))
        store.Redo()
        Assert.True(obj.ReferenceEquals(saved, store.HistoryPosition()))
        store.Undo()
        store.RenameEntity(project.Id, EntityKind.Project, "Other")
        Assert.False(obj.ReferenceEquals(saved, store.HistoryPosition()))

    /// History 패널의 저장 지점 표시(#255)용 — 저장 위치가 패널 몇 번째 항목인지. 되돌려도 redo 쪽의 같은 항목을 가리키고,
    /// 되돌린 뒤 새로 편집하면(redo 가 지워져) 이력에서 사라진다. 빈 이력은 "(초기 상태)" 항목(0)이다.
    [<Fact>]
    let ``history panel index of the saved position follows undo and disappears after a new edit`` () =
        let store = createStore ()
        Assert.Equal(0, store.HistoryPanelIndexOf(store.HistoryPosition()))
        let project = addProject store "Original"
        store.RenameEntity(project.Id, EntityKind.Project, "A")
        store.RenameEntity(project.Id, EntityKind.Project, "B")
        let saved = store.HistoryPosition()
        let savedIndex = store.HistoryPanelIndexOf(saved)
        Assert.True(savedIndex >= 2)

        store.Undo()
        Assert.Equal(savedIndex, store.HistoryPanelIndexOf(saved))
        Assert.Equal(savedIndex - 1, store.HistoryPanelIndexOf(store.HistoryPosition()))
        store.RenameEntity(project.Id, EntityKind.Project, "C")
        Assert.Equal(-1, store.HistoryPanelIndexOf(saved))
