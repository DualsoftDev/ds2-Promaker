/// Device(Passive) System 사용 현황(#263) — 어느 System 의 어느 Call 이 이 Device 의 Api 를 쓰는지.
/// Device 는 독립 System 이라 여러 System 이 같은 Device 를 같이 쓸 수 있다(이름 `{Flow}_{별칭}` 로 공유).
/// 그래서 "어느 System 소속인지" 가 아니라 "어디에 쓰이는지" 를 보여 주고, 아무도 안 쓰면 미사용으로 표시한다.
module Ds2.Editor.DeviceUsageQueries

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

/// 사용처 한 곳 — Call 의 ApiCall(또는 Call·Work 조건의 ApiCall)이 이 Device 의 Api 를 가리킨다.
[<Sealed; AllowNullLiteral>]
type DeviceUsageItem(systemName: string, flowName: string, workName: string, callName: string,
                     apiName: string, ownerId: Guid, isCondition: bool) =
    member _.SystemName = systemName
    member _.FlowName = flowName
    member _.WorkName = workName
    /// Work 조건에서 쓰면 빈 문자열.
    member _.CallName = callName
    member _.ApiName = apiName
    /// 이 사용을 가진 Call(Work 조건이면 Work)의 Id.
    member _.OwnerId = ownerId
    member _.IsCondition = isCondition
    /// "System / Flow / Work / Call" — 표시용.
    member _.Path =
        [ systemName; flowName; workName; callName ]
        |> List.filter (String.IsNullOrEmpty >> not)
        |> String.concat " / "

/// 트리 툴팁·미사용 표시용 요약.
[<Sealed; AllowNullLiteral>]
type DeviceUsageSummary(text: string, isUnused: bool) =
    member _.Text = text
    member _.IsUnused = isUnused

let rec private conditionLeaves (conditions: seq<Condition>) : seq<ApiCall> =
    seq {
        for condition in conditions do
            yield! condition.ApiCalls
            yield! conditionLeaves condition.Children
    }

/// 모델 전체를 한 번 훑어 Device System Id → 사용처 목록. Device 의 Api(ApiDef)를 가리키는 ApiCall 기준.
let usageByDevice (store: DsStore) : IReadOnlyDictionary<Guid, DeviceUsageItem list> =
    let apiDefs = store.ApiDefsReadOnly
    let result = Dictionary<Guid, ResizeArray<DeviceUsageItem>>()
    let names (workId: Guid) =
        match Queries.getWork workId store with
        | None -> "", "", ""
        | Some work ->
            match Queries.getFlow work.ParentId store with
            | None -> "", "", work.LocalName
            | Some flow ->
                let systemName = Queries.getSystem flow.ParentId store |> Option.map (fun s -> s.Name) |> Option.defaultValue ""
                systemName, flow.Name, work.LocalName
    let add (apiCall: ApiCall) (workId: Guid) (callName: string) (ownerId: Guid) (isCondition: bool) =
        match apiCall.ApiDefId with
        | Some defId ->
            match apiDefs.TryGetValue defId with
            | true, def ->
                let systemName, flowName, workName = names workId
                let item = DeviceUsageItem(systemName, flowName, workName, callName, def.Name, ownerId, isCondition)
                match result.TryGetValue def.ParentId with
                | true, list -> list.Add item
                | _ -> result.[def.ParentId] <- ResizeArray [ item ]
            | _ -> ()
        | None -> ()
    for call in store.CallsReadOnly.Values do
        for apiCall in call.ApiCalls do add apiCall call.ParentId call.Name call.Id false
        for apiCall in conditionLeaves call.Conditions do add apiCall call.ParentId call.Name call.Id true
    for work in store.WorksReadOnly.Values do
        for apiCall in conditionLeaves work.Conditions do add apiCall work.Id "" work.Id true
    let ordered =
        result
        |> Seq.map (fun kv ->
            KeyValuePair(
                kv.Key,
                kv.Value
                |> Seq.sortBy (fun u -> u.SystemName, u.FlowName, u.WorkName, u.CallName, u.ApiName, u.IsCondition)
                |> Seq.toList))
    Dictionary<Guid, DeviceUsageItem list>(ordered) :> IReadOnlyDictionary<_, _>

/// 한 Device 의 사용처 — 속성 창 "사용 현황" 목록.
let usageOf (store: DsStore) (deviceSystemId: Guid) : DeviceUsageItem list =
    match (usageByDevice store).TryGetValue deviceSystemId with
    | true, items -> items
    | _ -> []

/// 요약 문구 — "사용 현황: LineA 3곳, LineB 1곳" (곳 = 그 Device 를 쓰는 Call·Work 수). 아무도 안 쓰면 미사용.
let summarize (items: DeviceUsageItem list) : DeviceUsageSummary =
    if items.IsEmpty then
        DeviceUsageSummary("사용 현황: 미사용 — 이 Device 를 쓰는 Call 이 없습니다.", true)
    else
        let perSystem =
            items
            |> List.groupBy (fun u -> u.SystemName)
            |> List.map (fun (systemName, us) ->
                let owners = us |> List.map (fun u -> u.OwnerId) |> List.distinct |> List.length
                $"{systemName} {owners}곳")
        DeviceUsageSummary("사용 현황: " + String.Join(", ", perSystem), false)

/// 여러 Device 의 요약을 한 번에 — Device 트리 툴팁·미사용 표시용. 사용처가 없는 Device 도 미사용 요약을 받는다.
let summariesOf (store: DsStore) (deviceSystemIds: seq<Guid>) : IReadOnlyDictionary<Guid, DeviceUsageSummary> =
    let byDevice = usageByDevice store
    let result = Dictionary<Guid, DeviceUsageSummary>()
    for id in deviceSystemIds do
        let items =
            match byDevice.TryGetValue id with
            | true, items -> items
            | _ -> []
        result.[id] <- summarize items
    result :> IReadOnlyDictionary<_, _>
