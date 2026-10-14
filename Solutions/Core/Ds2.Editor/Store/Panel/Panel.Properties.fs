namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store


[<Extension>]
type DsStorePanelPropertiesExtensions =
    /// 프로젝트 속성 일괄 변경 (Undo 지원)
    [<Extension>]
    static member UpdateProjectProperties(store: DsStore, author: string, dateTime: DateTimeOffset,
                                          version: string) =
        StoreLog.debug($"UpdateProjectProperties")
        let project = Queries.allProjects store |> List.head
        store.WithTransaction("프로젝트 속성 변경", fun () ->
            store.TrackMutate(store.Projects, project.Id, fun p ->
                p.Author <- author
                p.DateTime <- dateTime
                p.Version <- version))

    /// Call 생성 직후 대화상자의 신호 수량(SignalCounts)을 Call 제어 속성에 기록한다. 직전 생성 단계와 한 번의 Undo 로
    /// 묶는다 — 예전엔 C# 이 store 엔티티를 직접 고쳐 Undo/Redo 를 거치면 신호 수량이 사라졌다.
    [<Extension>]
    static member ApplySignalCountsToNewCalls
        (store: DsStore, callIds: seq<Guid>, signalCounts: System.Collections.Generic.IDictionary<string, int>) =
        let ids = callIds |> Seq.distinct |> Seq.filter store.Calls.ContainsKey |> Seq.toList
        if not ids.IsEmpty && not (isNull signalCounts) && signalCounts.Count > 0 then
            let previousLabel = (StoreEditorState.get store).UndoManager.UndoLabels |> List.tryHead
            store.WithTransaction("신호 수량", fun () ->
                for id in ids do
                    store.TrackMutate(store.Calls, id, fun call ->
                        let props =
                            match call.GetControlProperties() with
                            | Some p -> p
                            | None ->
                                let p = ControlCallProperties()
                                call.SetControlProperties p
                                p
                        for kv in signalCounts do
                            props.SignalCounts.[kv.Key] <- kv.Value))
            match previousLabel with
            | Some label -> store.MergeLastTransactions(2, label)
            | None -> store.EmitHistoryChanged()

    /// 태그 마법사 1단계 — SystemType 별 FB 선두 주소(IW/QW/MW)를 기본 System(첫 프로젝트의 첫 active System)의
    /// 제어 속성에 Undo 가능하게 기록한다. 숫자가 아닌 칸은 건드리지 않는다.
    /// entries: (concreteSystemType, iwBase, qwBase, mwBase). 반환 = 기록한 System 이 있으면 true.
    [<Extension>]
    static member UpdateSystemTypeBaseAddresses
        (store: DsStore, entries: seq<struct (string * string * string * string)>) : bool =
        let isNumber (s: string) = not (isNull s) && fst (Int32.TryParse(s, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture))
        let primary =
            Queries.allProjects store
            |> List.tryHead
            |> Option.bind (fun p -> Queries.activeSystemsOf p.Id store |> List.tryHead)
        match primary with
        | None -> false
        | Some sys ->
            let list = entries |> Seq.toList
            store.WithTransaction("시스템 선두 주소 저장", fun () ->
                store.TrackMutate(store.Systems, sys.Id, fun s ->
                    let cp =
                        match s.GetControlProperties() with
                        | Some cp -> cp
                        | None ->
                            let cp = ControlSystemProperties()
                            s.SetControlProperties cp
                            cp
                    for struct (systemType, iw, qw, mw) in list do
                        let preset =
                            match cp.FBTagMapPresets.TryGetValue systemType with
                            | true, p -> p
                            | _ ->
                                let p = FBTagMapPreset()
                                cp.FBTagMapPresets.[systemType] <- p
                                p
                        let ba = if isNull (box preset.BaseAddresses) then FBBaseAddressSet() else preset.BaseAddresses
                        if isNumber iw then ba.InputBase <- iw
                        if isNumber qw then ba.OutputBase <- qw
                        if isNumber mw then ba.MemoryBase <- mw
                        preset.BaseAddresses <- ba))
            store.EmitAndHistory(SystemPropsChanged sys.Id)
            true

    /// 태그 마법사 1단계 — Flow 별 선두 주소 덮어쓰기(BaseAddressOverride)를 Undo 가능하게 기록한다.
    /// 세 칸이 모두 비면 그 Flow 는 건너뛰고, 숫자가 아닌 칸은 기본값으로 둔다. entries: (flowId, iw, qw, mw). 반환 = 기록한 Flow 수.
    [<Extension>]
    static member UpdateFlowBaseAddressOverrides
        (store: DsStore, entries: seq<struct (Guid * string * string * string)>) : int =
        let isNumber (s: string) = not (isNull s) && fst (Int32.TryParse(s, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture))
        let list =
            entries
            |> Seq.filter (fun struct (flowId, iw, qw, mw) ->
                store.Flows.ContainsKey flowId
                && not (String.IsNullOrWhiteSpace iw && String.IsNullOrWhiteSpace qw && String.IsNullOrWhiteSpace mw))
            |> Seq.toList
        if list.IsEmpty then 0
        else
            store.WithTransaction("Flow 선두 주소 저장", fun () ->
                for struct (flowId, iw, qw, mw) in list do
                    store.TrackMutate(store.Flows, flowId, fun f ->
                        let cfp =
                            match f.GetControlProperties() with
                            | Some p -> p
                            | None ->
                                let p = ControlFlowProperties()
                                f.SetControlProperties p
                                p
                        let ba = FBBaseAddressSet()
                        if isNumber iw then ba.InputBase <- iw
                        if isNumber qw then ba.OutputBase <- qw
                        if isNumber mw then ba.MemoryBase <- mw
                        cfp.BaseAddressOverride <- Some ba))
            store.EmitRefreshAndHistory()
            list.Length

    /// 시스템 타입 변경 (Undo 지원)
    [<Extension>]
    static member UpdateSystemType(store: DsStore, systemId: Guid, systemType: string) =
        StoreLog.debug($"UpdateSystemType systemId={systemId}, systemType={systemType}")
        store.WithTransaction("시스템 타입 변경", fun () ->
            store.TrackMutate(store.Systems, systemId, fun sys ->
                sys.SystemType <- DirectPanelOps.toOpt systemType))
        store.EmitAndHistory(SystemPropsChanged systemId)

    /// ApiCall IO 태그 일괄 업데이트 — 단일 transaction + Call 별 1회 이벤트.
    /// Wizard Apply 의 N 행 적용 시 N transactions → 1 transaction 으로 축소.
    /// entries: (callId, apiCallId, outTag, inTag) 튜플 시퀀스. null IOTag 는 None 으로 저장.
    /// 반환값: 실제로 적용된 entry 개수.
    [<Extension>]
    static member UpdateApiCallIoTagsBatch(
            store: DsStore,
            entries: System.Collections.Generic.IReadOnlyList<struct (Guid * Guid * IOTag * IOTag)>) : int =
        if isNull (box entries) || entries.Count = 0 then 0
        else
            let mutable applied = 0
            let touchedCallIds = System.Collections.Generic.HashSet<Guid>()
            store.WithTransaction("IO 태그 일괄 업데이트", fun () ->
                for e in entries do
                    let struct (callId, apiCallId, outTag, inTag) = e
                    if callId <> Guid.Empty && apiCallId <> Guid.Empty then
                        try
                            Queries.requireNonReferenceCall callId store
                            let call = StoreLog.requireCall(store, callId)
                            // ApiCall 사전을 기록한다. Call 만 기록하면 Undo 가 Call 을 되돌린 뒤 RewireApiCallReferences 가
                            // 새 태그를 가진 ApiCall 인스턴스를 다시 끼워 넣어, 되돌렸다고 표시돼도 태그는 그대로였다.
                            if call.ApiCalls |> Seq.exists (fun ac -> ac.Id = apiCallId) then
                                store.TrackMutate(store.ApiCalls, apiCallId, fun targetApiCall ->
                                    targetApiCall.OutTag <- Option.ofObj outTag
                                    targetApiCall.InTag  <- Option.ofObj inTag)
                                touchedCallIds.Add callId |> ignore
                                applied <- applied + 1
                        with _ -> ()
            )
            // Call 별 1회만 이벤트 — 같은 Call 내 N ApiCall 변경은 1 refresh.
            for cid in touchedCallIds do
                store.EmitAndHistory(CallPropsChanged cid)
            applied
