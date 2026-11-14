# 새 GPT Work / Codex에 전달할 작업 지시

대상 저장소: https://github.com/danhk0612/DK-Easy-Windows-System-Maintenance-Tool

이 프로젝트는 Windows 11 전용 **종합 시스템 정기점검 및 선택 관리 툴**이다. 이전 채팅 내용은 필요 없다. 이 저장소의 `AGENTS.md`, `README.md`, `docs/PRODUCT_SPEC.md`, `docs/DECISIONS.md`, `docs/IMPLEMENTATION_PLAN.md`, `docs/STATUS.md`를 먼저 읽고 실제 코드를 확인하라.

**첫 작업:** `docs/STATUS.md`에 있는 다음 미완료 작업부터 진행하라. 현재 구현 상태와 실기 미검증 항목을 확인하고 **M9 실기 검증**부터 진행한다.

원칙:
- C#/.NET 10/WPF/Windows x64/한국어 UI 유지.
- 기존 코드·기능을 훼손하지 않고 **요청한 현재 단계만** 구현.
- 자동 청소/레지스트리 정리/서비스 일괄 최적화/상주 모니터링을 추가하지 말 것.
- 스캔은 무조건 읽기 전용, 변경은 사용자의 개별 선택과 명확한 확인 이후.
- 가짜 측정값, 구현되지 않은 버튼의 성공 표시, 검증되지 않은 안전성 주장 금지.
- Windows에서 빌드·테스트하고, 불가능하면 결과를 '미검증'으로 남길 것.
- 완료 시 `docs/STATUS.md` 업데이트, 수정 파일/테스트/다음 작업을 보고할 것.
- 과도한 아키텍처·외부 패키지·기능 확장 없이 작게 완성하라.
