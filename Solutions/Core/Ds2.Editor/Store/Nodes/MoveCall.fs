namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

/// Flow 간 Call 이동의 사전 검증 결과.
/// Movable 외의 케이스는 모두 차단 사유 — UI 는 사유를 사용자에게 보여주고 작업을 취소.
type CrossFlowMoveValidation =
    /// (resolvedIds, targetFlowId, sourceFlowIds) — Call 이동은 Call id, Work 이동은 Work id.
    | Movable of (Guid list * Guid * Guid list)
    /// 이동하려는 Call/Work 가 Reference 묶음(Reference 이거나 Reference 를 거느린 원본)에 속한다. 이동은 원본을 옮기고
    /// 원래 자리의 묶음을 지우는 방식이라, 허용하면 Reference 가 아니라 원본이 옮겨지고 나머지 Reference 가 사라진다.
    | ReferencesInvolved of Guid list
    /// callIds 중 일부가 store 에서 resolve 안 됨 (이미 삭제된 ID 등).
    | NotResolvable of Guid list
    /// 모든 source Call 의 ParentId == targetWorkId — 의미상 no-op.
    | SameWorkMove
    /// (sourceCallId, conflictingName) — target work 에 이미 존재하는 이름과 충돌.
    | DuplicateNamesInTarget of (Guid * string) list
    /// (devAlias, conflict) — RenameSourceSystem 모드의 device 충돌.
    | RenameModeConflicts of (string * RenameDeviceConflict) list
    /// targetWorkId 가 store 에 없거나 그 Flow 를 못 찾음.
    | InvalidTarget

/// Move 실행 결과.
type CrossFlowMoveResult =
    /// 정상 이동 — 새로 생성된 Call ID 리스트 (target work 안의).
    | Moved of newCallIds: Guid list
    /// Validation 실패 — 사유 포함.
    | Blocked of CrossFlowMoveValidation

[<Extension>]
type DsStoreCallMoveExtensions =

    static member private TryGetMoveCandidate(store: DsStore, callId: Guid, targetWorkId: Guid) =
        match Queries.getCall callId store, Queries.getWork targetWorkId store with
        | Some call, Some targetWork ->
            match Queries.getWork call.ParentId store with
            | Some sourceWork ->
                Some(call, sourceWork, targetWork)
            | None -> None
        | _ -> None

    [<Extension>]
    static member CanMoveCallToWork(store: DsStore, callId: Guid, targetWorkId: Guid) : bool =
        match DsStoreCallMoveExtensions.TryGetMoveCandidate(store, callId, targetWorkId) with
        | Some(call, sourceWork, targetWork) ->
            sourceWork.Id <> targetWork.Id
            && sourceWork.ParentId = targetWork.ParentId
            && Queries.isCallNameUniqueInWork targetWork.Id call.Name (Some call.Id) store
        | None -> false

    /// 단일 call 의 attached arrow 제거 + ParentId 변경. *반드시* WithTransaction 안에서만 호출.
    static member private MoveOneCallInTransaction(store: DsStore, call: Call, targetWork: Work) =
        let attachedArrowIds =
            store.ArrowCallsReadOnly.Values
            |> Seq.filter (fun arrow -> arrow.SourceId = call.Id || arrow.TargetId = call.Id)
            |> Seq.map (fun arrow -> arrow.Id)
            |> Seq.toList
        for arrowId in attachedArrowIds do
            CascadeRemove.removeCallArrow store arrowId
        store.TrackMutate(store.Calls, call.Id, fun current ->
            current.ParentId <- targetWork.Id)

    /// 여러 call 을 같은 flow 의 target work 로 *한 transaction(=1 undo step)* 으로 이동.
    /// 이동 가능한 것만 옮기고 옮긴 개수를 반환. movable 판정은 단일 MoveCallToWork 와 동일하되,
    /// 이번 batch 안에서 이미 옮기기로 한 이름과의 중복까지 누적 차단(루프 호출 시 동작과 동일).
    [<Extension>]
    static member MoveCallsToWork(store: DsStore, callIds: seq<Guid>, targetWorkId: Guid) : int =
        let movables =
            callIds
            |> Seq.distinct
            |> Seq.choose (fun id -> DsStoreCallMoveExtensions.TryGetMoveCandidate(store, id, targetWorkId))
            |> Seq.fold
                (fun (acc, takenNames: Set<string>) (call, sourceWork, targetWork) ->
                    if sourceWork.Id <> targetWork.Id
                       && sourceWork.ParentId = targetWork.ParentId
                       && Queries.isCallNameUniqueInWork targetWork.Id call.Name (Some call.Id) store
                       && not (takenNames.Contains call.Name) then
                        (call, targetWork) :: acc, takenNames.Add call.Name
                    else
                        acc, takenNames)
                ([], Set.empty)
            |> fst
            |> List.rev
        if movables.IsEmpty then 0
        else
            store.WithTransaction("Move Call(s) To Work", fun () ->
                for (call, targetWork) in movables do
                    DsStoreCallMoveExtensions.MoveOneCallInTransaction(store, call, targetWork))
            store.EmitRefreshAndHistory()
            movables.Length

    [<Extension>]
    static member MoveCallToWork(store: DsStore, callId: Guid, targetWorkId: Guid) : bool =
        DsStoreCallMoveExtensions.MoveCallsToWork(store, Seq.singleton callId, targetWorkId) > 0

    /// RenameSourceSystem 모드 가능 여부를 사전 검사. (devAlias * conflict) list 반환.
    /// 빈 리스트면 Rename 모드 안전. UI Dialog 가 라디오 disable 여부 판단용으로 호출.
    [<Extension>]
    /// RenameSourceSystem 모드의 device 충돌 사전 검사 — 대상 Flow 기준. Call 이동과 Work 이동이 같은 검사를 쓴다
    /// (예전엔 대상 Work 를 받아, 대상 Work 가 없는 Work 이동은 검사를 건너뛰었다).
    static member GetRenameSourceSystemConflicts
        (store: DsStore, callIds: seq<Guid>, targetFlowId: Guid)
        : (string * RenameDeviceConflict) list =
        let resolved =
            callIds
            |> Seq.map (fun id -> Queries.resolveOriginalCallId id store)
            |> Seq.distinct
            |> Seq.choose (fun id -> Queries.getCall id store)
            |> Seq.toList
        if resolved.IsEmpty then []
        else
            match Queries.getFlow targetFlowId store with
            | None -> []
            | Some targetFlow ->
                let projectIdOpt =
                    Queries.getSystem targetFlow.ParentId store
                    |> Option.bind (fun s -> StoreHierarchyQueries.findProjectOfSystem store s.Id)
                match projectIdOpt with
                | None -> []
                | Some pid ->
                    PasteDeviceOps.collectRenameConflicts store (resolved |> List.map (fun c -> c.Id)) targetFlow.Name pid

    [<Extension>]
    static member CanMoveCallsAcrossFlow
        (store: DsStore, callIds: seq<Guid>, targetWorkId: Guid, mode: CrossFlowDeviceMode)
        : CrossFlowMoveValidation =
        let requested =
            callIds
            |> Seq.map (fun id -> Queries.resolveOriginalCallId id store)
            |> Seq.distinct
            |> Seq.toList
        let resolved = requested |> List.choose (fun id -> Queries.getCall id store)
        let resolvedIds = resolved |> List.map (fun c -> c.Id) |> Set.ofList
        let missing = requested |> List.filter (fun id -> not (resolvedIds.Contains id))
        let referenceInvolved =
            callIds
            |> Seq.distinct
            |> Seq.filter (fun id -> (Queries.getCall id store).IsSome && (Queries.callReferenceGroupOf id store).Length > 1)
            |> Seq.toList
        if not missing.IsEmpty then NotResolvable missing
        elif not referenceInvolved.IsEmpty then ReferencesInvolved referenceInvolved
        else
            match Queries.getWork targetWorkId store with
            | None -> InvalidTarget
            | Some targetWork ->
                let targetFlowId = targetWork.ParentId
                let sourceFlowIds =
                    resolved
                    |> List.choose (fun c -> Queries.getWork c.ParentId store |> Option.map (fun w -> w.ParentId))
                    |> List.distinct
                let allSameWork = resolved |> List.forall (fun c -> c.ParentId = targetWorkId)
                if allSameWork then SameWorkMove
                else
                    // Duplicate name check (target work 내 기존 Call 이름 + 우리가 옮길 Call 들끼리)
                    let existingNames =
                        Queries.originalCallsOf targetWorkId store
                        |> List.filter (fun c -> not (resolvedIds.Contains c.Id))
                        |> List.map (fun c -> c.Name)
                        |> Set.ofList
                    let dupes =
                        resolved
                        |> List.filter (fun c -> c.ParentId <> targetWorkId && existingNames.Contains c.Name)
                        |> List.map (fun c -> c.Id, c.Name)
                    if not dupes.IsEmpty then DuplicateNamesInTarget dupes
                    else
                        // Mode-specific guard
                        match mode with
                        | CrossFlowDeviceMode.RenameSourceSystem ->
                            match Queries.getFlow targetFlowId store with
                            | None -> InvalidTarget
                            | Some targetFlow ->
                                let projectIdOpt =
                                    Queries.getSystem targetFlow.ParentId store
                                    |> Option.bind (fun s -> StoreHierarchyQueries.findProjectOfSystem store s.Id)
                                match projectIdOpt with
                                | None -> InvalidTarget
                                | Some pid ->
                                    let conflicts =
                                        PasteDeviceOps.collectRenameConflicts store (resolved |> List.map (fun c -> c.Id)) targetFlow.Name pid
                                    if not conflicts.IsEmpty then RenameModeConflicts conflicts
                                    else Movable(resolved |> List.map (fun c -> c.Id), targetFlowId, sourceFlowIds)
                        | _ ->
                            Movable(resolved |> List.map (fun c -> c.Id), targetFlowId, sourceFlowIds)

    /// store 전체의 Call/Work Condition 안에서 *oldApiCall.Id -> newApiCall* 매핑에 따라
    /// ResizeArray<ApiCall> 안의 객체 reference 를 새 ApiCall 로 교체. 자기 자신 (movables 의 Condition)
    /// 은 어차피 cascade-remove 되므로 *target 외 모든 Call/Work* 만 walk 해도 안전하지만,
    /// 단순화 위해 *cascade-remove 이전에* 전체 store walk 한다 (movables 의 Condition 안 reference 가
    /// 새 Call 의 Condition 복사물에서도 동일 ApiCall 객체를 가리키는 경우 함께 갱신).
    static member private RewireConditionApiCallReferences
        (store: DsStore, apiCallMap: Map<Guid, ApiCall>) =
        if apiCallMap.IsEmpty then ()
        else
            // 바꿀 대상을 먼저 찾고, 실제 교체는 TrackMutate 안에서 한다 — 예전엔 교체한 뒤에 TrackMutate 를 불러 스냅샷이
            // 이미 바뀐 상태라 Undo 가 원래 참조로 되돌리지 못했다. 조건 leaf 는 본체 ApiCall 과 같은 Id 의 독립 사본으로 둔다
            // (DsStore.RebuildApiCallsDictionary 규약) — 본체 객체를 그대로 넣으면 leaf 스펙 편집이 본체까지 바꾼다.
            let rec needsRewire (conditions: ResizeArray<Condition>) =
                conditions
                |> Seq.exists (fun c ->
                    c.ApiCalls |> Seq.exists (fun ac -> apiCallMap.ContainsKey ac.Id)
                    || needsRewire c.Children)
            let rec rewire (conditions: ResizeArray<Condition>) =
                for c in conditions do
                    for i in 0 .. c.ApiCalls.Count - 1 do
                        let ac = c.ApiCalls.[i]
                        match Map.tryFind ac.Id apiCallMap with
                        | Some newAc ->
                            let leaf = newAc.DeepCopy()
                            leaf.Id <- newAc.Id
                            leaf.ContactKind <- ac.ContactKind
                            leaf.InputSpec <- ac.InputSpec
                            c.ApiCalls.[i] <- leaf
                        | None -> ()
                    rewire c.Children
            let touchedCalls =
                store.CallsReadOnly
                |> Seq.filter (fun kvp -> needsRewire kvp.Value.Conditions)
                |> Seq.map (fun kvp -> kvp.Key)
                |> Seq.toList
            for id in touchedCalls do
                store.TrackMutate(store.Calls, id, fun call -> rewire call.Conditions)
            let touchedWorks =
                store.WorksReadOnly
                |> Seq.filter (fun kvp -> needsRewire kvp.Value.Conditions)
                |> Seq.map (fun kvp -> kvp.Key)
                |> Seq.toList
            for id in touchedWorks do
                store.TrackMutate(store.Works, id, fun work -> rewire work.Conditions)

    [<Extension>]
    static member MoveCallsAcrossFlow
        (store: DsStore, callIds: seq<Guid>, targetWorkId: Guid, mode: CrossFlowDeviceMode)
        : CrossFlowMoveResult =
        match store.CanMoveCallsAcrossFlow(callIds, targetWorkId, mode) with
        | Movable(resolvedIds, _targetFlowId, _sourceFlowIds) ->
            // SameWork 케이스는 자연 제외 (이미 같은 work).
            let sourceCalls = resolvedIds |> List.choose (fun id -> Queries.getCall id store)
            let sameFlowSameWorkSplit =
                sourceCalls
                |> List.partition (fun c -> c.ParentId = targetWorkId)
            let _alreadyInTargetWork, movables = sameFlowSameWorkSplit
            if movables.IsEmpty then Moved []
            else
                // 원본 ApiCall.Id 들을 미리 캡처 — paste 후 새 Call 의 ApiCalls 와 인덱스 zip 으로 매핑 구축.
                let oldApiCallSnapshots =
                    movables
                    |> List.map (fun sc ->
                        sc.Id, sc.ApiCalls |> Seq.map (fun ac -> ac.Id) |> Seq.toList)
                    |> Map.ofList

                let mutable pastedIds = []
                store.WithTransaction($"Move {movables.Length} Call(s) across Flow ({mode})", fun () ->
                    pastedIds <-
                        DirectPasteOps.pasteCallsToWorkBatchWithMode store movables targetWorkId 0 mode

                    // 새 Call 들이 paste 된 순서로 movables 와 1:1 매칭됨 (pasteCallsToWorkBatchWithMode 가
                    // sortByPositionAndName 으로 정렬 후 paste — movables 도 같은 정렬을 받아야 일치)
                    let sortedMovables =
                        movables
                        |> List.mapi (fun idx call -> idx, call)
                        |> List.sortBy (fun (idx, call) ->
                            match call.Position with
                            | Some pos -> 0, pos.Y, pos.X, call.Name, idx
                            | None -> 1, 0, 0, call.Name, idx)
                        |> List.map snd
                    // oldApiCallId -> newApiCall 매핑 구축 (모든 mode 에 대해 동일 — Clone/Rename/Keep 모두
                    // 새 ApiCall 객체를 만들고 원본 ApiCall.Id 는 cascade-remove 됨).
                    let apiCallMap =
                        List.zip sortedMovables pastedIds
                        |> List.collect (fun (oldCall, newCallId) ->
                            match Queries.getCall newCallId store with
                            | Some newCall ->
                                let oldAcIds = Map.find oldCall.Id oldApiCallSnapshots
                                let newAcs = newCall.ApiCalls |> Seq.toList
                                if oldAcIds.Length = newAcs.Length then
                                    List.zip oldAcIds newAcs
                                else []
                            | None -> [])
                        |> Map.ofList

                    // Cascade-remove 전에 다른 Call/Work Condition 의 ApiCall reference 자동 rewire
                    DsStoreCallMoveExtensions.RewireConditionApiCallReferences(store, apiCallMap)

                    // 원본 Call 들 cascade-remove. 역인덱스는 **paste 뒤에** 만든다 — paste 가
                    // 만든 엔티티가 원본을 ReferenceOf 로 가리킬 수 있어 그 전에 스냅샷하면
                    // 역참조가 누락돼 dangling reference 가 남는다.
                    let removeIndex = CascadeRemove.buildIndex store
                    for sc in movables do
                        CascadeRemove.cascadeRemoveCall store removeIndex sc.Id
                    CascadeRemove.removeOrphanApiCalls store)
                if not pastedIds.IsEmpty then store.EmitRefreshAndHistory()
                Moved pastedIds
        | other -> Blocked other

    /// Work 를 다른 Flow 로 cross-flow 이동. Work cross-flow = 내부 Call 전부 cross-flow 이므로
    /// Call 이동(MoveCallsAcrossFlow)과 동일하게 device mode 를 적용한다: target Flow 에 paste
    /// (pasteWorksToFlowBatch 가 Work+Call+arrow+device 처리) 후 원본 Work cascade-remove.
    /// 반환 = target Flow 에 새로 생성된 Work id 리스트. 이미 그 Flow 인 Work 는 제외.
    /// Work cross-flow 이동 가능 여부. Call 이동과 같은 규칙 — Reference 묶음에 속한 Work/Call 은 막고,
    /// RenameSourceSystem 모드는 대상 Flow 기준 device 충돌을 검사한다. Movable 의 id = 실제로 옮길 Work(이미 그 Flow 인 것 제외).
    [<Extension>]
    static member CanMoveWorksAcrossFlow
        (store: DsStore, workIds: seq<Guid>, targetFlowId: Guid, mode: CrossFlowDeviceMode)
        : CrossFlowMoveValidation =
        match Queries.getFlow targetFlowId store with
        | None -> InvalidTarget
        | Some targetFlow ->
            let requested = workIds |> Seq.distinct |> Seq.toList
            let missing = requested |> List.filter (fun id -> not (store.Works.ContainsKey id))
            if not missing.IsEmpty then NotResolvable missing
            else
                let movables =
                    requested
                    |> List.choose (fun id -> Queries.getWork id store)
                    |> List.filter (fun w -> w.ParentId <> targetFlowId)
                let movableCalls = movables |> List.collect (fun w -> Queries.callsOf w.Id store)
                let referenceInvolved =
                    [ for w in movables do
                        if (Queries.referenceGroupOf w.Id store).Length > 1 then yield w.Id
                      for c in movableCalls do
                        if (Queries.callReferenceGroupOf c.Id store).Length > 1 then yield c.Id ]
                if not referenceInvolved.IsEmpty then ReferencesInvolved referenceInvolved
                else
                    let sourceFlowIds = movables |> List.map (fun w -> w.ParentId) |> List.distinct
                    let movable = Movable(movables |> List.map (fun w -> w.Id), targetFlowId, sourceFlowIds)
                    match mode with
                    | CrossFlowDeviceMode.RenameSourceSystem when not movableCalls.IsEmpty ->
                        let conflicts =
                            DsStoreCallMoveExtensions.GetRenameSourceSystemConflicts(
                                store, movableCalls |> List.map (fun c -> c.Id), targetFlow.Id)
                        if not conflicts.IsEmpty then RenameModeConflicts conflicts else movable
                    | _ -> movable

    [<Extension>]
    static member MoveWorksAcrossFlow
        (store: DsStore, workIds: seq<Guid>, targetFlowId: Guid, mode: CrossFlowDeviceMode)
        : Guid list =
        match DsStoreCallMoveExtensions.CanMoveWorksAcrossFlow(store, workIds, targetFlowId, mode) with
        | Movable(movableIds, _, _) when not movableIds.IsEmpty ->
            let movables = movableIds |> List.choose (fun id -> Queries.getWork id store)
            if movables.IsEmpty then []
            else
                let mutable pastedIds = []
                store.WithTransaction($"Move {movables.Length} Work(s) across Flow ({mode})", fun () ->
                    pastedIds <- DirectPasteOps.pasteWorksToFlowBatch store movables targetFlowId 0 mode
                    // 역인덱스는 paste 뒤에 — 이유는 MoveCallsAcrossFlow 쪽 주석 참조.
                    let removeIndex = CascadeRemove.buildIndex store
                    for w in movables do
                        CascadeRemove.cascadeRemoveWork store removeIndex w.Id
                    CascadeRemove.removeOrphanApiCalls store)
                if not pastedIds.IsEmpty then store.EmitRefreshAndHistory()
                pastedIds
        | _ -> []
