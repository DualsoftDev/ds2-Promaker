using System;
using System.Collections.ObjectModel;
using Ds2.Core;

namespace Promaker.ViewModels;

public partial class GanttChartState
{
    /// <summary>
    /// entry 당 보관할 최대 segment 수. VP/Control 같이 외부 신호로 장시간 가동되는 모드에서
    /// 무한 Add 시 OOM 으로 이어지므로 가장 오래된 segment 부터 잘라낸다.
    /// </summary>
    public const int MaxSegmentsPerEntry = 2000;

    public void UpdateNodeState(Guid nodeId, Status4 newState, DateTime timestamp)
    {
        var entry = FindEntry(nodeId);
        if (entry == null) return;

        ApplyStateSegment(entry, newState, timestamp);

        // Call 의 Plan 줄(=ApiCall 윗줄) = Call 수명(plan duration) 동기화.
        if (entry.IsCall)
        {
            foreach (var e in Entries)
                if (e.IsApiCall && e.ParentCallId == nodeId)
                    ApplyStateSegment(e, newState, timestamp);
        }

        CurrentTime = timestamp;
    }

    private void ApplyStateSegment(GanttTimelineEntry entry, Status4 newState, DateTime timestamp)
    {
        // Call/Plan 줄은 Going 구간만 막대로 그린다 (Ready/Finish/Homing 은 빈 구간).
        // R/G/F/H 전부 막대로 표기 (Ready 포함) — 색은 Status4Visuals.GanttBar{Ready=초록/Going=주황/Finish=파랑/Homing=회색}.
        bool shouldShowSegment = true;

        var lastSegment = entry.Segments.Count > 0 ? entry.Segments[^1] : null;
        if (lastSegment is { EndTime: null })
            lastSegment.EndTime = timestamp;

        if (shouldShowSegment)
        {
            entry.Segments.Add(new GanttStateSegment
            {
                State = newState,
                StartTime = timestamp,
                // 통신 재개 후 첫 Going = 사이클 중간 합류 — 시작점이 가짜라 plan 틀 생략.
                SuppressPlanOverlay = newState == Status4.Going && _suppressNextGoingPlan.Remove(entry.Id)
            });
            while (entry.Segments.Count > MaxSegmentsPerEntry)
                entry.Segments.RemoveAt(0);

            // shadow coast 행별 유추 복귀 — 이 행의 첫 실측 Going 이 곧 "위치를 다시 안" 시점.
            // 고스트는 행마다 자기 복귀까지 유지된다 (Call/Work/ApiCall PLN 줄 모두 이 경로).
            if (newState == Status4.Going)
                MarkShadowResumeForEntry(entry.Id, timestamp);
        }

        entry.CurrentState = newState;
    }

    /// <summary>통신 재개 — 전 행의 "다음 Going" plan 틀을 1회 생략 예약. 두절 후 첫 Going 은
    /// 실제 사이클 시작이 아니라 합류 시점에서 시작하므로(신호 유추 워밍업 합류와 같은 문제)
    /// 틀을 그리면 다음 사이클을 침범한다. 둘째 사이클부터 정상 틀.</summary>
    public void SuppressNextGoingPlanOverlay()
    {
        foreach (var e in Entries)
            _suppressNextGoingPlan.Add(e.Id);
    }

    private readonly HashSet<Guid> _suppressNextGoingPlan = new();

    /// <summary>실제 I/O — Tag(Out·In) 변화를 해당 ApiCall I/O 줄의 막대로. on=high 구간 시작, off=종료.</summary>
    public void UpdateIoState(string address, bool isOn, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(address)) return;
        foreach (var e in Entries)
        {
            if (!e.IsApiCall) continue;
            bool isOut = e.OutAddress == address;
            bool isIn = e.InAddress == address;
            if (!isOut && !isIn) continue;

            // Out/In 을 독립 리스트로 추적 — off 신호가 같은 종류(Out↔Out, In↔In)의 직전 high 구간만 닫는다.
            //   단일 리스트로 섞으면 Out off 가 In segment 를(또는 그 반대) 잘못 닫아 짧은 조각 + 빨간선 중복이
            //   생긴다(출력 유지 중 In 이 들어와 Out·In high 구간이 겹칠 수 있으므로).
            var segs = isOut ? e.OutSegments : e.InSegments;
            var last = segs.Count > 0 ? segs[^1] : null;
            if (last is { EndTime: null })
                last.EndTime = timestamp;

            if (isOn)
            {
                segs.Add(new GanttStateSegment
                {
                    State = isOut ? Status4.Going : Status4.Finish,
                    StartTime = timestamp
                });
                while (segs.Count > MaxSegmentsPerEntry)
                    segs.RemoveAt(0);
            }
        }
        CurrentTime = timestamp;
    }

    public void SyncNodeState(Guid nodeId, Status4 currentState, DateTime timestamp)
    {
        var entry = FindEntry(nodeId);
        if (entry == null) return;

        if (entry.CurrentState != currentState)
        {
            UpdateNodeState(nodeId, currentState, timestamp);
            return;
        }

        CurrentTime = timestamp;
    }

    /// <summary>abnormal 판정 — 해당 entry(Call 이면 자식 ApiCall 행 포함)의 최근 Going 사이클 바를
    /// 경고색으로 마킹. plan 틀 벗어남은 색을 바꾸지 않는다(참고) — 색이 바뀌는 건 판정뿐.</summary>
    public void MarkAbnormal(Guid nodeId)
    {
        var entry = FindEntry(nodeId);
        if (entry is null) return;
        MarkLatestGoingAbnormal(entry);
        if (entry.IsCall)
        {
            foreach (var e in Entries)
                if (e.IsApiCall && e.ParentCallId == nodeId)
                    MarkLatestGoingAbnormal(e);
        }
    }

    private static void MarkLatestGoingAbnormal(GanttTimelineEntry entry)
    {
        // 판정 시점의 사이클 = 가장 최근 Going (진행 중이거나 방금 닫힘 — SensorShort 는 Ready 중에도
        // 떨어질 수 있어 "직전 Going 사이클" 귀속이 자연스럽다).
        for (var i = entry.Segments.Count - 1; i >= 0; i--)
        {
            if (entry.Segments[i].State != Status4.Going) continue;
            entry.Segments[i].IsAbnormal = true;
            return;
        }
    }

    /// <summary>통신 blackout — 모든 행의 열린 세그먼트(상태·I/O)를 두절 시각으로 닫는다.
    /// 두절 구간은 증거가 없는데 진행 중 막대가 현재 시각까지 계속 늘어나면
    /// "마지막으로 알던 상태의 무한 연장" = 거짓 표시가 된다. actual 은 여기서 멈추고,
    /// plan 틀(예측)만 남는 것이 coast 구간의 올바른 시각 언어다.</summary>
    public void FreezeOpenSegments(DateTime at)
    {
        foreach (var entry in Entries)
        {
            CloseOpenSegment(entry.Segments, at);
            CloseOpenSegment(entry.OutSegments, at);
            CloseOpenSegment(entry.InSegments, at);
        }
    }

    private static void CloseOpenSegment(ObservableCollection<GanttStateSegment> segments, DateTime at)
    {
        if (segments.Count == 0) return;
        var last = segments[^1];
        if (last.EndTime is null && at > last.StartTime)
            last.EndTime = at;
    }
}
