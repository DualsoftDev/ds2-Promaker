module Ds2.Editor.ConnectionQueries

open System
open Ds2.Core
open Ds2.Core.Store


let arrowKey (sourceId: Guid) (targetId: Guid) (arrowType: ArrowType) =
    struct (sourceId, targetId, arrowType)

let arrowKeyOf (arrow: #DsArrow) =
    arrowKey arrow.SourceId arrow.TargetId arrow.ArrowType

let hasArrowKeyExcept
    (expectedKey: struct(Guid * Guid * ArrowType))
    (exceptId: Guid option)
    (arrows: seq<#DsArrow>) =
    arrows
    |> Seq.exists (fun arrow ->
        exceptId <> Some arrow.Id &&
        arrowKeyOf arrow = expectedKey)

// ─── Call 화살표 순환 금지 ───────────────────────────────────────────────────
// Call 그래프는 모든 화살표 종류를 통틀어 순환이 없어야 한다. 순환이 생기면 critical path 계산(Queries.tryGetDeviceDurationMs)
// 같은 그래프 순회가 끝나지 않는다. 새 연결뿐 아니라 방향 전환·재연결·일괄 전환·순서 연결도 모두 이 규칙을 거친다 —
// 예전엔 새 연결만 검사해서, 방향 전환 한 번으로 순환이 생겨 Work 클릭 시 StackOverflow 로 앱이 죽었다.

/// 간선 목록 edges 에 sourceId→targetId 를 더하면 순환이 생기는지(targetId 에서 sourceId 로 갈 수 있으면 순환).
let private edgesCreateCycle (edges: (Guid * Guid) list) (sourceId: Guid) (targetId: Guid) : bool =
    let visited = System.Collections.Generic.HashSet<Guid>()
    let rec reachable (current: Guid) =
        if current = sourceId then true
        elif not (visited.Add current) then false
        else edges |> List.exists (fun (s, t) -> s = current && reachable t)
    reachable targetId

let private callEdgesExcept (store: DsStore) (excluded: Set<Guid>) : (Guid * Guid) list =
    Queries.allArrowCalls store
    |> List.filter (fun a -> not (excluded.Contains a.Id))
    |> List.map (fun a -> a.SourceId, a.TargetId)

/// 새 화살표를 추가하면 Call 그래프에 사이클이 생기는지 검사
let wouldCreateCallCycle (store: DsStore) (sourceId: Guid) (targetId: Guid) : bool =
    edgesCreateCycle (callEdgesExcept store Set.empty) sourceId targetId

/// 기존 Call 화살표 하나의 끝점을 newSourceId→newTargetId 로 바꾸면 사이클이 생기는지 (방향 전환·재연결)
let wouldRewireCreateCallCycle (store: DsStore) (arrowId: Guid) (newSourceId: Guid) (newTargetId: Guid) : bool =
    edgesCreateCycle (callEdgesExcept store (Set.singleton arrowId)) newSourceId newTargetId

/// Call 화살표의 한쪽 끝을 newEndpointId 로 옮기면(재연결) 사이클이 생기는지. Call 화살표가 아니면 false.
let wouldReconnectCreateCallCycle (store: DsStore) (arrowId: Guid) (replaceSource: bool) (newEndpointId: Guid) : bool =
    match Queries.getArrowCall arrowId store with
    | Some arrow ->
        let newSource, newTarget =
            if replaceSource then newEndpointId, arrow.TargetId else arrow.SourceId, newEndpointId
        wouldRewireCreateCallCycle store arrowId newSource newTarget
    | None -> false

/// Call 화살표의 방향을 뒤집으면 사이클이 생기는지. Call 화살표가 아니면 false.
let wouldReverseCreateCallCycle (store: DsStore) (arrowId: Guid) : bool =
    match Queries.getArrowCall arrowId store with
    | Some arrow -> wouldRewireCreateCallCycle store arrowId arrow.TargetId arrow.SourceId
    | None -> false

/// 여러 Call 화살표를 함께 뒤집을 때 받아들일 수 있는 것만 고른다 — 앞에서 받아들인 전환을 반영한 그래프로 하나씩 검사한다.
let acceptCallReversalsWithoutCycle (store: DsStore) (arrowIds: Guid list) : Guid list =
    let candidates = arrowIds |> List.choose (fun id -> Queries.getArrowCall id store)
    let reversed = System.Collections.Generic.HashSet<Guid>()
    let currentEdges () =
        Queries.allArrowCalls store
        |> List.map (fun a -> if reversed.Contains a.Id then a.Id, (a.TargetId, a.SourceId) else a.Id, (a.SourceId, a.TargetId))
    [ for arrow in candidates do
        let others = currentEdges () |> List.filter (fun (id, _) -> id <> arrow.Id) |> List.map snd
        if not (edgesCreateCycle others arrow.TargetId arrow.SourceId) then
            reversed.Add arrow.Id |> ignore
            yield arrow.Id ]

/// 연속된 ID 쌍을 차례로 연결하면 Call 간 순환이 생기는지 — 앞쌍에서 더할 간선을 누적해서 검사한다.
/// (쌍마다 따로 보면 A,B,C 순서 연결이 기존 C→A 와 함께 만드는 순환을 놓친다.)
let hasCallCycleInSequence (store: DsStore) (ids: Guid seq) : bool =
    let pairs =
        ids
        |> Seq.pairwise
        |> Seq.filter (fun (a, b) -> (Queries.getCall a store).IsSome && (Queries.getCall b store).IsSome)
        |> List.ofSeq
    let rec go edges remaining =
        match remaining with
        | [] -> false
        | (a, b) :: rest -> edgesCreateCycle edges a b || go ((a, b) :: edges) rest
    go (callEdgesExcept store Set.empty) pairs

/// Ordered multi-selection -> connectable arrow links.
/// Result tuple: (entityKind, parentId, sourceId, targetId)
///   Work arrow: parentId = systemId
///   Call arrow: parentId = workId
let orderedArrowLinksForSelection
    (store: DsStore)
    (orderedNodeIds: seq<Guid>)
    (arrowType: ArrowType)
    : (EntityKind * Guid * Guid * Guid) list =

    let resolveOrderedNodeContext (nodeId: Guid) : (EntityKind * Guid * Guid) option =
        match Queries.getWork nodeId store with
        | Some work ->
            Queries.trySystemIdOfWork work.Id store
            |> Option.map (fun systemId -> (EntityKind.Work, systemId, work.Id))
        | None ->
            match Queries.getCall nodeId store with
            | Some call -> Some (EntityKind.Call, call.ParentId, call.Id)
            | None -> None

    let distinctIds =
        let seen = System.Collections.Generic.HashSet<Guid>()
        orderedNodeIds
        |> Seq.filter seen.Add
        |> Seq.toList

    let orderedContexts =
        distinctIds
        |> List.choose resolveOrderedNodeContext

    // 기존 화살표를 (Source, Target, ArrowType)으로 프리빌드 — 동일 타입만 중복 제외
    let existingWorkArrows =
        Queries.allArrowWorks store
        |> List.map arrowKeyOf
        |> System.Collections.Generic.HashSet

    let existingCallArrows =
        Queries.allArrowCalls store
        |> List.map arrowKeyOf
        |> System.Collections.Generic.HashSet

    orderedContexts
    |> List.pairwise
    |> List.choose (fun ((sourceType, sourceParent, sourceId), (targetType, targetParent, targetId)) ->
        if sourceType <> targetType
            || sourceId = targetId
            || not (EntityKindRules.isArrowTypeAllowedForKind sourceType arrowType) then
            None
        else
            match sourceType with
            | EntityKind.Work when sourceParent = targetParent ->
                // 같은 System에 속한 Work끼리만 연결, 동일 소스+타겟+타입만 중복
                if existingWorkArrows.Contains(arrowKey sourceId targetId arrowType) then None
                else Some (sourceType, sourceParent, sourceId, targetId)
            | EntityKind.Call when sourceParent = targetParent ->
                // 같은 Work에 속한 Call끼리만 연결, 동일 소스+타겟+타입만 중복
                if existingCallArrows.Contains(arrowKey sourceId targetId arrowType) then None
                else Some (sourceType, sourceParent, sourceId, targetId)
            | _ -> None)
