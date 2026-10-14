module Ds2.Editor.ConnectionQueries

open System
open Ds2.Core
open Ds2.Core.Store


// ─── 같은 연결 판정 ─────────────────────────────────────────────────────────────
// 한 노드 쌍에는 같은 연결을 하나만 둔다. 다른 종류는 같은 쌍에 더 그을 수 있다(Start 가 있어도 Reset 은 된다).
// 새 연결·재연결·방향 전환·상호 리셋이 모두 이 판정을 쓴다.

/// A(sourceA→targetA, typeA) 와 B 가 같은 연결인지.
///   - 같은 방향 같은 종류
///   - 같은 방향 Start · StartReset — 둘 다 source 가 끝나면 target 을 시작시킨다
///   - 방향만 다른 ResetReset — 양방향 리셋이라 A→B 와 B→A 가 같은 화살표다
let isSameConnection
    (sourceA: Guid) (targetA: Guid) (typeA: ArrowType)
    (sourceB: Guid) (targetB: Guid) (typeB: ArrowType) : bool =
    let startsTarget t = t = ArrowType.Start || t = ArrowType.StartReset
    let sameDirection = sourceA = sourceB && targetA = targetB
    let reversed = sourceA = targetB && targetA = sourceB
    (sameDirection && (typeA = typeB || (startsTarget typeA && startsTarget typeB)))
    || (reversed && typeA = ArrowType.ResetReset && typeB = ArrowType.ResetReset)

/// arrows 중 isExcluded 가 아닌 화살표에 sourceId→targetId(arrowType) 와 같은 연결이 있는지.
let hasSameConnection
    (isExcluded: Guid -> bool)
    (sourceId: Guid) (targetId: Guid) (arrowType: ArrowType)
    (arrows: seq<#DsArrow>) : bool =
    arrows
    |> Seq.exists (fun arrow ->
        not (isExcluded arrow.Id)
        && isSameConnection arrow.SourceId arrow.TargetId arrow.ArrowType sourceId targetId arrowType)

let private noneExcluded (_: Guid) = false

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
                // 같은 System에 속한 Work끼리만 연결, 같은 연결(isSameConnection)이 이미 있으면 제외
                if Queries.arrowWorksOf sourceParent store |> hasSameConnection noneExcluded sourceId targetId arrowType then None
                else Some (sourceType, sourceParent, sourceId, targetId)
            | EntityKind.Call when sourceParent = targetParent ->
                // 같은 Work에 속한 Call끼리만 연결, 같은 연결이 이미 있으면 제외
                if Queries.arrowCallsOf sourceParent store |> hasSameConnection noneExcluded sourceId targetId arrowType then None
                else Some (sourceType, sourceParent, sourceId, targetId)
            | _ -> None)

/// 선택 순서로 새로 이을 수 있는 화살표 종류 — 이을 쌍이 하나라도 남은 종류만. 비면 이을 것이 없다.
/// 연결 메뉴를 띄울지와 메뉴에서 어떤 종류를 켤지를 정한다(Start 가 이미 있어도 Reset 등은 남는다).
let connectableArrowTypesForSelection (store: DsStore) (orderedNodeIds: seq<Guid>) : ArrowType list =
    let ids = Seq.toList orderedNodeIds
    EntityKindRules.availableArrowTypes true
    |> List.filter (fun arrowType -> not (orderedArrowLinksForSelection store ids arrowType).IsEmpty)
