using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;
using Ds2.Runtime.IO;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    [RelayCommand(CanExecute = nameof(CanStartSimulation))]
    private void StartSimulation()
    {
        if (IsSimulating && IsSimPaused)
        {
            _simEngine?.SetAllFlowStates(FlowTag.Ready);
            _simEngine?.Resume();
            _isStepMode = false;
            SimStatusText = SimText.Running;
            ApplySimulationUiState(
                ganttRunning: true,
                isSimPaused: false,
                statusText: SimText.Resumed);
            return;
        }

        try
        {
            var index = SimIndexModule.build(Store, 10);

            // 토큰 역할이 설정되어 있으면 PLAY 전 자동 검증.
            var hasPreStartWarnings = false;
            if (HasAnyTokenRole(index))
            {
                var sections = RunGraphValidation(index);
                if (sections.Count > 0)
                {
                    hasPreStartWarnings = true;
                    AddGraphWarningLogs(sections);
                    Dialogs.DialogHelpers.ShowGraphWarnings(sections);
                    _setStatusText($"모델 검증: {sections.Count}건의 경고 발견");
                }
            }

            // v10 §12 — ApiDef/ApiCall invariant 점검. Error 면 시뮬 시작 중단, Warning 은 로그.
            // 기준은 파일 열기와 같은 오프라인 정책(V1/V2 면제) — Services.V10OfflinePolicy.
            var v10 = Services.V10OfflinePolicy.Evaluate(Store);
            var v10Errors = v10.Errors;
            foreach (var w in v10.Warnings)
                AddSimLog(Services.V10OfflinePolicy.Format(w), LogSeverity.Warn);
            if (v10Errors.Count > 0)
            {
                foreach (var e in v10Errors)
                    AddSimLog(Services.V10OfflinePolicy.Format(e), LogSeverity.Error);
                _setStatusText($"v10 모델 검증 실패: Error {v10Errors.Count}건 — 시뮬 시작 중단");
                return;
            }

            // Race Condition 경고: 순서 없는 Call이 같은 Device의 ResetReset 관계 Work를 참조
            var raceWarnings = GraphWarningProjection.findRaceConditionWarnings(index);
            if (raceWarnings.Length > 0)
            {
                hasPreStartWarnings = true;
                AddSimLog($"[WARN] Race Condition: 순서 없는 Call {raceWarnings.Length}쌍이 동일 Device ResetReset 관계 — 먼저 스케줄된 Call만 실행됩니다", LogSeverity.Warn);
            }

            if (!TryDisposeCurrentEngine("Simulation restart"))
                return;

            _simEngine = new EventDrivenEngine(index, RuntimeMode.Simulation);
            if (SimSpeed <= 0)
                SimSpeed = 1.0;
            SimTimeIgnore = false;
            _simEngine.SpeedMultiplier = SimSpeed;
            _simEngine.TimeIgnore = false;

            AdvanceSimUiGeneration();

            WireSimEvents();
            InitSimNodes();
            InitTokenSources();
            InitSceneEventHandler();

            _simStartTime = DateTime.Now;
            Report.Clear();
            _suppressedWarnings.Clear();
            _stepPrimingDone = false;

            GanttChart.Reset(_simStartTime);
            InitGanttEntries();
            GanttChart.IsRunning = true;

            if (!hasPreStartWarnings)
                _warningGuids.Clear();

            // PLAY 는 곧 자동 원위치 — Homing 페이즈가 있으면 그 완료 후 Run 으로 넘어간다.
            _simEngine.HomingPhaseCompleted += OnHomingPhaseCompleted;
            var hasHoming = _simEngine.StartWithHomingPhase();
            if (hasHoming)
            {
                IsHomingPhase = true;
                _setStatusText("시뮬레이션 초기화 중...");
                SimStatusText = "시뮬레이션 초기화 중...";
            }
            else
                _simEngine.HomingPhaseCompleted -= OnHomingPhaseCompleted;

            ApplySimStateToCanvas();
            ApplyWarningsToCanvas();

            ApplySimulationUiState(
                ganttRunning: true,
                isSimulating: true,
                isSimPaused: false,
                statusText: hasHoming ? "시뮬레이션 초기화 중..." : SimText.Started,
                logText: hasHoming ? "시뮬레이션 자동 원위치 진행 중" : SimText.Started);
            if (!hasHoming)
                SimStatusText = SimText.Running;
        }
        catch (Exception ex)
        {
            SimLog.Error("Simulation start failed", ex);
            _setStatusText(SimText.SimulationError(ex.Message));
        }
    }

    private bool CanStartSimulation() =>
        SimulationCommandFacade.IsAccepted(
            SimulationCommandFacade.DecideStart(IsSimulating, IsSimPaused, IsHomingPhase));
}
