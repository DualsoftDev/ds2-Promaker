namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store


// ─── ApiDef helpers ──────────────────────────────────────────────────

module internal PanelApiDefOps =
    let toApiDefPanelItem (apiDef: ApiDef) =
        ApiDefPanelItem(
            apiDef.Id, apiDef.Name, apiDef.ActionType, apiDef.SensingType,
            apiDef.TxGuid, apiDef.RxGuid,
            apiDef.Description |> Option.defaultValue "")

// ─── ApiDef extensions ───────────────────────────────────────────────

[<Extension>]
type DsStorePanelApiDefExtensions =
    [<Extension>]
    static member GetApiDefsForSystem(store: DsStore, systemId: Guid) : ApiDefPanelItem list =
        Queries.apiDefsOf systemId store
        |> List.map PanelApiDefOps.toApiDefPanelItem

    [<Extension>]
    static member GetWorksForSystem(store: DsStore, systemId: Guid) : WorkDropdownItem list =
        Queries.flowsOf systemId store
        |> List.collect (fun flow -> Queries.worksOf flow.Id store)
        |> List.map (fun work -> WorkDropdownItem(work.Id, work.Name))

    [<Extension>]
    static member TryGetApiDefForEdit(store: DsStore, apiDefId: Guid) : (Guid * ApiDefPanelItem) option =
        Queries.getApiDef apiDefId store
        |> Option.map (fun apiDef -> apiDef.ParentId, PanelApiDefOps.toApiDefPanelItem apiDef)

    [<Extension>]
    static member TryGetApiDefForEditOrNull(store: DsStore, apiDefId: Guid) : ApiDefEditInfo =
        DsStorePanelApiDefExtensions.TryGetApiDefForEdit(store, apiDefId)
        |> Option.map (fun (systemId, item) -> ApiDefEditInfo(systemId, item))
        |> Option.toObj

    [<Extension>]
    static member AddApiDefWithProperties
        (store: DsStore, name: string, systemId: Guid,
         actionType: ActionType, sensingType: SensingType,
         txGuid: Guid option, rxGuid: Guid option, description: string) : Guid =
        StoreLog.debug($"name={name}, systemId={systemId}, actionType={actionType}, sensingType={sensingType}")
        if System.String.IsNullOrWhiteSpace name then
            invalidOp "ApiDef 이름은 비어있거나 공백만일 수 없습니다."
        StoreLog.requireSystem(store, systemId) |> ignore
        if not (Queries.isApiDefNameUniqueInSystem systemId name None store) then
            invalidOp $"같은 System 내에 이미 '{name}' ApiDef가 존재합니다."
        let apiDef = ApiDef(name, systemId)
        apiDef.ActionType <- actionType
        apiDef.SensingType <- sensingType
        apiDef.TxGuid <- txGuid
        apiDef.RxGuid <- rxGuid
        apiDef.Description <- (if System.String.IsNullOrEmpty description then None else Some description)
        store.WithTransaction($"ApiDef 추가 \"{name}\"", fun () ->
            store.TrackAdd(store.ApiDefs, apiDef))
        store.EmitAndHistory(ApiDefAdded apiDef)
        apiDef.Id

    /// 이름만으로 ApiDef 추가 — Action/Sensing 은 기본값(Normal), Tx/Rx·설명 없음.
    [<Extension>]
    static member AddApiDefWithProperties
        (store: DsStore, name: string, systemId: Guid) : Guid =
        DsStorePanelApiDefExtensions.AddApiDefWithProperties(
            store, name, systemId, ActionType.Normal None, SensingType.Normal None, None, None, "")

    [<Extension>]
    static member UpdateApiDef
        (store: DsStore, apiDefId: Guid, newName: string,
         actionType: ActionType, sensingType: SensingType,
         txGuid: Guid option, rxGuid: Guid option, description: string) =
        StoreLog.debug($"apiDefId={apiDefId}, newName={newName}, actionType={actionType}, sensingType={sensingType}")
        StoreLog.requireApiDef(store, apiDefId) |> ignore
        store.WithTransaction("ApiDef 편집", fun () ->
            store.TrackMutate(store.ApiDefs, apiDefId, fun d ->
                d.Name <- newName
                d.ActionType <- actionType
                d.SensingType <- sensingType
                d.TxGuid <- txGuid
                d.RxGuid <- rxGuid
                d.Description <- (if System.String.IsNullOrEmpty description then None else Some description)))
        store.EmitRefreshAndHistory()

// ─── ApiCall extensions ──────────────────────────────────────────────

[<Extension>]
type DsStorePanelApiCallExtensions =
    [<Extension>]
    static member GetDeviceApiDefOptionsForCall(store: DsStore, callId: Guid) : DeviceApiDefOption list =
        let systems =
            match StoreHierarchyQueries.tryFindProjectIdForEntity store EntityKind.Call callId with
            | Some projectId -> Queries.passiveSystemsOf projectId store
            | None -> Queries.allProjects store |> List.collect (fun p -> Queries.passiveSystemsOf p.Id store)
        systems
        |> List.distinctBy (fun s -> s.Id)
        |> List.collect (fun system ->
            Queries.apiDefsOf system.Id store
            |> List.map (fun apiDef -> DeviceApiDefOption(apiDef.Id, system.Name, apiDef.Name)))
        |> List.sortBy (fun item -> item.DisplayName)

    [<Extension>]
    static member GetCallApiCallsForPanel(store: DsStore, callId: Guid) : CallApiCallPanelItem list =
        let resolvedId = Queries.resolveOriginalCallId callId store
        DirectPanelOps.withCallOrEmpty store resolvedId (fun call ->
            call.ApiCalls |> Seq.map (DirectPanelOps.toCallApiCallPanelItem store) |> Seq.toList)

    [<Extension>]
    static member TryGetCallApiCallForPanel(store: DsStore, callId: Guid, apiCallId: Guid) : CallApiCallPanelItem option =
        let resolvedId = Queries.resolveOriginalCallId callId store
        match Queries.getCall resolvedId store with
        | Some call ->
            call.ApiCalls
            |> Seq.tryFind (fun apiCall -> apiCall.Id = apiCallId)
            |> Option.map (DirectPanelOps.toCallApiCallPanelItem store)
        | None ->
            StoreLog.warn($"Call not found. id={callId}")
            None

    [<Extension>]
    static member TryGetCallApiCallForPanelOrNull(store: DsStore, callId: Guid, apiCallId: Guid) : CallApiCallPanelItem =
        DsStorePanelApiCallExtensions.TryGetCallApiCallForPanel(store, callId, apiCallId)
        |> Option.toObj

    [<Extension>]
    static member AddApiCallFromPanel
        (store: DsStore, callId: Guid, apiDefId: Guid,
         outputTagName: string, outputAddress: string,
         inputTagName: string, inputAddress: string,
         outTypeIndex: int, outText: string, inTypeIndex: int, inText: string)
        : Guid =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, apiDefId={apiDefId}")
        let apiDef = StoreLog.requireApiDef(store, apiDefId)
        let call = StoreLog.requireCall(store, callId)
        let outputSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        let inputSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        let apiCall = DirectPanelOps.buildApiCall apiDef apiDef.Name None outputTagName outputAddress inputTagName inputAddress None inputSpec outputSpec
        DirectPanelOps.withTransactionCallProps store callId "ApiCall 추가" (fun () ->
            DirectPanelOps.addApiCallToStore store call apiCall)
        apiCall.Id

    [<Extension>]
    static member UpdateApiCallFromPanel
        (store: DsStore, callId: Guid, apiCallId: Guid, apiDefId: Guid, apiCallName: string,
         outputTagName: string, outputAddress: string,
         inputTagName: string, inputAddress: string,
         outTypeIndex: int, outText: string, inTypeIndex: int, inText: string)
        : bool =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, apiCallId={apiCallId}, apiDefId={apiDefId}")
        let call = StoreLog.requireCall(store, callId)
        let newApiDef = StoreLog.requireApiDef(store, apiDefId)
        StoreLog.requireApiCallInCall(call, apiCallId)
        let outputSpec = PropertyPanelValueSpec.parseFromPanel outTypeIndex outText
        let inputSpec = PropertyPanelValueSpec.parseFromPanel inTypeIndex inText
        // 지웠다가 새로 만들면 OriginFlowId · ContactKind · 태그 설명/데이터 타입/기본값이 사라지고 목록 끝으로 밀렸다 —
        // 기존 ApiCall 을 제자리에서 고친다(ApiCall 사전을 기록하므로 Undo 도 그대로 된다).
        DirectPanelOps.withTransactionCallProps store callId "Update ApiCall" (fun () ->
            store.TrackMutate(store.ApiCalls, apiCallId, fun apiCall ->
                DirectPanelOps.applyPanelEdits
                    apiCall newApiDef apiCallName outputTagName outputAddress inputTagName inputAddress inputSpec outputSpec))
        true

    [<Extension>]
    static member RemoveApiCallFromCall(store: DsStore, callId: Guid, apiCallId: Guid) =
        Queries.requireNonReferenceCall callId store
        StoreLog.debug($"callId={callId}, apiCallId={apiCallId}")
        let call = StoreLog.requireCall(store, callId)
        StoreLog.requireApiCallInCall(call, apiCallId)
        DirectPanelOps.withTransactionCallProps store callId "ApiCall 제거" (fun () ->
            DirectPanelOps.removeApiCallFromStore store call apiCallId)
