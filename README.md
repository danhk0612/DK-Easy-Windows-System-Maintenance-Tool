# DK Easy Windows System Maintenance Tool

Windows 11 PC의 **정기점검 및 선택적 정리**를 위한 간단한 데스크톱 도구입니다.

> 원칙: **검사 → 결과 확인 → 사용자가 선택 → 변경**. 자동 최적화, 무분별한 레지스트리 청소, 시스템 튜닝은 하지 않습니다.

## 현재 사용 가능

- 대시보드에서 PC 이름, Windows 버전·빌드, 시스템 드라이브의 총·사용·남은 용량 확인.
- 전체 검사 진행 단계, 취소, 조회 실패 표시.
- 시스템 문제에서 Windows 시스템·응용 프로그램 로그의 심각·오류 및 Windows 오류 보고를 최신순 최대 200건 조회. 경고는 설정에서 선택.
- 이벤트 상세 열람과 UTF-8 TXT·JSON 내보내기. 보고서에는 발생 시각, 로그 채널, 원본, 이벤트 ID, 레코드 ID와 Windows가 제공하는 설명이 포함됩니다.
- 트레이 메뉴에서 창 열기, 전체 검사, 시스템 문제, 설정, 종료. 설정에서 닫기 동작, 최소화 동작, 앱 시작 시 검사와 로그 조회 범위를 지정합니다.
- 아래 목표 기능의 나머지 구현 상태는 [진행 현황](docs/STATUS.md)을 참고하세요.

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
- 배포: Windows x64, 실행 PC에 .NET 10 Desktop Runtime(x64) 설치 필요

Windows에서 .NET 10 SDK를 설치한 뒤 저장소 루트에서:

```powershell
dotnet build .\DK.Easy.WindowsMaintenance.slnx -c Debug
dotnet run --project .\src\DK.Easy.WindowsMaintenance\DK.Easy.WindowsMaintenance.csproj
```

릴리스 게시(기능 개발 및 검증 뒤 실행):

```powershell
dotnet publish .\src\DK.Easy.WindowsMaintenance\DK.Easy.WindowsMaintenance.csproj -c Release -r win-x64 --self-contained false
```

게시 폴더의 파일을 함께 보관한 뒤 `DK.Easy.WindowsMaintenance.exe`를 실행합니다. 이벤트 설명에 Windows가 기록한 경로나 장치 정보가 들어갈 수 있으므로 보고서를 공유하기 전에 내용을 확인하세요.

## 새 Work / Codex 작업 시작

1. [AGENTS.md](AGENTS.md)를 먼저 읽습니다.
2. [요구사항](docs/PRODUCT_SPEC.md), [개발 계획](docs/IMPLEMENTATION_PLAN.md), [진행 상태](docs/STATUS.md)를 확인합니다.
3. 현재 단계에서 **다음 미완료 작업 하나**만 구현합니다.
4. 변경 후 빌드·테스트 결과를 확인하고 진행 상태를 갱신합니다.

복사해 바로 사용할 작업 프롬프트: [docs/NEXT_AGENT_PROMPT.md](docs/NEXT_AGENT_PROMPT.md)

## 제외 기능

레지스트리 정리/최적화, Windows 핵심 서비스 튜닝, 드라이버·WinSxS 강제 삭제, 메모리/게임/네트워크 최적화, AI 진단, 상주 모니터링, 자동 예약 청소, 원격 관리.

라이선스는 아직 확정하지 않았습니다.

