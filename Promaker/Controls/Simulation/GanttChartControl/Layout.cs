using System;
using System.Collections.Generic;
using System.Linq;
using Promaker.ViewModels;

namespace Promaker.Controls;

public partial class GanttChartControl
{
    internal enum GanttSegmentRenderKind
    {
        Filled,
        VirtualAppendOutline,
        OutputAppendLine,
        OutputAppendEnd
    }

    internal readonly record struct GanttSegmentRenderPart(
        GanttSegmentRenderKind Kind,
        DateTime StartTime,
        DateTime EndTime);

    /// <summary>plan overlay 배경 바 구간 — Going 시작 시점부터 plan(BaseDurationMs) 만큼.</summary>
    internal readonly record struct GanttPlanOverlayPart(DateTime StartTime, DateTime EndTime);

    /// <summary>plan overlay (비-Simulation 모드) — Going 세그먼트의 plan 배경 바 구간. 대상 아니면 null.
    /// 통신 재개 후 첫 Going(SuppressPlanOverlay) 은 중간 합류라 시작점이 가짜 — 틀 생략.</summary>
    internal static GanttPlanOverlayPart? ResolvePlanOverlayPart(GanttTimelineEntry entry, GanttStateSegment segment)
    {
        if (segment.State != Ds2.Core.Status4.Going) return null;
        if (segment.SuppressPlanOverlay) return null;
        if (entry.BaseDurationMs is not { } durationMs || durationMs <= 0) return null;
        return new GanttPlanOverlayPart(segment.StartTime, segment.StartTime.AddMilliseconds(durationMs));
    }

    /// <summary>
    /// Work plan 틀의 동적 재예측 끝 시각 — 사이클 진행 중 이미 끝난 자식 Call 의 actual 을 반영해
    /// "남은 구간"만 예측한다 (내비의 도착 예정 시간 갱신과 동일).
    /// 고정 plan(중앙값)은 단계 전환 갭의 사이클별 지터가 직렬로 누적돼 수백 ms 잔차를 만들지만,
    /// 단계가 끝날 때마다 anchor 를 actual 로 옮기면 잔차가 누적되지 않고 마지막 한 단계 변동만 남는다.
    ///   틀 끝 = (진행 중 Call 의 Going 시작 + 그 Call plan | 마지막 완료 Call 의 actual 끝)
    ///         + 미시작 Call plan 합 + 남은 전환 갭(학습된 총 갭 × 남은 비율)
    /// 자식 Call 에 plan 이 없거나 자식이 없으면 null — 호출자가 고정 plan 으로 fallback.
    /// (직렬 체인 가정의 근사 — 병렬 Call 모델에선 남은 합이 과대일 수 있으나 고정 plan 보다 나쁘지 않다.)
    /// </summary>
    internal static DateTime? ResolveDynamicPlanEnd(
        GanttTimelineEntry workEntry,
        GanttStateSegment workGoing,
        IReadOnlyList<GanttTimelineEntry> allEntries,
        DateTime currentTime)
    {
        if (!workEntry.IsWork) return null;
        // 진행 중인 사이클만 — 완료된 사이클은 고정 plan(중앙값) 비교가 의미.
        // (완료 사이클에 적용하면 자식 탐색이 "그 이후 시작" 조건만으로 최신 사이클의
        //  자식을 오인해, 과거 틀이 현재까지 늘어나 겹치는 버그가 있었음.)
        if (workGoing.EndTime is not null) return null;
        if (workEntry.BaseDurationMs is not { } workPlanMs || workPlanMs <= 0) return null;

        var cycleStart = workGoing.StartTime;

        double childPlanTotal = 0, donePlan = 0, inProgressPlan = 0;
        int totalChildren = 0, doneChildren = 0;
        DateTime lastDoneEnd = cycleStart;
        DateTime? inProgressStart = null;

        foreach (var child in allEntries)
        {
            if (child.ParentWorkId != workEntry.Id || !child.IsCall) continue;
            if (child.BaseDurationMs is not { } childPlanMs || childPlanMs <= 0) return null;

            totalChildren++;
            childPlanTotal += childPlanMs;

            // 이 사이클의 Going 세그먼트 — 뒤에서부터 (장시간 누적 세그먼트 회피).
            GanttStateSegment? going = null;
            for (var i = child.Segments.Count - 1; i >= 0; i--)
            {
                var s = child.Segments[i];
                if (s.StartTime < cycleStart) break;
                if (s.State == Ds2.Core.Status4.Going) { going = s; break; }
            }

            if (going is null) continue;                  // 이 사이클에서 미시작
            if (going.EndTime is { } doneEnd)
            {
                doneChildren++;
                donePlan += childPlanMs;
                if (doneEnd > lastDoneEnd) lastDoneEnd = doneEnd;
            }
            else
            {
                inProgressStart = going.StartTime;
                inProgressPlan = childPlanMs;
            }
        }

        if (totalChildren == 0) return null;

        var gapTotal = Math.Max(0, workPlanMs - childPlanTotal);
        var remainingGap = gapTotal * (totalChildren - doneChildren) / totalChildren;
        var notStartedPlan = childPlanTotal - donePlan - inProgressPlan;

        return inProgressStart is { } inProgress
            ? inProgress.AddMilliseconds(inProgressPlan + notStartedPlan + remainingGap)
            : lastDoneEnd.AddMilliseconds(notStartedPlan + remainingGap);
    }

    /// <summary>plan overlay 모드에서 actual 상태 막대 높이 — 배경(plan) 바 안에 얇게 가운데.</summary>
    internal const double PlanOverlayActualBarHeight = 8;

    internal static double ResolveBarHeight(double rowHeight, bool planOverlay)
        => planOverlay ? PlanOverlayActualBarHeight : rowHeight - 4;

    /// <summary>열린 세그먼트의 렌더 끝 — 증거 cap(마지막 신호 시각)이 현재보다 과거면 거기까지만.
    /// 통신이 멎은 동안 열린 바가 빨간 선에 붙어 자라다 blackout 동결 때 되감기는 왜곡 방지.</summary>
    internal static DateTime ResolveOpenSegmentEnd(DateTime currentTime, DateTime? evidenceCap)
        => evidenceCap is { } cap && cap < currentTime ? cap : currentTime;

    internal static double ResolveBarTop(double rowY, double rowHeight, double barHeight)
        => rowY + (rowHeight - barHeight) / 2.0;

    internal static string ResolveRowBackgroundResourceKey(GanttTimelineEntry entry)
        => entry.IsWork ? "GanttWorkRowBackgroundBrush" : "GanttCallRowBackgroundBrush";

    internal static IReadOnlyList<GanttSegmentRenderPart> ResolveSegmentRenderParts(
        GanttTimelineEntry entry,
        GanttStateSegment segment,
        DateTime visibleEndTime)
    {
        if (visibleEndTime <= segment.StartTime) return [];

        var parts = new List<GanttSegmentRenderPart>(capacity: 4);
        var segmentEnd = visibleEndTime;
        var virtualAppendMs = entry.VirtualAppendMs;
        var hasVirtualAppend = virtualAppendMs > 0 && segment.State == Ds2.Core.Status4.Going;

        if (!hasVirtualAppend)
        {
            parts.Add(new GanttSegmentRenderPart(GanttSegmentRenderKind.Filled, segment.StartTime, segmentEnd));
        }
        else
        {
            var appendStart =
                entry.BaseDurationMs is { } durationMs
                    ? segment.StartTime.AddMilliseconds(durationMs)
                    : segment.EndTime is { } fixedEnd
                        ? fixedEnd.AddMilliseconds(-virtualAppendMs)
                        : segmentEnd;

            if (appendStart > segment.StartTime)
            {
                var filledEnd = appendStart < segmentEnd ? appendStart : segmentEnd;
                if (filledEnd > segment.StartTime)
                    parts.Add(new GanttSegmentRenderPart(GanttSegmentRenderKind.Filled, segment.StartTime, filledEnd));
            }

            if (segmentEnd > appendStart)
                parts.Add(new GanttSegmentRenderPart(GanttSegmentRenderKind.VirtualAppendOutline, appendStart, segmentEnd));
        }

        // timeAppend(출력 유지) 빨간 점선은 Call/Work 줄이 아니라 device I/O 줄(ApiCall 아래줄)에 그린다 → RenderApiCallSubRow 참고.

        return parts;
    }
}
