namespace Ds2.Editor

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Core.Store
open log4net

[<RequireQualifiedAccess>]
module internal StoreAuthoring =
    let private log = LogManager.GetLogger("Ds2.Editor.Authoring")

    let private state (store: DsStore) = StoreEditorState.get store

    let recordUndo (store: DsStore) (record: UndoRecord) =
        match (state store).CurrentRecords with
        | Some records -> records.Add(record)
        | None -> invalidOp "RecordUndo called outside transaction"

    let private recordAffectedId (store: DsStore) (id: Guid) =
        match (state store).CurrentAffectedIds with
        | Some ids -> if not (ids.Contains id) then ids.Add(id)
        | None -> ()

    /// 방금 뜬 undo 스냅샷의 크기를 진행 중 트랜잭션에 더한다. 용량 상한의 계산 근거.
    let private recordSnapshotBytes (store: DsStore) (bytes: int) =
        let editorState = state store
        editorState.CurrentSnapshotBytes <- editorState.CurrentSnapshotBytes + int64 bytes


    /// 빈번한 일괄 작업 (Wizard Apply 등) 의 노이즈 방지용. 에러 로그는 영향 없음.
    let withTransaction (store: DsStore) (label: string) (action: unit -> 'T) : 'T =
        let editorState = state store
        if editorState.CurrentRecords.IsSome then
            invalidOp "Nested transactions are not supported"

        let records = ResizeArray<UndoRecord>()
        let affectedIds = ResizeArray<Guid>()
        editorState.CurrentRecords <- Some records
        editorState.CurrentAffectedIds <- Some affectedIds
        editorState.CurrentSnapshotBytes <- 0L

        try
            try
                let result = action()
                if records.Count > 0 then
                    editorState.UndoManager.Push({ Label = label; Records = Seq.toList records; AffectedEntityIds = Seq.toList affectedIds; ApproxSnapshotBytes = editorState.CurrentSnapshotBytes; LightEventOnUndo = None })
                    // round-trip §1.3 hook: transaction commit 성공 + 실 변경 발생 시점.
                    // records.Count = 0 이면 wizard 빈 apply / read-only 등 — store 상태 무변경이므로 ++ skip.
                    store.BumpRevision()
                log.Debug($"Executed: {label}")
                result
            with ex ->
                editorState.CurrentRecords <- None
                editorState.CurrentAffectedIds <- None
                editorState.CurrentSnapshotBytes <- 0L
                for i in records.Count - 1 .. -1 .. 0 do
                    records.[i].Undo()
                log.Error($"Transaction failed: {label} - {ex.Message}", ex)
                reraise()
        finally
            editorState.CurrentRecords <- None
            editorState.CurrentAffectedIds <- None
            editorState.CurrentSnapshotBytes <- 0L

    let trackAdd<'T when 'T :> DsEntity> (store: DsStore) (dict: Dictionary<Guid, 'T>) (entity: 'T) =
        let backup, backupBytes = DeepCopyHelper.backupEntitySizedAs entity
        dict.[entity.Id] <- entity
        recordAffectedId store entity.Id
        recordSnapshotBytes store backupBytes
        // Undo/Redo 는 넣을 때마다 스냅샷의 새 사본을 넣는다. 스냅샷 객체 자체를 store 에 넣으면 다음 편집(trackMutate)이
        // 그 객체를 제자리에서 바꿔 스냅샷이 오염되고, 되돌리기·다시 실행을 섞으면 이력과 모델 내용이 어긋났다.
        recordUndo store {
            Undo = fun () -> dict.Remove(entity.Id) |> ignore
            Redo = fun () -> dict.[entity.Id] <- DeepCopyHelper.backupEntityAs backup
            Description = $"Add {typeof<'T>.Name} {entity.Id}"
        }

    let trackRemove<'T when 'T :> DsEntity> (store: DsStore) (dict: Dictionary<Guid, 'T>) (id: Guid) =
        match dict.TryGetValue(id) with
        | true, entity ->
            let backup, backupBytes = DeepCopyHelper.backupEntitySizedAs entity
            dict.Remove(id) |> ignore
            recordAffectedId store id
            recordSnapshotBytes store backupBytes
            recordUndo store {
                Undo = fun () -> dict.[id] <- DeepCopyHelper.backupEntityAs backup
                Redo = fun () -> dict.Remove(id) |> ignore
                Description = $"Remove {typeof<'T>.Name} {id}"
            }
        | false, _ -> ()

    let trackMutate<'T when 'T :> DsEntity> (store: DsStore) (dict: Dictionary<Guid, 'T>) (id: Guid) (mutate: 'T -> unit) =
        match dict.TryGetValue(id) with
        | true, entity ->
            let oldSnapshot, oldBytes, oldJson = DeepCopyHelper.backupEntityWithJsonAs entity
            mutate entity
            let newSnapshot, newBytes, newJson = DeepCopyHelper.backupEntityWithJsonAs entity
            // 바뀐 게 없으면(직렬화 결과가 같으면) Undo 기록을 남기지 않는다 — 기록이 없는 트랜잭션은 이력에 들어가지 않는다.
            // 예전엔 편집 창에서 아무것도 안 바꾸고 확인해도(ApiCall Spec · UserTag · 속성 창) History 에 항목이 쌓였다(#230).
            if oldJson <> newJson then
                recordAffectedId store id
                recordSnapshotBytes store (oldBytes + newBytes)
                recordUndo store {
                    Undo = fun () -> dict.[id] <- DeepCopyHelper.backupEntityAs oldSnapshot
                    Redo = fun () -> dict.[id] <- DeepCopyHelper.backupEntityAs newSnapshot
                    Description = $"Mutate {typeof<'T>.Name} {id}"
                }
        | false, _ -> invalidOp $"Entity not found: {id}"

    let emitEvent (store: DsStore) (evt: EditorEvent) =
        let editorState = state store
        if not editorState.SuppressEvents then
            editorState.EventBus.Trigger(evt)

    let emitHistoryChanged (store: DsStore) =
        let editorState = state store
        if not editorState.SuppressEvents then
            editorState.EventBus.Trigger(HistoryChanged(editorState.UndoManager.UndoLabels, editorState.UndoManager.RedoLabels))

    let emitAndHistory (store: DsStore) (evt: EditorEvent) =
        emitEvent store evt
        emitHistoryChanged store

    let emitConnectionsChangedAndHistory (store: DsStore) =
        emitEvent store ConnectionsChanged
        emitHistoryChanged store

    let emitRefreshAndHistory (store: DsStore) =
        emitEvent store StoreRefreshed
        emitHistoryChanged store

    let emitEntitiesMovedAndHistory (store: DsStore) (ids: Guid list) =
        emitEvent store (EntitiesMoved ids)
        emitHistoryChanged store

    /// 직전 WithTransaction 에서 push 된 트랜잭션에 가벼운 이벤트 힌트를 설정.
    /// undo/redo 시 StoreRefreshed 대신 이 이벤트가 발행된다.
    let setLastUndoLightEvent (store: DsStore) (evt: EditorEvent) =
        let editorState = state store
        editorState.UndoManager.SetTopLightEvent(evt)

    /// 직전 undo/redo 가 light event 로 처리됐는지 여부.
    /// C# JumpToHistory 가 명시적 RebuildAll 을 건너뛸지 결정.
    let wasLastUndoRedoLight (store: DsStore) : bool =
        let editorState = state store
        editorState.LastUndoRedoLightEvent.IsSome

    let observeEvents (store: DsStore) =
        (state store).EventBus.Publish

    let tryGetAddedEntityId (evt: EditorEvent) : Guid option =
        match evt with
        | ProjectAdded project -> Some project.Id
        | SystemAdded system -> Some system.Id
        | FlowAdded flow -> Some flow.Id
        | WorkAdded work -> Some work.Id
        | CallAdded call -> Some call.Id
        | ApiDefAdded apiDef -> Some apiDef.Id
        | HwComponentAdded(_, id, _) -> Some id
        | _ -> None

    let isTreeStructuralEvent (evt: EditorEvent) =
        match evt with
        | ProjectAdded _
        | ProjectRemoved _
        | SystemAdded _
        | SystemRemoved _
        | FlowAdded _
        | FlowRemoved _
        | WorkAdded _
        | WorkRemoved _
        | CallAdded _
        | CallRemoved _
        | ApiDefAdded _
        | ApiDefRemoved _
        | HwComponentAdded _
        | HwComponentRemoved _ -> true
        | _ -> false

    let private extractGuidsFromDescriptions (records: UndoRecord list) =
        records
        |> List.choose (fun r ->
            let parts = r.Description.Split(' ')
            if parts.Length >= 3 then
                match Guid.TryParse(parts.[parts.Length - 1]) with
                | true, guid -> Some guid
                | _ -> None
            else None)
        |> List.distinct

    let private applyTransaction (store: DsStore) pop push apply label =
        let editorState = state store
        match pop() with
        | None -> ()
        | Some tx ->
            let lightEvent = tx.LightEventOnUndo
            // C# 측에서 추가 RebuildAll 을 건너뛸 수 있도록 마킹. 다음 undo/redo 호출까지 유효.
            editorState.LastUndoRedoLightEvent <- lightEvent
            try
                apply tx.Records
                store.RewireApiCallReferences()
                push tx
                let ids =
                    if tx.AffectedEntityIds.IsEmpty then extractGuidsFromDescriptions tx.Records
                    else tx.AffectedEntityIds
                store.LastTransactionAffectedIds <- ids
                // round-trip §1.3 hook: undo / redo 성공 시점. tx 가 pop 된 시점에 이미 실 변경 보장됨.
                store.BumpRevision()
                log.Debug($"{label}: {tx.Label}")
            finally
                // 힌트가 있으면 가벼운 이벤트(예: EntitiesMoved) + HistoryChanged 만 발행.
                // 없으면 종전대로 StoreRefreshed → C# 측 RebuildAll 트리거.
                match lightEvent with
                | Some evt ->
                    emitEvent store evt
                    emitHistoryChanged store
                | None ->
                    emitRefreshAndHistory store

    let undo (store: DsStore) =
        let editorState = state store
        applyTransaction store editorState.UndoManager.PopUndo editorState.UndoManager.PushRedo (fun rs -> for r in List.rev rs do r.Undo()) "Undo"

    let redo (store: DsStore) =
        let editorState = state store
        applyTransaction store editorState.UndoManager.PopRedo editorState.UndoManager.PushUndo (fun rs -> for r in rs do r.Redo()) "Redo"

    let private runBatch (store: DsStore) (action: unit -> unit) (steps: int) =
        let editorState = state store
        let n = max 0 steps
        if n <= 1 then
            for _ in 1 .. n do
                action()
        else
            // 다중 step undo 는 일괄 처리 후 한 번에 StoreRefreshed 발행.
            // light hint 는 다중 step 케이스에서는 무시 — 마지막에 heavy refresh.
            editorState.SuppressEvents <- true
            try
                for _ in 1 .. n do
                    action()
            finally
                editorState.SuppressEvents <- false
                editorState.LastUndoRedoLightEvent <- None
                emitRefreshAndHistory store

    let undoTo (store: DsStore) (steps: int) = runBatch store (fun () -> undo store) steps
    let redoTo (store: DsStore) (steps: int) = runBatch store (fun () -> redo store) steps

    let clearHistory (store: DsStore) =
        let editorState = state store
        editorState.UndoManager.Clear()
        emitHistoryChanged store

[<Extension>]
type DsStoreAuthoringExtensions =
    [<Extension>]
    static member WithTransaction(store: DsStore, label: string, action: unit -> 'T) : 'T =
        StoreAuthoring.withTransaction store label action

    [<Extension>]
    static member TrackAdd<'T when 'T :> DsEntity>(store: DsStore, dict: Dictionary<Guid, 'T>, entity: 'T) =
        StoreAuthoring.trackAdd store dict entity

    [<Extension>]
    static member TrackRemove<'T when 'T :> DsEntity>(store: DsStore, dict: Dictionary<Guid, 'T>, id: Guid) =
        StoreAuthoring.trackRemove store dict id

    [<Extension>]
    static member TrackMutate<'T when 'T :> DsEntity>(store: DsStore, dict: Dictionary<Guid, 'T>, id: Guid, mutate: 'T -> unit) =
        StoreAuthoring.trackMutate store dict id mutate

    [<Extension>]
    static member EmitEvent(store: DsStore, evt: EditorEvent) =
        StoreAuthoring.emitEvent store evt

    [<Extension>]
    static member EmitHistoryChanged(store: DsStore) =
        StoreAuthoring.emitHistoryChanged store

    /// 편집 이력의 현재 위치(UndoRedoManager.CurrentPosition). 저장 시점 값과 ReferenceEquals 로 비교해 "저장 안 됨" 을 판정한다.
    [<Extension>]
    static member HistoryPosition(store: DsStore) : obj =
        (StoreEditorState.get store).UndoManager.CurrentPosition

    /// History 패널에서 position(HistoryPosition 값)이 몇 번째 항목인지. 0 = 초기 상태, 이력에서 사라졌으면 -1.
    [<Extension>]
    static member HistoryPanelIndexOf(store: DsStore, position: obj) : int =
        (StoreEditorState.get store).UndoManager.PanelIndexOf position

    [<Extension>]
    static member EmitAndHistory(store: DsStore, evt: EditorEvent) =
        StoreAuthoring.emitAndHistory store evt

    [<Extension>]
    static member EmitConnectionsChangedAndHistory(store: DsStore) =
        StoreAuthoring.emitConnectionsChangedAndHistory store

    [<Extension>]
    static member EmitRefreshAndHistory(store: DsStore) =
        StoreAuthoring.emitRefreshAndHistory store

    [<Extension>]
    static member EmitEntitiesMovedAndHistory(store: DsStore, ids: Guid list) =
        StoreAuthoring.emitEntitiesMovedAndHistory store ids

    [<Extension>]
    static member SetLastUndoLightEvent(store: DsStore, evt: EditorEvent) =
        StoreAuthoring.setLastUndoLightEvent store evt

    [<Extension>]
    static member WasLastUndoRedoLight(store: DsStore) =
        StoreAuthoring.wasLastUndoRedoLight store

    [<Extension>]
    static member ObserveEvents(store: DsStore) =
        StoreAuthoring.observeEvents store

    [<Extension>]
    static member AddedEntityIdOrNull(store: DsStore, evt: EditorEvent) : Nullable<Guid> =
        store.TryGetAddedEntityId(evt)
        |> Option.toNullable

    [<Extension>]
    static member TryGetAddedEntityId(_store: DsStore, evt: EditorEvent) : Guid option =
        StoreAuthoring.tryGetAddedEntityId evt

    [<Extension>]
    static member IsTreeStructuralEvent(_store: DsStore, evt: EditorEvent) =
        StoreAuthoring.isTreeStructuralEvent evt

    [<Extension>]
    static member Undo(store: DsStore) =
        StoreAuthoring.undo store

    [<Extension>]
    static member Redo(store: DsStore) =
        StoreAuthoring.redo store

    [<Extension>]
    static member UndoTo(store: DsStore, steps: int) =
        StoreAuthoring.undoTo store steps

    [<Extension>]
    static member RedoTo(store: DsStore, steps: int) =
        StoreAuthoring.redoTo store steps

    [<Extension>]
    static member TryGetUndoAffectedIds(store: DsStore, index: int) =
        (StoreEditorState.get store).UndoManager.TryGetUndoAffectedIds(index)

    [<Extension>]
    static member TryGetRedoAffectedIds(store: DsStore, index: int) =
        (StoreEditorState.get store).UndoManager.TryGetRedoAffectedIds(index)

    [<Extension>]
    static member MergeLastTransactions(store: DsStore, count: int, label: string) =
        let editorState = StoreEditorState.get store
        editorState.UndoManager.MergeTop(count, label)
        StoreAuthoring.emitHistoryChanged store

    [<Extension>]
    static member ClearHistory(store: DsStore) =
        StoreAuthoring.clearHistory store
