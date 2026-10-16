using System;
using Ds2.Core;

namespace Promaker.ViewModels;

/// <summary>shadow coast 템플릿의 Going 하나 — 사이클 시작 기준 offset 과 길이(ms).</summary>
public readonly record struct ShadowTemplateGoing(double OffsetMs, double DurationMs);

/// <summary>shadow coast 추정 막대(렌더 산출물) — entry 행에 그릴 절대 시각 구간.</summary>
public readonly record struct ShadowBar(Guid EntryId, DateTime StartTime, DateTime EndTime);

/// <summary>shadow coast reconcile 판정 — 재개 신호가 추정 위치 근방인지.</summary>
public readonly record struct ShadowReconcileResult(bool Joined, double ErrorMs, double ToleranceMs);

/// <summary>
/// 통신 두절(blackout) 한 구간의 shadow coast — actual 은 동결되지만 plan(추정)은 계속 가야 한다.
/// 직전 완료 사이클에서 entry 별 Going 타이밍 템플릿을 떠서 두절 구간에 주기 반복으로 투영한다
/// (내비 터널 구간의 추측 항법). 세그먼트 데이터에는 섞지 않고 렌더 레이어에서만 그린다 —
/// 추정이 실측 기록을 오염시키지 않는다.
/// </summary>
public class GanttShadowCoastWindow
{
    /// <summary>두절(freeze) 시각 — 추정 표시 시작.</summary>
    public DateTime StartTime { get; init; }
    /// <summary>전역 종료 시각 — null 이면 추정 진행 중(현재 시각까지 자람).
    /// 행별 종료는 <see cref="ResumeByEntry"/> 가 우선 — 모든 행이 복귀하면 여기도 닫힌다.</summary>
    public DateTime? EndTime { get; set; }
    /// <summary>행별 유추 복귀 시각 — 그 행의 첫 실측 Going. 고스트는 행마다 자기 복귀까지 유지
    /// (한 행의 복귀가 다른 행의 추정을 끊으면 늦게 복귀하는 행에 공백이 생긴다 — 실기 확인).</summary>
    public Dictionary<Guid, DateTime> ResumeByEntry { get; } = [];
    /// <summary>직전 완료 사이클 주기(연속 anchor Work Going 시작 간격).</summary>
    public double PeriodMs { get; init; }
    /// <summary>두절 시점 사이클의 시작(anchor Work 의 마지막 Going 시작) — 템플릿 k=0 위상 기준.</summary>
    public DateTime AnchorCycleStart { get; init; }
    /// <summary>entry 별 직전 완료 사이클의 Going 타이밍.</summary>
    public Dictionary<Guid, List<ShadowTemplateGoing>> Template { get; init; } = [];
    /// <summary>reconcile 불일치 — 재개 신호가 추정과 어긋남, 추정 구간을 미확정(흐리게) 강등.</summary>
    public bool LowConfidence { get; set; }
    /// <summary>재개 후 첫 Going 과 비교 완료 여부.</summary>
    public bool Reconciled { get; set; }
}

public partial class GanttChartState
{
    // ── shadow coast — 두절 구간 plan 추정 진행 (직전 완료 사이클 템플릿의 주기 타일링) ──

    /// <summary>두절 이력 — 닫힌 윈도우도 보관해 재개 후에도 소실 구간의 추정 틀이 남는다.</summary>
    public List<GanttShadowCoastWindow> ShadowWindows { get; } = [];

    /// <summary>주기로 인정할 최소값(ms) — 이보다 짧으면 사이클이 아니라 노이즈.</summary>
    internal const double ShadowMinPeriodMs = 100;

    /// <summary>
    /// 두절 진입 — 직전 완료 사이클에서 타이밍 템플릿을 떠 shadow coast 윈도우를 연다.
    /// anchor = 마지막 Going 시작이 가장 최근인 Work. 그 직전 Going 과의 시작 간격이 주기.
    /// 완료 사이클이 아직 없으면(첫 사이클 중 두절) null — 추정 근거가 없으니 공백이 정직하다.
    /// </summary>
    public GanttShadowCoastWindow? TryBeginShadowCoast(DateTime freezeAt)
    {
        // 직전 윈도우가 아직 열려 있으면(일부 행 미복귀 중 재두절) 여기서 전역 마감 —
        // 복귀했던 행의 actual 구간 위에 옛 추정이 다시 겹치지 않게 새 윈도우로 구간을 나눈다.
        if (ShadowWindows.Count > 0 && ShadowWindows[^1].EndTime is null)
            ShadowWindows[^1].EndTime = freezeAt > ShadowWindows[^1].StartTime
                ? freezeAt
                : ShadowWindows[^1].StartTime;

        GanttTimelineEntry? anchor = null;
        int anchorLastIdx = -1;
        foreach (var e in Entries)
        {
            if (!e.IsWork) continue;
            for (var i = e.Segments.Count - 1; i >= 0; i--)
            {
                if (e.Segments[i].State != Status4.Going) continue;
                if (anchorLastIdx < 0 || e.Segments[i].StartTime > anchor!.Segments[anchorLastIdx].StartTime)
                {
                    anchor = e;
                    anchorLastIdx = i;
                }
                break;
            }
        }
        if (anchor is null) return null;

        var anchorLast = anchor.Segments[anchorLastIdx];
        GanttStateSegment? prev = null;
        for (var i = anchorLastIdx - 1; i >= 0; i--)
        {
            if (anchor.Segments[i].State != Status4.Going) continue;
            prev = anchor.Segments[i];
            break;
        }
        if (prev is null) return null;   // 완료 사이클 없음

        var periodMs = (anchorLast.StartTime - prev.StartTime).TotalMilliseconds;
        if (periodMs < ShadowMinPeriodMs) return null;

        // 템플릿 — 직전 완료 사이클 [prev.Start, anchorLast.Start) 구간의 모든 entry Going.
        var template = new Dictionary<Guid, List<ShadowTemplateGoing>>();
        foreach (var e in Entries)
        {
            foreach (var s in e.Segments)
            {
                if (s.State != Status4.Going) continue;
                if (s.StartTime < prev.StartTime || s.StartTime >= anchorLast.StartTime) continue;
                var offset = (s.StartTime - prev.StartTime).TotalMilliseconds;
                var dur = ((s.EndTime ?? anchorLast.StartTime) - s.StartTime).TotalMilliseconds;
                if (dur <= 0) continue;
                if (!template.TryGetValue(e.Id, out var list))
                    template[e.Id] = list = [];
                list.Add(new ShadowTemplateGoing(offset, dur));
            }
        }
        if (template.Count == 0) return null;

        var window = new GanttShadowCoastWindow
        {
            StartTime = freezeAt,
            PeriodMs = periodMs,
            AnchorCycleStart = anchorLast.StartTime,
            Template = template
        };
        ShadowWindows.Add(window);
        return window;
    }

    /// <summary>신호 재개 — 열린 shadow 윈도우를 닫는다. 닫은 윈도우가 있으면 true(reconcile 대기 신호).</summary>
    public bool EndShadowCoast(DateTime at)
    {
        if (ShadowWindows.Count == 0) return false;
        var last = ShadowWindows[^1];
        if (last.EndTime is not null) return false;
        last.EndTime = at > last.StartTime ? at : last.StartTime;
        return true;
    }

    /// <summary>
    /// 재개 후 첫 실측 Going 을 shadow 추정 위치와 대조 — 신뢰 윈도우(끊김이 길수록 허용 오차 누적) 안이면
    /// 무에러 합류, 벗어나면 추정 구간을 미확정(LowConfidence)으로 강등한다.
    /// 해당 entry 가 템플릿에 없으면 null — 판정 보류(다음 Going 으로 재시도).
    /// </summary>
    public ShadowReconcileResult? TryReconcileShadowCoast(Guid entryId, DateTime actualGoingStart)
    {
        // 행별 복귀 정책에선 첫 실측 Going 시점에 윈도우가 아직 열려 있을 수 있다(미복귀 행 잔존) —
        // 미reconcile 이면 열림/닫힘 무관 대상. blackout 경과는 EndTime ?? actualGoingStart 로 계산.
        GanttShadowCoastWindow? window = null;
        for (var i = ShadowWindows.Count - 1; i >= 0; i--)
        {
            if (!ShadowWindows[i].Reconciled)
            {
                window = ShadowWindows[i];
                break;
            }
        }
        if (window is null) return null;
        if (!window.Template.TryGetValue(entryId, out var goings) || goings.Count == 0) return null;

        // 예측 Going 시작들(offset + k·P) 중 actual 에 가장 가까운 것과의 원형 거리.
        var bestErrorMs = double.MaxValue;
        var elapsedMs = (actualGoingStart - window.AnchorCycleStart).TotalMilliseconds;
        foreach (var g in goings)
        {
            var k = Math.Max(0, Math.Round((elapsedMs - g.OffsetMs) / window.PeriodMs));
            for (var kk = Math.Max(0, k - 1); kk <= k + 1; kk++)
            {
                var err = Math.Abs(elapsedMs - (g.OffsetMs + kk * window.PeriodMs));
                if (err < bestErrorMs) bestErrorMs = err;
            }
        }

        // 신뢰 윈도우 — 두절 1사이클당 주기의 5% 씩 허용 오차 누적(바닥 300ms).
        var blackoutMs = ((window.EndTime ?? actualGoingStart) - window.StartTime).TotalMilliseconds;
        var blackoutCycles = Math.Max(1.0, blackoutMs / window.PeriodMs);
        var toleranceMs = Math.Max(300.0, window.PeriodMs * 0.05 * blackoutCycles);

        window.Reconciled = true;
        var joined = bestErrorMs <= toleranceMs;
        window.LowConfidence = !joined;
        return new ShadowReconcileResult(joined, bestErrorMs, toleranceMs);
    }

    /// <summary>
    /// 렌더용 — 윈도우의 추정 막대를 절대 시각 구간으로 열거. 템플릿을 AnchorCycleStart 부터
    /// 주기 반복으로 투영하고 [윈도우 시작, min(행별 복귀, 전역 끝, until)] 로 클립한다.
    /// 행별 복귀(ResumeByEntry)가 우선 — 각 행의 고스트는 그 행이 다시 유추될 때까지 유지되고,
    /// 미복귀 행은 until(현재 시각)까지 실시간으로 자란다.
    /// </summary>
    public static IEnumerable<ShadowBar> EnumerateShadowBars(GanttShadowCoastWindow window, DateTime until)
    {
        var globalEnd = window.EndTime is { } closed && closed < until ? closed : until;
        if (globalEnd <= window.StartTime) yield break;

        foreach (var (entryId, goings) in window.Template)
        {
            var end = window.ResumeByEntry.TryGetValue(entryId, out var resumed) && resumed < globalEnd
                ? resumed
                : globalEnd;
            if (end <= window.StartTime) continue;

            foreach (var g in goings)
            {
                for (var k = 0; k < 10_000; k++)
                {
                    var start = window.AnchorCycleStart.AddMilliseconds(g.OffsetMs + k * window.PeriodMs);
                    if (start >= end) break;
                    var stop = start.AddMilliseconds(g.DurationMs);
                    var clipStart = start < window.StartTime ? window.StartTime : start;
                    var clipStop = stop > end ? end : stop;
                    if (clipStop > clipStart)
                        yield return new ShadowBar(entryId, clipStart, clipStop);
                }
            }
        }
    }

    /// <summary>열린 shadow 윈도우에 행별 유추 복귀 시각 기록(최초 1회) — 그 행의 고스트가 여기서 끝난다.
    /// 템플릿의 모든 행이 복귀하면 윈도우를 전역 마감한다(다음 두절이 새 윈도우를 열 수 있게).</summary>
    private void MarkShadowResumeForEntry(Guid entryId, DateTime at)
    {
        if (ShadowWindows.Count == 0) return;
        var window = ShadowWindows[^1];
        if (window.EndTime is not null) return;
        if (!window.Template.ContainsKey(entryId) || window.ResumeByEntry.ContainsKey(entryId)) return;

        window.ResumeByEntry[entryId] = at;
        if (window.ResumeByEntry.Count >= window.Template.Count)
        {
            var last = window.StartTime;
            foreach (var t in window.ResumeByEntry.Values)
                if (t > last) last = t;
            window.EndTime = last;
        }
    }
}
