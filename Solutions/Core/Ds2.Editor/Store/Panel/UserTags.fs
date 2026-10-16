namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store
open Ds2.Core.LoggingHelpers


// ─── UserTag 패널 투영 ───────────────────────────────────────────────

module internal PanelUserTagOps =

    /// System 의 UserTag → 패널 항목(저장 인덱스 보존). 해석은 Core(UserTagStore.parsedTagsOf)가 한다.
    let toPanelItems (sys: DsSystem) : UserTagPanelItem list =
        UserTagStore.parsedTagsOf sys
        |> List.map (fun (i, tag) ->
            UserTagPanelItem(
                i, tag.Name,
                UserTagHelpers.logLevelToString tag.LogLevel,
                tag.TagAddress,
                UserTagHelpers.valueTypeToString tag.ValueType,
                UserTagHelpers.matchOpToString tag.MatchOp,
                tag.MatchValue))


// ─── UserTag 편집 ────────────────────────────────────────────────────

/// 편집기의 UserTag 조작. 모델 쓰기는 Core 의 UserTagStore 가 하고, 여기서는 Undo 트랜잭션·변경 추적·
/// SystemPropsChanged 이벤트로 감싼다. 읽기(GetAllUserTagsForProject)와 Undo 없는 서버 측 교체
/// (ReplaceUserTags)는 Core 의 DsStoreUserTagExtensions 에 있다 — 서버(DSPilot·Hub)는 편집기를 참조하지 않는다.
[<Extension>]
type DsStorePanelUserTagExtensions =

    /// 모든 UserTag 편집이 같은 길을 간다 — System 존재 확인 → Undo 트랜잭션 → 변경 추적 → 이벤트.
    /// `changed` 는 이벤트 발행만 정한다 — 트랜잭션(Undo 항목)은 이미 열린 뒤라, 바뀔 것이 없으면 호출 전에 걸러야 한다.
    static member private Mutate
        (store: DsStore, systemId: Guid, op: string, label: string,
         mutate: DsSystem -> 'T, changed: 'T -> bool) : 'T =
        StoreLog.debug($"{op} systemId={systemId}")
        StoreLog.requireSystem(store, systemId, op) |> ignore
        let mutable result = Unchecked.defaultof<'T>
        store.WithTransaction(label, fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys -> result <- mutate sys))
        if changed result then store.EmitAndHistory(SystemPropsChanged systemId)
        result

    [<Extension>]
    static member GetUserTagsForSystem(store: DsStore, systemId: Guid) : UserTagPanelItem list =
        match Queries.getSystem systemId store with
        | Some sys -> PanelUserTagOps.toPanelItems sys
        | None -> []

    /// 반환 = 추가된 항목의 저장 인덱스.
    [<Extension>]
    static member AddUserTag
        (store: DsStore, systemId: Guid,
         name: string, logLevel: string, tagAddress: string, valueType: string,
         matchOp: string, matchValue: string) : int =
        DsStorePanelUserTagExtensions.Mutate(
            store, systemId, "AddUserTag", $"사용자 태그 추가 \"{name}\"",
            (fun sys ->
                let props = UserTagStore.ensureLoggingProps sys
                props.UserTags.Add(UserTagHelpers.format (UserTagStore.buildTag name logLevel tagAddress valueType matchOp matchValue))
                props.UserTags.Count - 1),
            (fun _ -> true))

    /// 반환 = 실제로 바뀌었는지. 값이 그대로면(편집 창에서 아무것도 안 바꾸고 확인) 트랜잭션·Undo 항목·이벤트를 만들지 않는다 —
    /// 예전엔 값이 같아도 History 에 "사용자 태그 편집" 이 쌓였다(#230).
    [<Extension>]
    static member UpdateUserTag
        (store: DsStore, systemId: Guid, index: int,
         name: string, logLevel: string, tagAddress: string, valueType: string,
         matchOp: string, matchValue: string) : bool =
        let newTag = UserTagStore.buildTag name logLevel tagAddress valueType matchOp matchValue
        let formatted = UserTagHelpers.format newTag
        // 저장 문자열이 아니라 해석한 태그끼리 비교한다 — 예전 형식(칸 수가 적은 문자열·다른 표기)은 같은 태그도 글자가 달라
        // 아무것도 안 바꿨는데 "바뀜" 으로 잡혔다.
        let willChange =
            match (StoreLog.requireSystem(store, systemId, "UpdateUserTag")).GetLoggingProperties() with
            | Some p -> index >= 0 && index < p.UserTags.Count && UserTagHelpers.parse p.UserTags.[index] <> Some newTag
            | None -> false
        willChange
        && DsStorePanelUserTagExtensions.Mutate(
            store, systemId, "UpdateUserTag", "사용자 태그 편집",
            (fun sys ->
                match sys.GetLoggingProperties() with
                | Some p when index >= 0 && index < p.UserTags.Count ->
                    p.UserTags.[index] <- formatted
                    true
                | _ -> false),
            id)

    /// 반환 = 지웠는지. 없는 인덱스면 트랜잭션·Undo 항목을 만들지 않는다.
    [<Extension>]
    static member RemoveUserTag(store: DsStore, systemId: Guid, index: int) : bool =
        let exists =
            match (StoreLog.requireSystem(store, systemId, "RemoveUserTag")).GetLoggingProperties() with
            | Some p -> index >= 0 && index < p.UserTags.Count
            | None -> false
        exists
        && DsStorePanelUserTagExtensions.Mutate(
            store, systemId, "RemoveUserTag", "사용자 태그 삭제",
            (fun sys ->
                match sys.GetLoggingProperties() with
                | Some p when index >= 0 && index < p.UserTags.Count ->
                    p.UserTags.RemoveAt(index)
                    true
                | _ -> false),
            id)

    /// CSV 일괄 추가 — 한 트랜잭션으로 append. 반환 = 추가 수(0 이면 트랜잭션·이벤트 없음).
    [<Extension>]
    static member AddUserTagsBatch
        (store: DsStore, systemId: Guid,
         entries: System.Collections.Generic.IReadOnlyList<struct (string * string * string * string * string * string)>) : int =
        if isNull (box entries) || entries.Count = 0 then 0
        else
            DsStorePanelUserTagExtensions.Mutate(
                store, systemId, "AddUserTagsBatch", $"사용자 태그 일괄 추가 ({entries.Count}건)",
                (fun sys -> UserTagStore.appendAll sys entries),
                (fun added -> added > 0))

    /// CSV 교체 — 기존 항목 전부 삭제 후 새 항목 추가를 Undo 한 단위로. 서버 측(Undo 없는) 교체는 Core 의 ReplaceUserTags.
    [<Extension>]
    static member ReplaceUserTagsWithUndo
        (store: DsStore, systemId: Guid,
         entries: System.Collections.Generic.IReadOnlyList<struct (string * string * string * string * string * string)>) : int =
        DsStorePanelUserTagExtensions.Mutate(
            store, systemId, "ReplaceUserTagsWithUndo", $"사용자 태그 교체 ({entries.Count}건)",
            (fun sys -> UserTagStore.replaceAll sys entries),
            (fun _ -> true))

    /// 일괄 삭제 — 해당 System 의 UserTag 를 전부 제거. 삭제할 것이 없으면 트랜잭션·Undo 항목·이벤트를 만들지 않는다.
    [<Extension>]
    static member ClearUserTags(store: DsStore, systemId: Guid) : int =
        let sys = StoreLog.requireSystem(store, systemId)
        let count =
            match sys.GetLoggingProperties() with
            | Some p -> p.UserTags.Count
            | None -> 0
        if count = 0 then 0
        else
            DsStorePanelUserTagExtensions.Mutate(
                store, systemId, "ClearUserTags", "사용자 태그 일괄 삭제",
                (fun s ->
                    match s.GetLoggingProperties() with
                    | Some p -> p.UserTags.Clear()
                    | None -> ()
                    count),
                (fun _ -> true))
