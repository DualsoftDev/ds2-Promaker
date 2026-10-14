namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store


// =============================================================================
// 내부 헬퍼 — 화살표 재연결 해석
// =============================================================================

module internal DirectArrowOps =
    let private resolveEndpoints (replaceSource: bool) (srcId: Guid) (tgtId: Guid) (newEndpointId: Guid) =
        let keepId = if replaceSource then tgtId else srcId
        let newSourceId = if replaceSource then newEndpointId else keepId
        let newTargetId = if replaceSource then keepId else newEndpointId
        if newSourceId = newTargetId || (newSourceId = srcId && newTargetId = tgtId) then None
        else Some(keepId, newSourceId, newTargetId)

    let tryReconnectArrow (store: DsStore) (arrowId: Guid) (replaceSource: bool) (newEndpointId: Guid) : bool =
        let tryWork (arrow: ArrowBetweenWorks) =
            match resolveEndpoints replaceSource arrow.SourceId arrow.TargetId newEndpointId with
            | None -> false
            | Some(keepId, newSourceId, newTargetId) ->
                // ArrowBetweenWorks.parentId = systemId — 새 endpoint도 같은 System에 속해야 함
                match Queries.trySystemIdOfWork newEndpointId store, Queries.trySystemIdOfWork keepId store with
                | Some newSysId, Some keepSysId
                    when newSysId = arrow.ParentId && keepSysId = arrow.ParentId ->
                    let hasDuplicate =
                        Queries.arrowWorksOf arrow.ParentId store
                        |> ConnectionQueries.hasConflict ((=) arrow.Id) newSourceId newTargetId arrow.ArrowType
                    if hasDuplicate then false
                    else
                        store.TrackMutate(store.ArrowWorks, arrowId, fun a ->
                            a.SourceId <- newSourceId
                            a.TargetId <- newTargetId)
                        true
                | _ -> false

        let tryCall (arrow: ArrowBetweenCalls) =
            match resolveEndpoints replaceSource arrow.SourceId arrow.TargetId newEndpointId with
            | None -> false
            | Some(keepId, newSourceId, newTargetId) ->
                // ArrowBetweenCalls.parentId = workId — 새 endpoint도 같은 Work에 속해야 함
                match Queries.getCall newEndpointId store, Queries.getCall keepId store with
                | Some newCall, Some keepCall
                    when newCall.ParentId = arrow.ParentId && keepCall.ParentId = arrow.ParentId ->
                    let hasDuplicate =
                        Queries.arrowCallsOf arrow.ParentId store
                        |> ConnectionQueries.hasConflict ((=) arrow.Id) newSourceId newTargetId arrow.ArrowType
                    if hasDuplicate then false
                    elif ConnectionQueries.wouldRewireCreateCallCycle store arrowId newSourceId newTargetId then
                        StoreLog.warn($"Reconnect rejected: Call cycle. arrowId={arrowId}")
                        false
                    else
                        store.TrackMutate(store.ArrowCalls, arrowId, fun a ->
                            a.SourceId <- newSourceId
                            a.TargetId <- newTargetId)
                        true
                | _ -> false

        match Queries.getArrowWork arrowId store, Queries.getArrowCall arrowId store with
        | Some arrow, _ -> tryWork arrow
        | None, Some arrow -> tryCall arrow
        | _ -> false

    let orderedWorkChainLinks (store: DsStore) (orderedWorkIds: seq<Guid>) =
        orderedWorkIds
        |> Seq.distinct
        |> Seq.pairwise
        |> Seq.choose (fun (sourceId, targetId) ->
            match Queries.trySystemIdOfWork sourceId store, Queries.trySystemIdOfWork targetId store with
            | Some sourceSystemId, Some targetSystemId when sourceSystemId = targetSystemId ->
                Some(sourceSystemId, sourceId, targetId)
            | _ -> None)
        |> Seq.toList

// =============================================================================
// DsStore 화살표 확장 — 삭제/재연결/순서 연결
// =============================================================================

[<Extension>]
type DsStoreArrowsExtensions =

    [<Extension>]
    static member TrackSyncOrderedWorkChain(store: DsStore, orderedWorkIds: seq<Guid>, arrowType: ArrowType) : int =
        let orderedIds = orderedWorkIds |> Seq.distinct |> Seq.toList
        let workIdSet = orderedIds |> Set.ofList
        let arrowIdsToRemove =
            orderedIds
            |> Seq.choose (fun workId -> Queries.trySystemIdOfWork workId store |> Option.map (fun systemId -> systemId, workId))
            |> Seq.groupBy fst
            |> Seq.collect (fun (systemId, pairs) ->
                let systemWorkIds = pairs |> Seq.map snd |> Set.ofSeq
                Queries.arrowWorksOf systemId store
                |> Seq.filter (fun arrow -> Set.contains arrow.SourceId systemWorkIds && Set.contains arrow.TargetId systemWorkIds)
                |> Seq.map _.Id)
            |> Seq.distinct
            |> Seq.toList
        let links = DirectArrowOps.orderedWorkChainLinks store orderedIds
        let retainedGroupPairs =
            if arrowType = ArrowType.Group then links |> Seq.map (fun (_, a, b) -> a, b) |> Set.ofSeq
            else Set.empty
        for arrowId in arrowIdsToRemove do
            store.TrackRemove(store.ArrowWorks, arrowId)
        for (systemId, sourceId, targetId) in links do
            if Set.contains sourceId workIdSet && Set.contains targetId workIdSet then
                store.TrackAdd(store.ArrowWorks, ArrowBetweenWorks(systemId, sourceId, targetId, arrowType))
        links.Length

    [<Extension>]
    static member RemoveArrows(store: DsStore, arrowIds: seq<Guid>) : int =
        let toRemove =
            arrowIds
            |> Seq.distinct
            |> Seq.choose (fun arrowId ->
                match Queries.getArrowWork arrowId store with
                | Some _ -> Some(arrowId, true)
                | None ->
                    match Queries.getArrowCall arrowId store with
                    | Some _ -> Some(arrowId, false)
                    | None -> None)
            |> Seq.toList
        if toRemove.IsEmpty then 0
        else
            StoreLog.debug($"count={toRemove.Length}")
            store.WithTransaction("Delete Arrows", fun () ->
                for (arrowId, isWork) in toRemove do
                    if isWork then CascadeRemove.removeWorkArrow store arrowId
                    else CascadeRemove.removeCallArrow store arrowId)
            store.EmitConnectionsChangedAndHistory()
            toRemove.Length

    [<Extension>]
    static member ReconnectArrow(store: DsStore, arrowId: Guid, replaceSource: bool, newEndpointId: Guid) : bool =
        StoreLog.debug($"arrowId={arrowId}, replaceSource={replaceSource}, newEndpointId={newEndpointId}")
        let mutable success = false
        store.WithTransaction("화살표 재연결", fun () ->
            success <- DirectArrowOps.tryReconnectArrow store arrowId replaceSource newEndpointId)
        if success then store.EmitConnectionsChangedAndHistory()
        else StoreLog.warn($"Reconnect failed. arrowId={arrowId}")
        success

    [<Extension>]
    static member UpdateArrowType(store: DsStore, arrowId: Guid, newArrowType: ArrowType) : bool =
        StoreLog.debug($"arrowId={arrowId}, newArrowType={newArrowType}")
        match Queries.getArrowWork arrowId store with
        | Some arrow when arrow.ArrowType <> newArrowType
                          && EntityKindRules.isArrowTypeAllowedForKind EntityKind.Work newArrowType ->
            // 바꾼 종류와 한 쌍에 함께 둘 수 없는 화살표는 이 화살표가 대체한다 (Start → StartReset 이면 Start 삭제 등).
            let replacedIds =
                Queries.arrowWorksOf arrow.ParentId store
                |> ConnectionQueries.arrowsReplacedBy ((=) arrow.Id) arrow.SourceId arrow.TargetId newArrowType
            store.WithTransaction("화살표 타입 변경", fun () ->
                for id in replacedIds do
                    store.TrackRemove(store.ArrowWorks, id)
                store.TrackMutate(store.ArrowWorks, arrowId, fun a -> a.ArrowType <- newArrowType))
            if not replacedIds.IsEmpty then
                StoreLog.debug($"replaced {replacedIds.Length} arrow(s) on the same pair")
            store.EmitConnectionsChangedAndHistory()
            true
        | Some _ -> false
        | None ->
            match Queries.getArrowCall arrowId store with
            | Some arrow when arrow.ArrowType <> newArrowType
                              && EntityKindRules.isArrowTypeAllowedForKind EntityKind.Call newArrowType ->
                let replacedIds =
                    Queries.arrowCallsOf arrow.ParentId store
                    |> ConnectionQueries.arrowsReplacedBy ((=) arrow.Id) arrow.SourceId arrow.TargetId newArrowType
                store.WithTransaction("화살표 타입 변경", fun () ->
                    for id in replacedIds do
                        store.TrackRemove(store.ArrowCalls, id)
                    store.TrackMutate(store.ArrowCalls, arrowId, fun a -> a.ArrowType <- newArrowType))
                if not replacedIds.IsEmpty then
                    StoreLog.debug($"replaced {replacedIds.Length} arrow(s) on the same pair")
                store.EmitConnectionsChangedAndHistory()
                true
            | _ -> false

    [<Extension>]
    static member UpdateArrowTypesBatch(store: DsStore, arrowIds: seq<Guid>, newArrowType: ArrowType) : int =
        let workChanges = ResizeArray<Guid>()
        let callChanges = ResizeArray<Guid>()
        for arrowId in arrowIds do
            match Queries.getArrowWork arrowId store with
            | Some arrow when arrow.ArrowType <> newArrowType
                              && EntityKindRules.isArrowTypeAllowedForKind EntityKind.Work newArrowType ->
                workChanges.Add(arrowId)
            | Some _ -> ()
            | None ->
                match Queries.getArrowCall arrowId store with
                | Some arrow when arrow.ArrowType <> newArrowType
                                  && EntityKindRules.isArrowTypeAllowedForKind EntityKind.Call newArrowType ->
                    callChanges.Add(arrowId)
                | _ -> ()
        let total = workChanges.Count + callChanges.Count
        if total > 0 then
            StoreLog.debug($"UpdateArrowTypesBatch: {total} items -> {newArrowType}")
            // 바꾼 화살표마다 한 쌍에 함께 둘 수 없는 화살표를 지운다(바뀐 화살표끼리 겹치면 Id 가 작은 쪽을 남긴다).
            let removed = System.Collections.Generic.HashSet<Guid>()
            let absorb (arrowsOfParent: Guid -> seq<#DsArrow>) (getArrow: Guid -> #DsArrow) (remove: Guid -> unit) (changedIds: seq<Guid>) =
                for id in changedIds |> Seq.distinct |> Seq.sort do
                    if not (removed.Contains id) then
                        let kept = getArrow id
                        let replacedIds =
                            arrowsOfParent kept.ParentId
                            |> ConnectionQueries.arrowsReplacedBy
                                (fun otherId -> otherId = id || removed.Contains otherId)
                                kept.SourceId kept.TargetId kept.ArrowType
                        for replacedId in replacedIds do
                            removed.Add replacedId |> ignore
                            remove replacedId
            store.WithTransaction("화살표 타입 일괄 변경", fun () ->
                for id in workChanges do
                    store.TrackMutate(store.ArrowWorks, id, fun a -> a.ArrowType <- newArrowType)
                for id in callChanges do
                    store.TrackMutate(store.ArrowCalls, id, fun a -> a.ArrowType <- newArrowType)
                absorb (fun parentId -> Queries.arrowWorksOf parentId store) (fun id -> store.ArrowWorks.[id])
                       (fun id -> store.TrackRemove(store.ArrowWorks, id)) workChanges
                absorb (fun parentId -> Queries.arrowCallsOf parentId store) (fun id -> store.ArrowCalls.[id])
                       (fun id -> store.TrackRemove(store.ArrowCalls, id)) callChanges)
            store.EmitConnectionsChangedAndHistory()
        total

    [<Extension>]
    static member ReverseArrow(store: DsStore, arrowId: Guid) : bool =
        StoreLog.debug($"arrowId={arrowId}")
        match Queries.getArrowWork arrowId store with
        | Some arrow ->
            let hasDuplicate =
                Queries.arrowWorksOf arrow.ParentId store
                |> ConnectionQueries.hasConflict ((=) arrow.Id) arrow.TargetId arrow.SourceId arrow.ArrowType
            if hasDuplicate then false
            else
                store.WithTransaction("화살표 방향 변경", fun () ->
                    store.TrackMutate(store.ArrowWorks, arrowId, fun a ->
                        let src = a.SourceId
                        a.SourceId <- a.TargetId
                        a.TargetId <- src))
                store.EmitConnectionsChangedAndHistory()
                true
        | None ->
            match Queries.getArrowCall arrowId store with
            | Some arrow ->
                let hasDuplicate =
                    Queries.arrowCallsOf arrow.ParentId store
                    |> ConnectionQueries.hasConflict ((=) arrow.Id) arrow.TargetId arrow.SourceId arrow.ArrowType
                if hasDuplicate then false
                elif ConnectionQueries.wouldReverseCreateCallCycle store arrowId then
                    StoreLog.warn($"Reverse rejected: Call cycle. arrowId={arrowId}")
                    false
                else
                    store.WithTransaction("화살표 방향 변경", fun () ->
                        store.TrackMutate(store.ArrowCalls, arrowId, fun a ->
                            let src = a.SourceId
                            a.SourceId <- a.TargetId
                            a.TargetId <- src))
                    store.EmitConnectionsChangedAndHistory()
                    true
            | None -> false

    [<Extension>]
    static member ReverseArrowsBatch(store: DsStore, arrowIds: seq<Guid>) : int =
        let distinctIds = arrowIds |> Seq.distinct |> Seq.toList
        let batchSet = System.Collections.Generic.HashSet<Guid>(distinctIds)

        let workAccepted = ResizeArray<Guid>()
        let callAccepted = ResizeArray<Guid>()

        for arrowId in distinctIds do
            match Queries.getArrowWork arrowId store with
            | Some arrow ->
                let conflict =
                    Queries.arrowWorksOf arrow.ParentId store
                    |> ConnectionQueries.hasConflict batchSet.Contains arrow.TargetId arrow.SourceId arrow.ArrowType
                if not conflict then workAccepted.Add(arrowId)
            | None ->
                match Queries.getArrowCall arrowId store with
                | Some arrow ->
                    let conflict =
                        Queries.arrowCallsOf arrow.ParentId store
                        |> ConnectionQueries.hasConflict batchSet.Contains arrow.TargetId arrow.SourceId arrow.ArrowType
                    if not conflict then callAccepted.Add(arrowId)
                | None -> ()

        // Call 화살표는 함께 뒤집어도 순환이 생기지 않는 것만 받아들인다.
        let callAccepted =
            ConnectionQueries.acceptCallReversalsWithoutCycle store (List.ofSeq callAccepted)
            |> ResizeArray
        let total = workAccepted.Count + callAccepted.Count
        if total > 0 then
            StoreLog.debug($"ReverseArrowsBatch: {total} items")
            store.WithTransaction("화살표 방향 일괄 전환", fun () ->
                for id in workAccepted do
                    store.TrackMutate(store.ArrowWorks, id, fun a ->
                        let src = a.SourceId
                        a.SourceId <- a.TargetId
                        a.TargetId <- src)
                for id in callAccepted do
                    store.TrackMutate(store.ArrowCalls, id, fun a ->
                        let src = a.SourceId
                        a.SourceId <- a.TargetId
                        a.TargetId <- src))
            store.EmitConnectionsChangedAndHistory()
        total

    [<Extension>]
    static member ConnectSelectionInOrder(store: DsStore, orderedNodeIds: seq<Guid>, arrowType: ArrowType) : int =
        let links = ConnectionQueries.orderedArrowLinksForSelection store orderedNodeIds arrowType
        if links.IsEmpty then 0
        else
            StoreLog.debug($"count={links.Length}, arrowType={arrowType}")
            store.WithTransaction("Connect Selected Nodes In Order", fun () ->
                // 한 쌍에 함께 둘 수 없는 기존 화살표는 새 화살표가 대체한다 (Start → StartReset, Reset → ResetReset 등).
                for (entityKind, parentId, sourceId, targetId) in links do
                    match entityKind with
                    | EntityKind.Work ->
                        for id in Queries.arrowWorksOf parentId store
                                  |> ConnectionQueries.arrowsReplacedBy (fun _ -> false) sourceId targetId arrowType do
                            store.TrackRemove(store.ArrowWorks, id)
                        let arrow = ArrowBetweenWorks(parentId, sourceId, targetId, arrowType)
                        store.TrackAdd(store.ArrowWorks, arrow)
                    | EntityKind.Call ->
                        for id in Queries.arrowCallsOf parentId store
                                  |> ConnectionQueries.arrowsReplacedBy (fun _ -> false) sourceId targetId arrowType do
                            store.TrackRemove(store.ArrowCalls, id)
                        let arrow = ArrowBetweenCalls(parentId, sourceId, targetId, arrowType)
                        store.TrackAdd(store.ArrowCalls, arrow)
                    | _ -> ())
            store.EmitConnectionsChangedAndHistory()
            links.Length

    /// 지정된 Work 순서를 기준으로 같은 집합 내부 화살표를 제거하고 새 선형 체인으로 재구성.
    /// 순서 기반 편집기에서 Flow 내 Work 인과 관계를 트리 순서에 맞춰 동기화할 때 사용.
    [<Extension>]
    static member SyncOrderedWorkChain(store: DsStore, orderedWorkIds: seq<Guid>, arrowType: ArrowType) : int =
        let orderedIds = orderedWorkIds |> Seq.distinct |> Seq.toList
        if orderedIds.IsEmpty then 0
        else
            let mutable linkCount = 0
            store.WithTransaction("순서 기반 Work 체인 동기화", fun () ->
                linkCount <- DsStoreArrowsExtensions.TrackSyncOrderedWorkChain(store, orderedIds, arrowType))
            store.EmitConnectionsChangedAndHistory()
            linkCount

    /// Undo/History 없이 순서 기반 Work 체인만 직접 동기화.
    [<Extension>]
    static member SyncOrderedWorkChainDirect(store: DsStore, orderedWorkIds: seq<Guid>, arrowType: ArrowType) : int =
        let orderedIds = orderedWorkIds |> Seq.distinct |> Seq.toList
        let arrowIdsToRemove =
            orderedIds
            |> Seq.choose (fun workId -> Queries.trySystemIdOfWork workId store |> Option.map (fun systemId -> systemId, workId))
            |> Seq.groupBy fst
            |> Seq.collect (fun (systemId, pairs) ->
                let systemWorkIds = pairs |> Seq.map snd |> Set.ofSeq
                Queries.arrowWorksOf systemId store
                |> Seq.filter (fun arrow -> Set.contains arrow.SourceId systemWorkIds && Set.contains arrow.TargetId systemWorkIds)
                |> Seq.map _.Id)
            |> Seq.distinct
            |> Seq.toList
        let links = DirectArrowOps.orderedWorkChainLinks store orderedIds
        for arrowId in arrowIdsToRemove do
            store.ArrowWorks.Remove(arrowId) |> ignore
        for (systemId, sourceId, targetId) in links do
            let arrow = ArrowBetweenWorks(systemId, sourceId, targetId, arrowType)
            store.ArrowWorks[arrow.Id] <- arrow
        links.Length

    /// 지정된 Work ID 목록에서 같은 System에 속한 Work들끼리 ResetReset 화살표를 전체 쌍으로 연결.
    /// 쌍 (A, B)에 같은 화살표(A→B 또는 B→A ResetReset)가 이미 있으면 스킵 → 중복 방지. 그 쌍의 Reset 은 ResetReset 이 대체한다.
    /// N개 Work → 최대 N*(N-1)/2 화살표 (단일 트랜잭션)
    [<Extension>]
    static member ConnectWorksWithMutualReset(store: DsStore, workIds: seq<Guid>) : int =
        let bySystem =
            workIds
            |> Seq.distinct
            |> Seq.choose (fun id ->
                Queries.trySystemIdOfWork id store
                |> Option.map (fun sysId -> sysId, id))
            |> Seq.groupBy fst
            |> Seq.map (fun (sysId, pairs) -> sysId, pairs |> Seq.map snd |> Seq.toList)
            |> Seq.filter (fun (_, works) -> List.length works >= 2)
            |> Seq.toList
        if bySystem.IsEmpty then 0
        else
            let links =
                bySystem
                |> List.collect (fun (sysId, works) ->
                    let systemArrows = Queries.arrowWorksOf sysId store |> Seq.toList
                    [ for i in 0 .. works.Length - 2 do
                        for j in i + 1 .. works.Length - 1 do
                            let exists =
                                systemArrows
                                |> ConnectionQueries.hasSameConnection (fun _ -> false) works.[i] works.[j] ArrowType.ResetReset
                            if not exists then
                                yield (sysId, works.[i], works.[j]) ])
            if links.IsEmpty then 0
            else
                StoreLog.debug($"상호리셋 arrows={links.Length}")
                store.WithTransaction("상호리셋 연결", fun () ->
                    for (sysId, srcId, tgtId) in links do
                        for id in Queries.arrowWorksOf sysId store
                                  |> ConnectionQueries.arrowsReplacedBy (fun _ -> false) srcId tgtId ArrowType.ResetReset do
                            store.TrackRemove(store.ArrowWorks, id)
                        store.TrackAdd(store.ArrowWorks, ArrowBetweenWorks(sysId, srcId, tgtId, ArrowType.ResetReset)))
                store.EmitRefreshAndHistory()
                links.Length
