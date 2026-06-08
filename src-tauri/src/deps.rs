// 외부 CLI 의존성(예: libimobiledevice)에 대한 공통 처리.
//
// 어떤 기능이 외부 CLI 를 필요로 할 때, 그 도구가 설치돼 있는지 확인하고
// 없으면 프런트엔드에서 "설치할까요?" 팝업을 띄운 뒤 OS 에 맞는 패키지
// 매니저로 설치를 진행하는 공통 로직입니다. 새 도구는 DEPS 에 한 줄 추가만
// 하면 동일한 팝업/설치 흐름을 그대로 재사용할 수 있습니다.

use serde::Serialize;

use crate::ai::{find_binary, run_in_terminal};

/// 외부 의존성 도구의 정적 메타데이터.
struct DepMeta {
    /// UI/IPC 에서 쓰는 안정적인 id.
    id: &'static str,
    /// 사용자에게 보여줄 이름.
    display_name: &'static str,
    /// 설치 여부를 판별할 때 PATH 에서 찾는 대표 실행 파일 이름.
    bin: &'static str,
    /// macOS Homebrew 포뮬러 이름.
    brew: &'static str,
    /// Windows scoop 패키지 이름(없으면 "").
    scoop: &'static str,
    /// Linux apt 패키지 이름(없으면 "").
    apt: &'static str,
}

/// 지원하는 외부 의존성 목록. 여기에 추가하면 공통 흐름이 그대로 적용됩니다.
const DEPS: &[DepMeta] = &[DepMeta {
    id: "libimobiledevice",
    display_name: "libimobiledevice",
    bin: "idevice_id",
    brew: "libimobiledevice",
    scoop: "libimobiledevice",
    apt: "libimobiledevice-utils",
}];

fn dep_meta(id: &str) -> Option<&'static DepMeta> {
    DEPS.iter().find(|d| d.id == id)
}

/// 현재 OS 에서 이 도구를 설치하는 명령(미지원이면 None).
fn install_command(meta: &DepMeta) -> Option<String> {
    if cfg!(target_os = "macos") {
        Some(format!("brew install {}", meta.brew))
    } else if cfg!(windows) {
        if meta.scoop.is_empty() {
            None
        } else {
            Some(format!("scoop install {}", meta.scoop))
        }
    } else if meta.apt.is_empty() {
        None
    } else {
        Some(format!("sudo apt-get install -y {}", meta.apt))
    }
}

#[derive(Serialize)]
pub struct DepStatus {
    pub id: String,
    pub display_name: String,
    /// 도구가 이미 설치돼 있는지.
    pub installed: bool,
    /// 설치에 사용할 명령(프런트엔드 팝업에 미리보기로 표시). 미지원이면 "".
    pub install_cmd: String,
    /// 이 OS 에서 자동 설치를 지원하는지.
    pub installable: bool,
}

/// 외부 도구의 설치 여부와 설치 방법을 조회합니다.
#[tauri::command]
pub fn check_dependency(id: String) -> Result<DepStatus, String> {
    let meta = dep_meta(&id).ok_or_else(|| format!("알 수 없는 도구입니다: {id}"))?;
    let install_cmd = install_command(meta);
    Ok(DepStatus {
        id: meta.id.to_string(),
        display_name: meta.display_name.to_string(),
        installed: find_binary(meta.bin).is_some(),
        install_cmd: install_cmd.clone().unwrap_or_default(),
        installable: install_cmd.is_some(),
    })
}

/// 외부 도구를 OS 에 맞는 패키지 매니저로 설치합니다.
/// 설치는 보이는 터미널에서 진행되어 사용자가 진행 상황과 비밀번호 입력을
/// 직접 확인할 수 있습니다.
#[tauri::command]
pub fn install_dependency(id: String) -> Result<String, String> {
    let meta = dep_meta(&id).ok_or_else(|| format!("알 수 없는 도구입니다: {id}"))?;

    let cmd = install_command(meta).ok_or_else(|| {
        format!(
            "{} 자동 설치는 이 OS 에서 지원하지 않습니다. 수동으로 설치해주세요.",
            meta.display_name
        )
    })?;

    // 패키지 매니저 자체가 없으면 친절히 안내합니다.
    if cfg!(target_os = "macos") && find_binary("brew").is_none() {
        return Err(
            "Homebrew 가 필요합니다. https://brew.sh 에서 먼저 설치해주세요.".to_string(),
        );
    }
    if cfg!(windows) && find_binary("scoop").is_none() {
        return Err(
            "scoop 이 필요합니다. https://scoop.sh 에서 먼저 설치해주세요.".to_string(),
        );
    }

    run_in_terminal(&cmd)?;
    Ok(format!(
        "{} 설치를 터미널에서 시작했습니다: `{}`\n설치가 끝나면 다시 시도해주세요.",
        meta.display_name, cmd
    ))
}
