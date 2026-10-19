using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Controls;
using Promaker.ViewModels;
using Xunit;

namespace Promaker.Tests.PropertyPanel;

/// <summary>
/// 속성 패널의 사용자 태그 목록(#252). System 하나에 태그가 천 개 넘게 있는 현장 모델에서 System 을 누를 때마다
/// 목록이 줄을 전부 만들어 수 초 멈췄다 — 보이는 줄만 만들어야 한다.
/// </summary>
public sealed class UserTagsListTests
{
    [Fact]
    public void Selecting_a_system_with_many_user_tags_creates_only_visible_rows()
    {
        StaTestRunner.Run(() =>
        {
            var vm = new MainViewModel();
            vm.NewProjectCommand.Execute(null);
            StaTestRunner.PumpPendingUi();
            var store = TestReflection.Get<DsStore>(vm, "_store");
            var projectId = Queries.allProjects(store).Head.Id;
            var systemId = Queries.activeSystemsOf(projectId, store).Head.Id;
            var tags = Enumerable.Range(0, 1000)
                .Select(i => ($"Tag{i}", "Error", $"%MX{i}", "Bit", "RisingEdge", ""))
                .ToList();
            store.AddUserTagsBatch(systemId, tags);
            StaTestRunner.PumpPendingUi();

            var systemNode = Flatten(vm.ControlTreeRoots).First(n => n.Id == systemId);
            vm.Selection.SelectNodeFromTree(systemNode, false, false);
            StaTestRunner.PumpPendingUi();
            Assert.Equal(1000, vm.PropertyPanel.UserTags.Count);

            var section = new UserTagsSectionControl { DataContext = vm.PropertyPanel };
            section.Measure(new Size(320, 600));
            section.Arrange(new Rect(0, 0, 320, 600));
            section.UpdateLayout();

            var rows = FindDescendant<VirtualizingStackPanel>(section.UserTagList);
            Assert.NotNull(rows);
            Assert.InRange(VisualTreeHelper.GetChildrenCount(rows), 1, 30);
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private static IEnumerable<EntityNode> Flatten(IEnumerable<EntityNode> roots)
    {
        foreach (var n in roots)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }
}
