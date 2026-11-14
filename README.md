# DK Easy Windows System Maintenance Tool

Windows 11 PC의 저장 공간, 시작 프로그램, 서비스, 예약 작업과 최근 오류를 한곳에서 점검하는 무료 도구입니다.

## 다운로드 및 실행

1. [GitHub Releases](https://github.com/danhk0612/DK-Easy-Windows-System-Maintenance-Tool/releases)에서 Windows x64 ZIP을 내려받습니다.
2. [Microsoft .NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)을 설치합니다. 이미 설치되어 있다면 다음 단계로 진행합니다.
3. ZIP을 폴더에 풀고 `DK.Easy.WindowsMaintenance.exe`를 실행합니다. 압축을 푼 파일들은 같은 폴더에 둡니다.

## 처음 사용하는 방법

**전체 검사**를 누르면 진행 중인 단계가 표시됩니다. 드라이브를 읽는 데 시간이 걸리면 **취소**할 수 있습니다. 특정 목록만 확인하려면 해당 화면의 **이 화면 검사**를 누르세요.

| 화면 | 할 수 있는 일 |
| --- | --- |
| 대시보드 | Windows 버전·빌드, 시스템 드라이브 사용량과 마지막 점검 확인 |
| 시스템 문제 | 시스템·응용 프로그램 이벤트와 Windows 오류 보고를 확인하고 TXT·JSON으로 내보내기 |
| 저장 공간 | 드라이브별 대용량 파일·폴더 및 사용자 임시파일 조회, 위치 열기, 허용 파일을 휴지통으로 이동 |
| 시작 프로그램 | 자동 실행 등록 위치 확인, 현재 사용자 64비트 Run 항목 비활성화·복원 |
| 서비스·예약 작업 | 상태 확인, 지원하는 항목에 한해 확인 후 변경 |
| 설치 프로그램 | 등록된 데스크톱 앱 확인, Windows의 설치된 앱 설정 열기 |
| 점검 기록 | 최근 12회 전체 검사와 직전 검사 대비 목록·용량 변화 확인 |

**설정**에서 창을 닫을 때 종료하거나 트레이로 숨기도록 선택할 수 있습니다. 이벤트 조회 기간, 경고 포함 여부, 큰 파일·폴더 기준도 조정할 수 있습니다.

## 알아두면 좋은 점

- 전체 검사는 시스템 설정을 변경하지 않습니다. 파일 이동과 시작 항목·서비스·예약 작업 변경은 대상 확인 후 실행됩니다. 서비스·예약 작업 변경에는 관리자 권한이 필요합니다.
- 시스템 문제 화면에는 최신 200건이 표시됩니다. 보고서에는 Windows가 기록한 장치 정보와 파일 경로가 포함될 수 있으니 공유 전에 내용을 확인하세요.
- 파일 이동은 사용자 프로필 내 허용된 파일과 사용자 임시파일에 적용됩니다. Windows 서비스와 Microsoft 예약 작업은 변경 대상에서 제외됩니다.
- 이 버전의 실기 검증 항목은 [Windows 실기 테스트](docs/MANUAL_TESTS.md)에 정리되어 있습니다. 문제를 발견하면 재현 순서와 앱 버전을 포함해 [GitHub Issues](https://github.com/danhk0612/DK-Easy-Windows-System-Maintenance-Tool/issues)에 남겨 주세요.

개발 및 빌드 안내는 [개발 문서](AGENTS.md), 검증 결과는 [진행 현황](docs/STATUS.md)에서 확인할 수 있습니다.

## 라이선스

[MIT License](LICENSE)
