namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

[<Extension>]
type DsStoreNodeEditExtensions =

    // ─── Move ────────────────────────────────────────────────────────
    [<Extension>]
    static member MoveEntities(store: DsStore, requests: seq<MoveEntityRequest>) : int =
        let moves =
            requests
            |> Seq.distinctBy (fun r -> r.Id)
            |> Seq.choose (fun r ->
                let newPos = Some r.Position
                match Queries.getWork r.Id store with
                | Some work when work.Position <> newPos -> Some(r.Id, true, newPos)
                | Some _ -> None
                | None ->
                    match Queries.getCall r.Id store with
                    | Some call when call.Position <> newPos -> Some(r.Id, false, newPos)
                    | _ -> None)
            |> Seq.toList
        if moves.IsEmpty then 0
        else
            StoreLog.debug($"count={moves.Length}")
            store.WithTransaction("Move Selected Nodes", fun () ->
                for (id, isWork, newPos) in moves do
                    if isWork then
                        store.TrackMutate(store.Works, id, fun work -> work.Position <- newPos)
                    else
                        store.TrackMutate(store.Calls, id, fun call -> call.Position <- newPos))
            // 트리/캔버스 visual tree 재구축을 피하기 위해 가벼운 이벤트 발행.
            // C# 측에서 인접 화살표 path만 재계산한다.
            let movedIds = moves |> List.map (fun (id, _, _) -> id)
            // 같은 트랜잭션을 undo/redo 할 때도 동일한 가벼운 경로를 타도록 힌트 부착.
            // 미부착 시 applyTransaction 이 StoreRefreshed 를 emit 해 RebuildAll → 50+ 노드 ContentPresenter 재생성.
            store.SetLastUndoLightEvent(EntitiesMoved movedIds)
            store.EmitEntitiesMovedAndHistory(movedIds)
            moves.Length

    // ─── Remove ──────────────────────────────────────────────────────
    [<Extension>]
    static member RemoveEntities(store: DsStore, selections: seq<EntityKind * Guid>) =
        let selList = selections |> Seq.distinctBy snd |> Seq.toList
        if not selList.IsEmpty then
            let types = selList |> List.map (fun (k, _) -> k.ToString()) |> List.distinct |> String.concat ","
            StoreLog.debug($"count={selList.Length}, types=[{types}]")
            store.WithTransaction("Delete Entities", fun () ->
                CascadeRemove.batchRemoveEntities store selList)
            store.EmitRefreshAndHistory()

    // ─── Rename ──────────────────────────────────────────────────────
    [<Extension>]
    static member RenameEntity(store: DsStore, id: Guid, entityKind: EntityKind, newName: string) =
        if entityKind = EntityKind.Call then
            Queries.requireNonReferenceCall id store
        // Call은 DevicesAlias만 변경, Work는 LocalName만 변경
        let resolvedName =
            match entityKind with
            | EntityKind.Call ->
                match newName.IndexOf('.') with
                | -1  -> newName
                | idx -> newName[..idx - 1]
            | EntityKind.Work ->
                // "FlowPrefix.LocalName" 형식이 올 수 있으므로 LocalName 부분만 추출
                match newName.IndexOf('.') with
                | -1  -> newName
                | idx -> newName[idx + 1..]
            | _ -> newName
        let oldName, isChanged =
            match entityKind with
            | EntityKind.Call ->
                match store.Calls.TryGetValue(id) with
                | true, c  -> Some c.DevicesAlias, c.DevicesAlias <> resolvedName
                | false, _ -> None, false
            | EntityKind.Work ->
                match store.Works.TryGetValue(id) with
                | true, w  -> Some w.LocalName, w.LocalName <> resolvedName
                | false, _ -> None, false
            | _ ->
                let n = Queries.tryGetName store entityKind id
                n, n |> Option.map (fun o -> o <> resolvedName) |> Option.defaultValue false
        match oldName with
        | Some _ when isChanged ->
            StoreLog.debug($"id={id}, kind={entityKind}, newName={resolvedName}")
            // Work: Flow 내 LocalName 중복 검사
            match entityKind with
            | EntityKind.Work ->
                let work = store.Works.[id]
                if not (Queries.isLocalNameUniqueInFlow work.ParentId resolvedName (Some id) store) then
                    invalidOp $"같은 Flow 내에 이미 '{resolvedName}' Work가 존재합니다."
            | EntityKind.Flow ->
                let flow = store.Flows.[id]
                if not (Queries.isFlowNameUniqueInSystem flow.ParentId resolvedName (Some id) store) then
                    invalidOp $"같은 System 내에 이미 '{resolvedName}' Flow가 존재합니다."
            | EntityKind.Call ->
                let call = store.Calls.[id]
                let fullName = $"{resolvedName}.{call.ApiName}"
                if not (Queries.isCallNameUniqueInWork call.ParentId fullName (Some id) store) then
                    invalidOp $"같은 Work 내에 이미 '{fullName}' Call이 존재합니다."
            | _ -> ()
            store.WithTransaction($"이름 변경 → \"{resolvedName}\"", fun () ->
                match entityKind with
                | EntityKind.Project   -> store.TrackMutate(store.Projects, id, fun e -> e.Name <- resolvedName)
                | EntityKind.System    -> store.TrackMutate(store.Systems, id, fun e -> e.Name <- resolvedName)
                | EntityKind.Flow      ->
                    store.TrackMutate(store.Flows, id, fun e -> e.Name <- resolvedName)
                    // Cascade: 자식 Work들의 FlowPrefix 갱신
                    for work in Queries.worksOf id store do
                        store.TrackMutate(store.Works, work.Id, fun w -> w.FlowPrefix <- resolvedName)
                | EntityKind.Work      ->
                    store.TrackMutate(store.Works, id, fun e -> e.LocalName <- resolvedName)
                | EntityKind.Call      -> store.TrackMutate(store.Calls, id, fun e -> e.DevicesAlias <- resolvedName)
                | EntityKind.ApiDef    -> store.TrackMutate(store.ApiDefs, id, fun e -> e.Name <- resolvedName)
                | _                    -> failwithf "Unknown entity kind: %A" entityKind)
            // Call/Work은 표시명 조합이 다름
            let displayName =
                match entityKind with
                | EntityKind.Call -> store.Calls.[id].Name
                | EntityKind.Work -> store.Works.[id].Name
                | _ -> resolvedName
            // treeName: 트리에서 표시할 이름 (Work만 LocalName, 나머지는 displayName과 동일)
            let treeName =
                match entityKind with
                | EntityKind.Work -> resolvedName
                | _ -> displayName
            store.EmitAndHistory(EntityRenamed(id, displayName, treeName))
        | Some _ -> () // 이름 변경 없음
        | None ->
            StoreLog.warn($"Entity not found. kind={entityKind}, id={id}")
            invalidOp $"Entity not found. kind={entityKind}, id={id}"

    /// 여러 Flow 의 IsDisabled 를 한 transaction(=1 undo step)으로 토글한다. 실제로 바뀐 개수를 반환.
    /// disabled=true → 비활성화(숨김), false → 복원. 이미 같은 상태인 Flow 는 건너뛴다.
    [<Extension>]
    static member SetFlowsDisabled(store: DsStore, flowIds: seq<Guid>, disabled: bool) : int =
        let targets =
            flowIds
            |> Seq.distinct
            |> Seq.choose (fun id -> Queries.getFlow id store)
            |> Seq.filter (fun f -> f.IsDisabled <> disabled)
            |> Seq.toList
        if targets.IsEmpty then 0
        else
            let verb = if disabled then "비활성화" else "복원"
            store.WithTransaction($"Flow {targets.Length}개 {verb}", fun () ->
                for f in targets do
                    store.TrackMutate(store.Flows, f.Id, fun x -> x.IsDisabled <- disabled))
            store.EmitRefreshAndHistory()
            targets.Length

    /// 여러 Work 를 target Flow 로 이동한다(한 transaction = 1 undo step). 이동한 개수를 반환.
    /// 이름 중복(target Flow 내 동일 LocalName)·no-op(이미 그 Flow)은 제외한다.
    /// 이동 시 그 Work 가 끝점인 Work 화살표는 제거한다(Call 이동과 동일 정책 — cross-flow 연결 끊김).
    [<Extension>]
    static member MoveWorksToFlow(store: DsStore, workIds: seq<Guid>, targetFlowId: Guid) : int =
        match Queries.getFlow targetFlowId store with
        | None -> 0
        | Some targetFlow ->
            let movables =
                workIds
                |> Seq.distinct
                |> Seq.choose (fun id -> Queries.getWork id store)
                |> Seq.filter (fun w ->
                    w.ParentId <> targetFlowId
                    && Queries.isLocalNameUniqueInFlow targetFlowId w.LocalName (Some w.Id) store)
                |> Seq.toList
            if movables.IsEmpty then 0
            else
                store.WithTransaction($"Move {movables.Length} Work(s) to Flow", fun () ->
                    for w in movables do
                        let arrowIds =
                            store.ArrowWorksReadOnly.Values
                            |> Seq.filter (fun a -> a.SourceId = w.Id || a.TargetId = w.Id)
                            |> Seq.map (fun a -> a.Id)
                            |> Seq.toList
                        for aid in arrowIds do
                            CascadeRemove.removeWorkArrow store aid
                        store.TrackMutate(store.Works, w.Id, fun x ->
                            x.ParentId <- targetFlowId
                            x.FlowPrefix <- targetFlow.Name))
                store.EmitRefreshAndHistory()
                movables.Length

    /// 여러 Flow 를 다른 System 으로 통째로 이동한다(한 transaction = 1 undo step). 이동한 개수를 반환.
    /// Work/Call/ApiCall 은 id 그대로 딸려가고(재생성 아님 — 복사와 다름), 화살표는:
    /// - Flow 내부 Work 끼리의 화살표 → 대상 System 으로 재부모화 (ArrowBetweenWorks.ParentId = systemId 불변식)
    /// - 소스 System 의 타 Flow Work 와 걸친 화살표 → 제거 (Work 이동과 동일 정책 — cross-flow 연결 끊김)
    /// 대상 System 에 같은 이름의 Flow 가 있으면 유니크 이름으로 자동 개명하고 Work.FlowPrefix 를 cascade.
    [<Extension>]
    static member MoveFlowsToSystem(store: DsStore, flowIds: seq<Guid>, targetSystemId: Guid) : int =
        match Queries.getSystem targetSystemId store with
        | None -> 0
        | Some _ ->
            let movables =
                flowIds
                |> Seq.distinct
                |> Seq.choose (fun id -> Queries.getFlow id store)
                |> Seq.filter (fun f -> f.ParentId <> targetSystemId)
                |> Seq.toList
            if movables.IsEmpty then 0
            else
                let movedFlowIds = movables |> Seq.map (fun f -> f.Id) |> Set.ofSeq
                let movedWorkIds =
                    movables |> Seq.collect (fun f -> Queries.worksOf f.Id store)
                    |> Seq.map (fun w -> w.Id) |> Set.ofSeq
                store.WithTransaction($"Move {movables.Length} Flow(s) to System", fun () ->
                    for flow in movables do
                        let workIds =
                            Queries.worksOf flow.Id store |> List.map (fun w -> w.Id) |> Set.ofList
                        let sourceArrows = Queries.arrowWorksOf flow.ParentId store
                        for arrow in sourceArrows do
                            let srcIn = workIds.Contains arrow.SourceId
                            let tgtIn = workIds.Contains arrow.TargetId
                            if srcIn && tgtIn then
                                store.TrackMutate(store.ArrowWorks, arrow.Id, fun a ->
                                    a.ParentId <- targetSystemId)
                            elif srcIn || tgtIn then
                                CascadeRemove.removeWorkArrow store arrow.Id
                        let existingNames =
                            Queries.flowsOf targetSystemId store |> List.map (fun f -> f.Name)
                        // oldName 캡처 필수 — TrackMutate 는 같은 인스턴스를 뮤테이트하므로
                        // 뮤테이트 후 flow.Name 과 비교하면 개명 여부를 판별할 수 없다.
                        let oldName = flow.Name
                        let newName = Queries.nextUniqueName oldName existingNames
                        store.TrackMutate(store.Flows, flow.Id, fun f ->
                            f.ParentId <- targetSystemId
                            f.Name <- newName)
                        if newName <> oldName then
                            for wid in workIds do
                                store.TrackMutate(store.Works, wid, fun w -> w.FlowPrefix <- newName))
                store.EmitRefreshAndHistory()
                movables.Length

    // ─── AutoLayout ────────────────────────────────────────────────────

    /// 노드들이 모두 같은 좌표에 몰려있으면 자동 배치 적용 (Mermaid 임포트, 탭 최초 오픈 등).
    /// 사용자 동작이 아닌 초기화 과정이므로 Undo 스택에 "Move Selected Nodes" 같은 항목을
    /// 남기지 않는다 — 위치만 직접 변경하고 가벼운 EntitiesMoved 이벤트를 발행한다.
    /// (이전에는 StoreRefreshed 였는데, 새 탭 오픈 시마다 큐잉되는 RebuildAll 이 ~2s hitch 의 주범이었음.
    ///  변한 건 노드 좌표뿐이고 OpenTab 직후 RefreshCanvasForActiveTab 가 새 탭을 채우므로 heavy 갱신 불필요.)
    [<Extension>]
    static member AutoLayoutIfNeeded(store: DsStore, kind: TabKind, rootId: Guid) : bool =
        let content = EditorCanvasProjection.canvasContentForTab store kind rootId
        if CanvasLayout.needsAutoLayout content then
            let mutable applied = 0
            let movedIds = ResizeArray<Guid>()
            for r in CanvasLayout.computeLayout content do
                let newPos = Some r.Position
                match Queries.getWork r.Id store with
                | Some work when work.Position <> newPos ->
                    work.Position <- newPos
                    applied <- applied + 1
                    movedIds.Add(r.Id)
                | Some _ -> ()
                | None ->
                    match Queries.getCall r.Id store with
                    | Some call when call.Position <> newPos ->
                        call.Position <- newPos
                        applied <- applied + 1
                        movedIds.Add(r.Id)
                    | _ -> ()
            if applied > 0 then
                store.EmitEvent(EntitiesMoved (List.ofSeq movedIds))
            applied > 0
        else false
