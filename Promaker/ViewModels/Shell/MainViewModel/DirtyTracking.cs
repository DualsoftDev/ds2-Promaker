using Ds2.Editor;

namespace Promaker.ViewModels;

/// <summary>
/// "저장 안 됨"(IsDirty) 판정. 저장(또는 열기) 시점의 편집 이력 위치를 기억해 두고 현재 위치와 비교한다 — 예전엔 undo 개수로
/// 판정해, 저장 후 되돌리면 "저장됨" 으로 보였고(되돌린 상태가 저장 확인 없이 사라짐), 이력 밖 변경(PLC 접속 저장 등)은
/// 다음 이력 갱신 때 표시가 꺼졌다.
/// </summary>
public partial class MainViewModel
{
    /// <summary>저장·열기 시점의 편집 이력 위치(DsStore.HistoryPosition). 현재 위치와 같으면 저장된 상태다.</summary>
    private object? _savedHistoryPosition;

    /// <summary>편집 이력 밖에서 바뀐 모델 데이터(PLC 접속, 태그 마법사 템플릿, 시뮬 결과 선택)나 아직 어디에도 저장되지 않은
    /// 새 문서(CSV 가져오기·샘플). 저장하거나 다른 파일을 열 때만 지운다.</summary>
    private bool _hasUnsavedNonHistoryChanges;

    /// <summary>현재 상태를 "저장됨" 으로 기록 — 저장 완료, 파일 열기 완료, 새 프로젝트.</summary>
    private void MarkSavedState()
    {
        _savedHistoryPosition = _store.HistoryPosition();
        _hasUnsavedNonHistoryChanges = false;
        IsDirty = false;
    }

    /// <summary>편집 이력에 남지 않는 모델 변경을 표시한다.</summary>
    internal void MarkNonHistoryChange()
    {
        _hasUnsavedNonHistoryChanges = true;
        IsDirty = true;
    }

    /// <summary>열기 직후 이력을 비운 시점을 저장 기준으로 다시 잡는다 — 이력 밖 변경 표시(새 문서)는 유지한다.</summary>
    private void RebaseSavedHistoryPosition()
    {
        _savedHistoryPosition = _store.HistoryPosition();
        RecomputeDirtyFromHistory();
    }

    /// <summary>편집 이력이 바뀔 때마다 다시 계산.</summary>
    private void RecomputeDirtyFromHistory() =>
        IsDirty = _hasUnsavedNonHistoryChanges
                  || !ReferenceEquals(_store.HistoryPosition(), _savedHistoryPosition);
}
