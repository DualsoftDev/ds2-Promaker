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

/// 프로젝트에 같은 이름의 System 이 이미 있을 때 — 그 Device 를 통째로 건너뛰거나, 비어 있는 새 이름(Cyl_1 …)의
/// Device 로 만든다. 사용자가 고른다. 기존 Device 안에 Api 를 섞어 넣지 않는다 — CSV 의 Device 는 그 자체로 하나의 Device 다.
type DeviceNameConflictMode =
    | Skip = 0
    | Rename = 1

[<Sealed>]
type DeviceImportResult(addedDevices: int, addedApis: int, skippedDevices: string list, renamedDevices: (string * string) list) =
    member _.AddedDevices = addedDevices
    member _.AddedApis = addedApis
    /// 이름이 겹쳐 건너뛴 Device.
    member _.SkippedDevices = skippedDevices
    /// (CSV 의 Device 이름, 새로 붙인 이름).
    member _.RenamedDevices = renamedDevices

module internal DeviceImportOps =

    /// 프로젝트의 모든 System 이름(Active·Passive) — Device 이름 충돌 기준.
    let systemNamesOf (store: DsStore) (projectId: Guid) : Set<string> =
        Queries.projectSystemsOf projectId store |> List.map (fun s -> s.Name) |> Set.ofList

    /// name_1, name_2 … 중 쓰이지 않은 첫 이름.
    let nextFreeName (taken: Set<string>) (name: string) =
        Seq.initInfinite (fun i -> $"{name}_{i + 1}") |> Seq.find (fun candidate -> not (taken.Contains candidate))

[<Extension>]
type DsStoreDeviceImportExtensions =

    /// 불러올 Device 중 프로젝트에 같은 이름의 System 이 이미 있는 것 — 불러오기 전에 건너뛸지 새 이름으로 넣을지 묻는다.
    [<Extension>]
    static member FindExistingDeviceNames(store: DsStore, projectId: Guid, specs: seq<DeviceApiSpec>) : string list =
        let taken = DeviceImportOps.systemNamesOf store projectId
        specs |> Seq.map (fun spec -> spec.Device) |> Seq.distinct |> Seq.filter taken.Contains |> Seq.toList

    /// Device CSV 를 기존 프로젝트에 더한다 — Undo 한 번(#253). Device 마다 Device System(Flow "{이름}_Flow")을 새로 만들고,
    /// Api 마다 Device Work 와 ApiDef(Tx·Rx = 그 Work)를 만들어 Work 끼리 상호 리셋으로 잇는다 — Call 로 Device 를 만들 때와
    /// 같은 모양이라 시뮬레이션이 같게 돈다. 이름이 겹치는 Device 는 mode 대로 통째로 건너뛰거나 새 이름으로 만든다.
    [<Extension>]
    static member ImportDevices
        (store: DsStore, projectId: Guid, specs: seq<DeviceApiSpec>, mode: DeviceNameConflictMode) : DeviceImportResult =
        let specs = Seq.toList specs
        StoreLog.debug($"projectId={projectId}, count={specs.Length}, mode={mode}")
        if specs.IsEmpty then DeviceImportResult(0, 0, [], [])
        else
            let result =
                store.WithTransaction($"Device CSV 불러오기 ({specs.Length}건)", fun () ->
                    let mutable taken = DeviceImportOps.systemNamesOf store projectId
                    let mutable addedDevices = 0
                    let mutable addedApis = 0
                    let skipped = ResizeArray<string>()
                    let renamed = ResizeArray<string * string>()
                    for deviceName, group in specs |> List.groupBy (fun spec -> spec.Device) do
                        let targetName =
                            if not (taken.Contains deviceName) then Some deviceName
                            elif mode = DeviceNameConflictMode.Rename then
                                let newName = DeviceImportOps.nextFreeName taken deviceName
                                renamed.Add((deviceName, newName))
                                Some newName
                            else
                                skipped.Add deviceName
                                None
                        match targetName with
                        | None -> ()
                        | Some name ->
                            let system = DsSystem(name)
                            let flow = Flow($"{name}_Flow", system.Id)
                            store.TrackAdd(store.Systems, system)
                            store.TrackMutate(store.Projects, projectId, fun p -> p.PassiveSystemIds.Add(system.Id))
                            store.TrackAdd(store.Flows, flow)
                            taken <- taken.Add name
                            addedDevices <- addedDevices + 1
                            let works =
                                [ for spec in group do
                                    let work = Work(flow.Name, spec.Api, flow.Id)
                                    work.Duration <- Some (TimeSpan.FromMilliseconds 500.)
                                    store.TrackAdd(store.Works, work)
                                    let apiDef = ApiDef(spec.Api, system.Id)
                                    apiDef.ActionType <- spec.ActionType
                                    apiDef.SensingType <- spec.SensingType
                                    apiDef.TxGuid <- Some work.Id
                                    apiDef.RxGuid <- Some work.Id
                                    if spec.Description <> "" then apiDef.Description <- Some spec.Description
                                    store.TrackAdd(store.ApiDefs, apiDef)
                                    yield work ]
                            addedApis <- addedApis + works.Length
                            DirectDeviceOps.createMutualResetArrows store system.Id works
                    DeviceImportResult(addedDevices, addedApis, List.ofSeq skipped, List.ofSeq renamed))
            if result.AddedDevices > 0 then
                store.EmitRefreshAndHistory()
            result
