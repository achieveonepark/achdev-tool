# AchDev Tool

Unity 빌드 실행과 AI 개발 보조를 한곳에서 처리하는 Tauri 데스크톱 앱입니다.
좌측 사이드바에서 **Build** / **AI** 카테고리를 전환하며 사용합니다.

## 기능

### 🔨 Build

- Android APK를 연결된 기기에 설치 (설치 후 자동 실행 옵션)
- iOS Xcode 프로젝트를 바로 열기
- WebGL 빌드를 로컬 서버로 실행

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

## 개발 실행

```bash
npm install
npm run tauri:dev
```

## 바로 설치 파일 받기

저장소에 바로 설치해볼 수 있는 빌드 결과도 함께 넣어두었습니다.

- macOS 설치 파일: [achdev-tool-0.1.0-macos-arm64.dmg](./artifacts/achdev-tool-0.1.0-macos-arm64.dmg)
- Windows 설치 파일: [achdev-tool-0.1.0-windows-x64-setup.exe](./artifacts/achdev-tool-0.1.0-windows-x64-setup.exe)

> 참고: 동봉된 Windows 설치 파일은 AI 카테고리가 추가되기 전 빌드입니다.
> 최신 기능을 반영하려면 Windows에서 직접 다시 빌드하세요(아래 참고).

## 설치 파일 빌드

이 프로젝트는 Tauri v2 기준입니다. 설치 파일은 빌드하는 운영체제에서 만드는 것이 가장 안정적입니다.

### 공통 준비

1. Node.js LTS 설치
2. Rust 설치
3. 터미널을 다시 열어서 `cargo`가 잡히는지 확인

```bash
node -v
npm -v
cargo -V
```

이 저장소의 Tauri 스크립트는 `rustup which cargo`를 이용해 `cargo` 경로를 자동으로 보정합니다. 그래도 직접 `cargo`를 써야 하거나 `cargo: command not found`가 나오면 아래를 확인하세요.

```bash
source "$HOME/.cargo/env"
```

`~/.cargo/env`가 없는 환경이라면 다음 명령으로 PATH를 직접 보정할 수 있습니다.

```bash
export PATH="$(dirname "$(rustup which cargo)"):$PATH"
```

### macOS에서 빌드

macOS에서는 Xcode 또는 최소한 Xcode Command Line Tools가 필요합니다.

```bash
xcode-select --install
npm install
npm run tauri:build:macos
```

생성 결과물:

- `src-tauri/target/release/bundle/macos/AchDev Tool.app`
- `src-tauri/target/release/bundle/dmg/AchDev Tool_<version>_<arch>.dmg`

설치 방법:

- `.app`은 바로 실행 가능
- `.dmg`는 열어서 앱을 `Applications`로 드래그

### Windows에서 빌드

Windows에서는 아래 준비가 필요합니다.

1. Microsoft C++ Build Tools 설치
2. 설치 중 `Desktop development with C++` 체크
3. WebView2 Runtime 확인
4. Rust를 `stable-msvc` 툴체인으로 설치

```powershell
rustup default stable-msvc
npm install
npm run tauri:build:windows
```

생성 결과물:

- `src-tauri/target/release/bundle/msi/*.msi`
- `src-tauri/target/release/bundle/nsis/*-setup.exe`

설치 방법:

- `.msi` 또는 `-setup.exe`를 실행해서 설치

### macOS에서 Windows NSIS 크로스 빌드

정식 권장 경로는 Windows에서 직접 빌드하는 방식입니다. 그래도 macOS에서 Windows용 설치 파일이 꼭 필요하면 NSIS만 크로스 빌드할 수 있습니다.

```bash
brew install nsis llvm
export PATH="/opt/homebrew/opt/llvm/bin:$PATH"
rustup target add x86_64-pc-windows-msvc
cargo install --locked cargo-xwin
npm install
npm run tauri:build:windows:cross
```

생성 결과물:

- `src-tauri/target/x86_64-pc-windows-msvc/release/bundle/nsis/*-setup.exe`

참고:

- macOS에서는 `msi`를 만들 수 없습니다.
- Windows 설치 파일은 Windows에서 직접 빌드하는 방식이 가장 안정적입니다.

## 빌드 산출물 위치

- 프런트엔드 정적 파일: `dist/`
- macOS 번들: `src-tauri/target/release/bundle/macos/`
- macOS DMG: `src-tauri/target/release/bundle/dmg/`
- Windows MSI: `src-tauri/target/release/bundle/msi/`
- Windows NSIS: `src-tauri/target/release/bundle/nsis/`

## 테스트

```bash
cd src-tauri && cargo test
```

## 서명 관련 참고

현재 설정은 로컬 설치와 테스트용 기준입니다.

- macOS: 코드 서명과 notarization을 하지 않으면 외부 배포 시 Gatekeeper 경고가 뜰 수 있습니다.
- Windows: 서명되지 않은 NSIS 설치 파일은 SmartScreen 경고가 뜰 수 있습니다.

## 참고 문서

- [Tauri Prerequisites](https://v2.tauri.app/ko/start/prerequisites/)
- [Tauri macOS Application Bundle](https://v2.tauri.app/distribute/macos-application-bundle/)
- [Tauri Windows Installer](https://v2.tauri.app/distribute/windows-installer/)
