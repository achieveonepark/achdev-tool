# AchDev Tool

Unity 빌드 실행과 AI 개발 보조, GitHub 저장소 관리를 한곳에서 처리하는 Avalonia 데스크톱 앱입니다.
좌측 사이드바에서 **Build** / **AI** / **Git** 카테고리를 전환하며 사용합니다.

## 기능

### 🔨 Build

- Android APK를 연결된 기기에 설치 (설치 후 자동 실행 옵션)
- iOS Xcode 프로젝트를 바로 열기 (macOS 전용)
- WebGL 빌드를 앱 내장 로컬 서버로 실행 (Python 불필요)

빌드 경로를 한 번 지정하면 `Android` / `iOS` / `WebGL` 하위 폴더를 자동으로 스캔해
앱 이름·아이콘과 함께 카드로 보여줍니다.

### 🤖 AI

- 설치된 AI CLI(`claude`, `opencode`, `codex`) 자동 감지
- 폴더를 VSCode로 열면서, 통합 터미널에서 선택한 CLI를 자동 실행
  (`.vscode/tasks.json`에 folderOpen 태스크를 병합하고 VSCode의
  `task.allowAutomaticTasks`를 한 번 켜줍니다)
- 각 도구의 설정 파일 열기 (없으면 생성)
- 미설치 도구를 `npm i -g`로 설치
- 도구별 MCP 서버 목록 확인 및 등록

### ⌘ Git

- 선택한 폴더 아래의 GitHub 저장소를 재귀적으로 탐색
- 로컬 변경사항이 없고 추적 브랜치가 있는 저장소만 안전하게 일괄 업데이트 (`git pull --ff-only`)

## 기술 스택

- **UI**: [Avalonia UI](https://avaloniaui.net/) 12 (.NET 10, MVVM + CommunityToolkit.Mvvm)
- **설치 파일**: [Velopack](https://velopack.io/) — `dotnet publish` 결과물만으로 Windows/macOS
  설치 파일과 자동 업데이트 패키지를 생성합니다.

## 개발 실행

```bash
dotnet run --project src/AchDevTool/AchDevTool.csproj
```

## 테스트

```bash
dotnet test
```

## 설치 파일 빌드

설치 파일은 **빌드하는 운영체제에서 만드는 것이 가장 안정적**입니다 (Velopack 자체 제약).
아래 스크립트가 `dotnet publish` → `vpk pack`(Velopack CLI) → (macOS는) `.dmg` 변환까지 자동으로 처리합니다.
버전은 `src/AchDevTool/AchDevTool.csproj`의 `<Version>` 값(현재 `1.0.0`)을 그대로 사용합니다.

### 공통 준비

1. [.NET SDK 10](https://dotnet.microsoft.com/download) 설치
2. `dotnet --version` 으로 설치 확인

Velopack CLI(`vpk`)는 빌드 스크립트가 없으면 자동으로 설치합니다
(수동 설치: `dotnet tool install -g vpk`).

### Windows에서 빌드

```powershell
pwsh ./build/build-windows.ps1
```

생성 결과물 (`releases/win-x64/`):

- `AchDevTool-<version>-win-Setup.exe` — 더블클릭으로 설치되는 설치 파일
- `AchDevTool-<version>-win-full.nupkg` — 자동 업데이트에 사용되는 패키지

### macOS에서 빌드

Xcode Command Line Tools가 필요합니다 (`xcode-select --install`).

```bash
./build/build-macos.sh          # Apple Silicon (arm64), 기본값
./build/build-macos.sh x64      # Intel Mac
```

생성 결과물 (`releases/osx-arm64/` 또는 `releases/osx-x64/`):

- `AchDevTool-<version>-<rid>.dmg` — 열어서 앱을 `Applications`로 드래그하는 설치 파일
- `AchDevTool-Portable.zip` — 포터블 `.app` (자동 업데이트에도 사용)

설치 방법:

- `.dmg`를 열어서 앱을 `Applications`로 드래그
- 또는 `.zip`을 풀어서 `.app`을 바로 실행

## 빌드 산출물 위치

- 퍼블리시 결과물: `publish/<rid>/`
- 설치 파일: `releases/<rid>/`

## 자동 배포

`main` 브랜치에 푸시하면 `.github/workflows/release.yml`이 Windows/macOS 설치 파일을 빌드해
GitHub Release(`app-v<version>`)에 자동으로 올립니다.

## 서명 관련 참고

현재 설정은 로컬 설치와 테스트용 기준입니다.

- macOS: 코드 서명과 notarization을 하지 않으면 외부 배포 시 Gatekeeper 경고가 뜰 수 있습니다.
- Windows: 서명되지 않은 설치 파일은 SmartScreen 경고가 뜰 수 있습니다.

## 참고 문서

- [Avalonia UI Docs](https://docs.avaloniaui.net/)
- [Velopack Docs](https://docs.velopack.io/)
