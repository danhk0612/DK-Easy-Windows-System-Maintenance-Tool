# DK Easy Windows System Maintenance Tool

Windows 11 PC의 **정기점검 및 선택적 정리**를 위한 간단한 데스크톱 도구입니다.

> 원칙: **검사 → 결과 확인 → 사용자가 선택 → 변경**. 자동 최적화, 무분별한 레지스트리 청소, 시스템 튜닝은 하지 않습니다.

## 현재 상태 (초기 프로젝트)

- **M0 초기 문서 및 WPF 골격 준비 단계**입니다.
- 실제 시스템 검사, 삭제, 서비스 변경 기능은 아직 구현되지 않았습니다.
- 개발 범위를 확정한 뒤 작은 단계로 구현합니다. 완료되지 않은 기능을 작동하는 것처럼 보여주지 않습니다.

## 목표 기능

| 영역 | 확인 | 사용자 선택 작업 |
| --- | --- | --- |
| 대시보드 | OS/디스크 요약, 마지막 점검, 확인 필요 항목 | 전체 검사 |
| 저장 공간 | 큰 파일·폴더, 임시파일 | 위치 열기, 안전한 선택 정리 |
| 시작 프로그램 | 자동 실행 항목, 등록 위치, 실행 대상 | 활성/비활성 |
| 서비스 | 타사 서비스 상태, 시작 유형 | 시작/중지, 시작 유형 변경 |
| 예약 작업 | 타사 작업·실행 대상 | 활성/비활성 |
| 설치 프로그램 | 설치 목록, 경로 | 시스템 제공 제거 도구 실행 |
| 점검 기록 | 직전 점검 대비 변화, 최근 기록 | 결과 열람 |

모든 시스템 변경은 **명시적 사용자 확인** 뒤에만 수행합니다.

## 기술 및 빌드

- Windows 11 x64
- C# / .NET 10 / WPF
- 한국어 UI, 기본적으로 외부 서버·DB·계정 없음
- 설정 및 점검 기록은 로컬 JSON
- 목표 배포: self-contained win-x64, 가능한 한 단일 EXE (실제 게시 결과 확인 필요)

Windows에서 .NET 10 SDK를 설치한 뒤 저장소 루트에서:

```powershell
dotnet build .\DK.Easy.WindowsMaintenance.slnx -c Debug
dotnet run --project .\src\DK.Easy.WindowsMaintenance\DK.Easy.WindowsMaintenance.csproj
```

릴리스 게시(기능 개발 및 검증 뒤 실행):

```powershell
dotnet publish .\src\DK.Easy.WindowsMaintenance\DK.Easy.WindowsMaintenance.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

**주의:** 초기 스캐폴드는 실제 Windows 환경에서 빌드/실행 검증해야 합니다. 실행 파일이 만들어졌다고 해서 시스템 변경 기능이 검증된 것은 아닙니다.

## 새 Work / Codex 작업 시작

1. [AGENTS.md](AGENTS.md)를 먼저 읽습니다.
2. [요구사항](docs/PRODUCT_SPEC.md), [개발 계획](docs/IMPLEMENTATION_PLAN.md), [진행 상태](docs/STATUS.md)를 확인합니다.
3. 현재 단계에서 **다음 미완료 작업 하나**만 구현합니다.
4. 변경 후 빌드·테스트 결과를 확인하고 진행 상태를 갱신합니다.

복사해 바로 사용할 작업 프롬프트: [docs/NEXT_AGENT_PROMPT.md](docs/NEXT_AGENT_PROMPT.md)

## 제외 기능

레지스트리 정리/최적화, Windows 핵심 서비스 튜닝, 드라이버·WinSxS 강제 삭제, 메모리/게임/네트워크 최적화, AI 진단, 상주 모니터링, 자동 예약 청소, 원격 관리.

라이선스는 아직 확정하지 않았습니다.
