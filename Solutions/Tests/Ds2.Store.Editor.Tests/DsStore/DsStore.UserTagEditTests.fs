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
