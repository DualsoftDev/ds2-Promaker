using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Promaker.Controls;

public partial class GanttChartControl
{
    // ── 엘리먼트 풀 (Children.Clear() 대신 재사용) ──
    private readonly List<Rectangle> _rowBgPool = new();
    private readonly List<Line> _rowLinePool = new();
    private readonly List<Rectangle> _barPool = new();
    private readonly List<Rectangle> _planBarPool = new();
    private readonly List<Rectangle> _shadowBarPool = new();
    private readonly List<Border> _virtualAppendPool = new();
    private readonly List<Line> _outputAppendLinePool = new();
    private readonly List<Line> _outputAppendEndPool = new();
    private readonly List<Line> _rulerTickPool = new();
    private readonly List<TextBlock> _rulerLabelPool = new();

    // ── 풀 헬퍼 ──

    private T GetOrCreate<T>(List<T> pool, Canvas target, int index, Func<T> factory, Action<T>? init = null)
        where T : UIElement
    {
        if (index < pool.Count)
        {
            pool[index].Visibility = Visibility.Visible;
            return pool[index];
        }
        var element = factory();
        init?.Invoke(element);
        pool.Add(element);
        target.Children.Add(element);
        return element;
    }

    private Rectangle GetOrCreateRowBg(int index)
        => GetOrCreate(_rowBgPool, TimelineCanvas, index, () => new Rectangle());

    private Line GetOrCreateRowLine(int index)
        => GetOrCreate(_rowLinePool, TimelineCanvas, index, () => new Line { StrokeThickness = 0.5 });

    private Rectangle GetOrCreateBar(int index)
        => GetOrCreate(_barPool, TimelineCanvas, index,
            () => new Rectangle { RadiusX = 2, RadiusY = 2, Cursor = Cursors.Hand },
            bar => { bar.MouseEnter += OnBarMouseEnter; bar.MouseLeave += OnBarMouseLeave; Panel.SetZIndex(bar, 10); });

    /// plan overlay 배경 바 — actual 바(ZIndex 10) 아래, 행 배경 위.
    private Rectangle GetOrCreatePlanBar(int index)
        => GetOrCreate(_planBarPool, TimelineCanvas, index,
            () => new Rectangle { RadiusX = 2, RadiusY = 2, Cursor = Cursors.Hand },
            bar => { bar.MouseEnter += OnBarMouseEnter; bar.MouseLeave += OnBarMouseLeave; Panel.SetZIndex(bar, 5); });

    /// shadow coast 추정 바 — 두절 구간의 가상 진행. 점선 외곽으로 "추정"임을 표시, plan 틀(5)보다 아래.
    private Rectangle GetOrCreateShadowBar(int index)
        => GetOrCreate(_shadowBarPool, TimelineCanvas, index,
            () => new Rectangle
            {
                RadiusX = 2, RadiusY = 2,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 }
            },
            bar => Panel.SetZIndex(bar, 4));

    private Border GetOrCreateVirtualAppend(int index)
        => GetOrCreate(_virtualAppendPool, TimelineCanvas, index,
            () => new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = ResolveVirtualAppendBorderThickness(),
                CornerRadius = ResolveVirtualAppendCornerRadius(),
                Cursor = Cursors.Hand
            },
            border => { border.MouseEnter += OnBarMouseEnter; border.MouseLeave += OnBarMouseLeave; });

    internal static CornerRadius ResolveVirtualAppendCornerRadius() => new(0, 2, 2, 0);

    internal static Thickness ResolveVirtualAppendBorderThickness() => new(0, 1.5, 1.5, 1.5);

    private Line GetOrCreateOutputAppendLine(int index)
        => GetOrCreate(_outputAppendLinePool, TimelineCanvas, index,
            () => new Line { StrokeThickness = 2, Cursor = Cursors.Hand, StrokeDashArray = new DoubleCollection { 5, 4 } },
            line => { line.MouseEnter += OnBarMouseEnter; line.MouseLeave += OnBarMouseLeave; Panel.SetZIndex(line, 50); });

    private Line GetOrCreateOutputAppendEnd(int index)
        => GetOrCreate(_outputAppendEndPool, TimelineCanvas, index,
            () => new Line { StrokeThickness = 2, Cursor = Cursors.Hand },
            line => { line.MouseEnter += OnBarMouseEnter; line.MouseLeave += OnBarMouseLeave; Panel.SetZIndex(line, 50); });

    private Line GetOrCreateRulerTick(int index)
        => GetOrCreate(_rulerTickPool, TimeRulerCanvas, index, () => new Line { StrokeThickness = 1 });

    private TextBlock GetOrCreateRulerLabel(int index)
        => GetOrCreate(_rulerLabelPool, TimeRulerCanvas, index, () => new TextBlock { FontSize = 9 });

    private static void HideRemaining<T>(List<T> pool, int activeCount) where T : UIElement
    {
        for (int i = activeCount; i < pool.Count; i++)
            pool[i].Visibility = Visibility.Collapsed;
    }

    /// 시뮬 리셋 시 풀 전체 정리
    internal void ClearPools()
    {
        _rowBgPool.Clear();
        _rowLinePool.Clear();
        _barPool.Clear();
        _planBarPool.Clear();
        _shadowBarPool.Clear();
        _virtualAppendPool.Clear();
        _outputAppendLinePool.Clear();
        _outputAppendEndPool.Clear();
        _rulerTickPool.Clear();
        _rulerLabelPool.Clear();
        TimelineCanvas.Children.Clear();
        TimeRulerCanvas.Children.Clear();
    }
}
