using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Microsoft.FSharp.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private bool _pendingFirstStepAfterStart;
    private bool _stepPrimingDone;

    [RelayCommand(CanExecute = nameof(CanStepSimulation))]
    private async Task StepSimulationAsync()
    {
        // 시뮬 시작 안 한 상태면 STEP 으로 Start.
        if (!IsSimulating)
        {
            // Homing 페이즈가 있는 모델이면 OnHomingPhaseCompleted 가 끝난 후 자동으로
            // PauseSimulation + STEP 재호출 하도록 flag set. (polling/Sleep 안 씀)
            _pendingFirstStepAfterStart = true;
            StartSimulation();
            if (_simEngine is null)
            {
                _pendingFirstStepAfterStart = false;
                return;
            }
            if (IsHomingPhase)
                return;  // OnHomingPhaseCompleted 가 이어서 처리

            _pendingFirstStepAfterStart = false;
            PauseSimulation();
        }

        if (_simEngine is null) return;
        var engine = _simEngine;
        SimStatusText = SimText.StepMode;
        // STEP 이 도는 동안 PLAY·RESET 을 막는다(CanStart/CanReset 이 StepSimulationCommand.IsRunning 을 본다).
        NotifyStepRunningChanged();

        var (selectedSourceGuid, autoStartSources) = GetStepAdvanceSelection();

        // Source priming — *첫 STEP* 에만 호출. 이후 STEP 에선 자연 cascade/cycle 따라감.
        // 매 STEP 호출 시 NewFlow.NewWork 가 cycle 끝나서 Ready 로 돌아오는 시점마다 다시
        // ForceWorkState(Going) 호출되면 NewFlow1 안 끝났는데 NewFlow 가 또 시작하는 race 발생.
        if (!_stepPrimingDone)
        {
            var action = StepPrimingPlan.decide(
                FuncConvert.FromFunc<Guid, bool>(g => SimIndexModule.isTokenSource(engine.Index, g)),
                FuncConvert.FromFunc<Guid, bool>(g => engine.GetWorkToken(g) is not null),
                FuncConvert.FromFunc<Guid, FSharpOption<Status4>>(g => engine.GetWorkState(g)),
                selectedSourceGuid,
                autoStartSources);

            ApplyStepPrimingAction(engine, action);
            _stepPrimingDone = true;
        }

        Guid[] batch;

        try
        {
            batch = engine.BeginStepBatch(selectedSourceGuid, autoStartSources);
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation step failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
            return;
        }

        var progressed = false;

        // batch 의 unit 모두 finish 까지 단계적 advance + wait.
        // 각 nextEventTime 까지 real-time wait 후 그 시점에 AdvanceSimulationTo 호출 →
        // 이벤트들이 그 시점에 발화 → GanttChart 가 시간선 따라 표시 (점프 X).
        // step 진행 중에는 interpolator 가 paused 상태에서도 보간하도록 stepping flag 켠다.
        _clockInterpolator.IsStepping = true;
        _clockInterpolator.ResetBase();
        GanttChart.IsRunning = true;
        var engineReplaced = false;
        try
        {
            var guard = 0;
            while (engine.IsStepBatchActive(batch) && guard < 256)
            {
                guard++;
                var nextEventTime = engine.NextEventTimeMs;
                if (nextEventTime is null) break;

                var simDelta = nextEventTime.Value - engine.CurrentTimeMs;
                if (simDelta > 0)
                {
                    var speed = Math.Max(0.000001, engine.SpeedMultiplier);
                    var realWaitMs = (int)Math.Ceiling(simDelta / speed);
                    if (realWaitMs > 0)
                    {
                        await Task.Delay(realWaitMs);
                        // 기다리는 사이 엔진이 바뀌었으면(파일 열기·새 프로젝트·STOP 후 PLAY) 이 STEP 은 끝난 것이다 —
                        // 해제된 엔진을 계속 쓰면 EndStep 이 ObjectDisposedException 으로 앱을 종료시켰다.
                        if (!ReferenceEquals(_simEngine, engine)) { engineReplaced = true; break; }
                    }
                }
                engine.AdvanceSimulationTo(nextEventTime.Value);
                progressed = true;
            }
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation step advance failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
        }
        finally
        {
            _clockInterpolator.IsStepping = false;
            if (!engineReplaced && ReferenceEquals(_simEngine, engine))
            {
                try
                {
                    engine.EndStep();
                }
                catch (Exception ex)
                {
                    SimLog.Error("Simulation step end failed", ex);
                    _setStatusText(SimText.SimulationError(ex.Message));
                }
                // CurrentTime 을 sim clock 끝 시점으로 *먼저* set (= Going 끝 시점).
                // IsRunning=false 가 먼저 가면 타이머 즉시 정지 → 마지막 RenderAll 이 advance 직전 보간 값에서 멈춤.
                GanttChart.CurrentTime = ToGanttTimestamp(engine.State.Clock);
                GanttChart.IsRunning = false;
            }
            NotifyStepRunningChanged();
        }

        // 엔진이 바뀐 뒤면 새 엔진의 화면 상태를 건드리지 않는다.
        if (engineReplaced || !ReferenceEquals(_simEngine, engine))
            return;

        AddSimLog(progressed ? "STEP 실행" : "STEP 진행 없음", LogSeverity.System);
        RefreshSimulationProgressUi();
        SimStatusText = SimText.Paused;
    }

    /// <summary>STEP 진행 여부가 바뀌었음을 PLAY·RESET 버튼에 알린다.</summary>
    private void NotifyStepRunningChanged()
    {
        StartSimulationCommand.NotifyCanExecuteChanged();
        ResetSimulationCommand.NotifyCanExecuteChanged();
    }

    private bool CanStepSimulation() =>
        SimulationCommandFacade.IsAccepted(
            SimulationCommandFacade.DecideStep(
                IsSimulating, IsSimPaused, IsHomingPhase, RuntimeMode.Simulation));

    private static void ApplyStepPrimingAction(ISimulationEngine engine, StepPrimingAction action)
    {
        switch (action)
        {
            case var _ when action.IsStartAllSourcesWithoutToken:
                foreach (var sg in engine.Index.TokenSourceGuids)
                    if (engine.GetWorkToken(sg) is null)
                        engine.StartSourceWork(sg);
                break;
            case StepPrimingAction.StartSelectedSource s:
                if (engine.GetWorkToken(s.Item) is null)
                    engine.StartSourceWork(s.Item);
                break;
            case StepPrimingAction.ForceSelectedReadyToGoing f:
                engine.ForceWorkState(f.Item, Status4.Going);
                break;
            // NoAction: no-op
        }
    }
}
