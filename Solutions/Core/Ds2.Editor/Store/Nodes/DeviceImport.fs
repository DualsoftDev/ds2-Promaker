namespace Ds2.Editor

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store

/// Device CSV 로 불러올 Device·Api 한 건(#253). 편집기는 CSV 라이브러리를 참조하지 않으므로 앱이 Ds2.CSV 의 행을 이것으로 옮긴다.
[<Sealed; AllowNullLiteral>]
type DeviceApiSpec(device: string, api: string, actionType: ActionType, sensingType: SensingType, description: string) =
    member _.Device = device
    member _.Api = api
    member _.ActionType = actionType
    member _.SensingType = sensingType
    /// 빈 문자열이면 설명 없음.
    member _.Description = if isNull description then "" else description
    member _.FullName = $"{device}.{api}"

/// 그 Device 에 같은 이름의 Api 가 이미 있을 때 — 건너뛰거나 새 이름(Api_1, Api_2 …)으로 더한다. 사용자가 고른다.
type DeviceApiConflictMode =
    | Skip = 0
    | Rename = 1

[<Sealed>]
type DeviceImportResult(addedDevices: int, addedApis: int, skipped: string list, renamed: (string * string) list) =
    member _.AddedDevices = addedDevices
    member _.AddedApis = addedApis
    /// 건너뛴 "Device.Api".
    member _.Skipped = skipped
    /// (원래 "Device.Api", 새 "Device.Api_1").
    member _.Renamed = renamed

module internal DeviceImportOps =

    let tryFindDevice (store: DsStore) (projectId: Guid) (name: string) =
        Queries.passiveSystemsOf projectId store |> List.tryFind (fun s -> s.Name = name)

    /// name_1, name_2 … 중 쓰이지 않은 첫 이름.
    let nextFreeName (taken: Set<string>) (name: string) =
        Seq.initInfinite (fun i -> $"{name}_{i + 1}") |> Seq.find (fun candidate -> not (taken.Contains candidate))

[<Extension>]
type DsStoreDeviceImportExtensions =

    /// 불러올 Api 중 그 Device 에 같은 이름의 Api 가 이미 있는 것 — 불러오기 전에 건너뛸지 새 이름으로 넣을지 묻는다.
    [<Extension>]
    static member FindDeviceApiConflicts(store: DsStore, projectId: Guid, specs: seq<DeviceApiSpec>) : DeviceApiSpec list =
        specs
        |> Seq.filter (fun spec ->
            match DeviceImportOps.tryFindDevice store projectId spec.Device with
            | Some device -> Queries.apiDefsOf device.Id store |> List.exists (fun d -> d.Name = spec.Api)
            | None -> false)
        |> Seq.toList

    /// Device CSV 를 기존 프로젝트에 더한다 — Undo 한 번(#253). 없는 Device 는 그 이름의 Device System 으로 새로 만들고
    /// (Flow "{이름}_Flow"), Api 마다 Device Work 와 ApiDef(Tx·Rx = 그 Work)를 만들어 Work 끼리 상호 리셋으로 잇는다.
    /// Call 로 Device 를 만들 때와 같은 모양이라 시뮬레이션이 같게 돈다. 같은 이름의 Api 는 mode 대로 건너뛰거나 새 이름으로.
    [<Extension>]
    static member ImportDeviceApis
        (store: DsStore, projectId: Guid, specs: seq<DeviceApiSpec>, mode: DeviceApiConflictMode) : DeviceImportResult =
        let specs = Seq.toList specs
        StoreLog.debug($"projectId={projectId}, count={specs.Length}, mode={mode}")
        if specs.IsEmpty then DeviceImportResult(0, 0, [], [])
        else
            let result =
                store.WithTransaction($"Device CSV 불러오기 ({specs.Length}건)", fun () ->
                    let mutable addedDevices = 0
                    let mutable addedApis = 0
                    let skipped = ResizeArray<string>()
                    let renamed = ResizeArray<string * string>()
                    for deviceName, group in specs |> List.groupBy (fun spec -> spec.Device) do
                        let device, flow =
                            match DeviceImportOps.tryFindDevice store projectId deviceName with
                            | Some existing ->
                                match Queries.flowsOf existing.Id store with
                                | flow :: _ -> existing, flow
                                | [] ->
                                    let flow = Flow($"{deviceName}_Flow", existing.Id)
                                    store.TrackAdd(store.Flows, flow)
                                    existing, flow
                            | None ->
                                let system = DsSystem(deviceName)
                                let flow = Flow($"{deviceName}_Flow", system.Id)
                                store.TrackAdd(store.Systems, system)
                                store.TrackMutate(store.Projects, projectId, fun p -> p.PassiveSystemIds.Add(system.Id))
                                store.TrackAdd(store.Flows, flow)
                                addedDevices <- addedDevices + 1
                                system, flow
                        let existingApis = Queries.apiDefsOf device.Id store |> List.map (fun d -> d.Name) |> Set.ofList
                        let mutable taken =
                            Queries.worksOf flow.Id store |> List.map (fun w -> w.LocalName) |> Set.ofList |> Set.union existingApis
                        let mutable addedHere = false
                        for spec in group do
                            let apiName =
                                if not (existingApis.Contains spec.Api) then Some spec.Api
                                elif mode = DeviceApiConflictMode.Rename then
                                    let newName = DeviceImportOps.nextFreeName taken spec.Api
                                    renamed.Add((spec.FullName, $"{deviceName}.{newName}"))
                                    Some newName
                                else
                                    skipped.Add spec.FullName
                                    None
                            match apiName with
                            | None -> ()
                            | Some name ->
                                // 같은 이름의 Device Work 가 이미 있으면(ApiDef 만 없던 경우) 그 Work 를 쓴다.
                                let work =
                                    Queries.worksOf flow.Id store
                                    |> List.tryFind (fun w -> w.LocalName = name)
                                    |> Option.defaultWith (fun () ->
                                        let w = Work(flow.Name, name, flow.Id)
                                        w.Duration <- Some (TimeSpan.FromMilliseconds 500.)
                                        store.TrackAdd(store.Works, w)
                                        w)
                                let apiDef = ApiDef(name, device.Id)
                                apiDef.ActionType <- spec.ActionType
                                apiDef.SensingType <- spec.SensingType
                                apiDef.TxGuid <- Some work.Id
                                apiDef.RxGuid <- Some work.Id
                                if spec.Description <> "" then apiDef.Description <- Some spec.Description
                                store.TrackAdd(store.ApiDefs, apiDef)
                                taken <- taken.Add name
                                addedApis <- addedApis + 1
                                addedHere <- true
                        if addedHere then
                            DirectDeviceOps.createMutualResetArrows store device.Id (Queries.worksOf flow.Id store)
                    DeviceImportResult(addedDevices, addedApis, List.ofSeq skipped, List.ofSeq renamed))
            if result.AddedApis > 0 || result.AddedDevices > 0 then
                store.EmitRefreshAndHistory()
            result
