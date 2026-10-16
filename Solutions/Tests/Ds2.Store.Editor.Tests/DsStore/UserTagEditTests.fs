module Ds2.Store.Editor.Tests.DsStoreUserTagEditTests

open Xunit
open Ds2.Core.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

/// UserTag 편집은 실제로 바뀔 때만 History 에 남아야 한다(#230). 예전엔 편집 창에서 아무것도 안 바꾸고 확인해도,
/// 없는 인덱스를 지우려 해도 Undo 항목이 쌓였다 — 저장 안 됨 표시도 같이 켜졌다.
module UserTagEditHistoryTests =

    let private systemWithTag () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        store.AddUserTag(system.Id, "Tag1", "Info", "%MX0", "Bool", "Eq", "true") |> ignore
        let item = store.GetUserTagsForSystem(system.Id) |> List.head
        store, system.Id, item

    [<Fact>]
    let ``updating a user tag with the same values leaves no history entry`` () =
        let store, systemId, item = systemWithTag ()
        let before = store.HistoryPosition()

        let changed =
            store.UpdateUserTag(systemId, item.Index, item.Name, item.LogLevel, item.TagAddress,
                                item.ValueType, item.MatchOp, item.MatchValue)

        Assert.False(changed)
        Assert.Same(before, store.HistoryPosition())

    [<Fact>]
    let ``updating a user tag with a new value is recorded and undoable`` () =
        let store, systemId, item = systemWithTag ()
        let before = store.HistoryPosition()

        let changed =
            store.UpdateUserTag(systemId, item.Index, "Tag2", item.LogLevel, item.TagAddress,
                                item.ValueType, item.MatchOp, item.MatchValue)

        Assert.True(changed)
        Assert.NotSame(before, store.HistoryPosition())
        store.Undo()
        Assert.Equal("Tag1", (store.GetUserTagsForSystem(systemId) |> List.head).Name)

    [<Fact>]
    let ``removing a user tag at a missing index leaves no history entry`` () =
        let store, systemId, _ = systemWithTag ()
        let before = store.HistoryPosition()

        Assert.False(store.RemoveUserTag(systemId, 5))
        Assert.Same(before, store.HistoryPosition())

    /// 예전 형식으로 저장된 태그(칸 수가 적은 문자열)도 같은 값이면 수정으로 치지 않는다 — 글자로 비교하던 때는
    /// 다시 만든 문자열과 글자가 달라 아무것도 안 바꿔도 History 에 남았다.
    [<Fact>]
    let ``a tag stored in the legacy short form is not rewritten when updated with the same values`` () =
        let store = createStore ()
        let project = addProject store "P"
        let system = addSystem store "S" project.Id true
        // 예전 파일을 막 읽은 상태 — 4칸 문자열(조건·기준값 없음, 값 형식 표기 BOOL)
        (UserTagStore.ensureLoggingProps store.Systems.[system.Id]).UserTags.Add("Legacy|Info|%MX9|BOOL")
        let item = store.GetUserTagsForSystem(system.Id) |> List.head
        let before = store.HistoryPosition()

        let changed =
            store.UpdateUserTag(system.Id, item.Index, item.Name, item.LogLevel, item.TagAddress,
                                item.ValueType, item.MatchOp, item.MatchValue)

        Assert.False(changed)
        Assert.Same(before, store.HistoryPosition())
