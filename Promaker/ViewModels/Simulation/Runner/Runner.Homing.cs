using System;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    /// <summary>엔진의 자동 원위치 페이즈 완료 — Run 으로 넘어간다. STEP 으로 시작한 경우엔 Pause + STEP 재진입.</summary>
    private void OnHomingPhaseCompleted(object? sender, EventArgs e)
    {
        var engine = _simEngine;
        var generation = System.Threading.Interlocked.Read(ref _simUiGeneration);
        if (engine is not null)
            engine.HomingPhaseCompleted -= OnHomingPhaseCompleted;
        _dispatcher.BeginInvoke(() =>
        {
            // 다른 엔진 이벤트(Events.cs)와 같은 세대 검사 — STOP 직전에 큐에 들어간 콜백이 STOP 뒤에 실행되면 상태를
            // "실행 중" 으로 되돌리거나, 남은 STEP 표시로 시뮬을 다시 시작했다.
            if (!ReferenceEquals(_simEngine, engine)
                || System.Threading.Interlocked.Read(ref _simUiGeneration) != generation)
                return;
            IsHomingPhase = false;
            SimStatusText = SimText.Running;
            _setStatusText(SimText.Started);
            AddSimLog("시뮬레이션 자동 원위치 완료", LogSeverity.System);

            // STEP 으로 시뮬을 시작한 경우: Homing 완료 후 자동 PauseSimulation + STEP 재진입.
            if (_pendingFirstStepAfterStart)
            {
                _pendingFirstStepAfterStart = false;
                PauseSimulation();
                _ = StepSimulationAsync();
            }
        });
    }
}
