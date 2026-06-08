mod ai;
mod deps;

use serde::{Deserialize, Serialize};
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::Mutex;
use tauri::{AppHandle, Emitter, Manager, State, Window};

// Global state for WebGL server process
struct AppState {
    webgl_server_pid: Mutex<Option<u32>>,
}

// Simple base64 encoder (no external dependency)
fn to_base64(bytes: &[u8]) -> String {
    const TABLE: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut result = String::with_capacity((bytes.len() + 2) / 3 * 4);
    for chunk in bytes.chunks(3) {
        let b0 = chunk[0] as usize;
        let b1 = if chunk.len() > 1 { chunk[1] as usize } else { 0 };
        let b2 = if chunk.len() > 2 { chunk[2] as usize } else { 0 };
        result.push(TABLE[b0 >> 2] as char);
        result.push(TABLE[((b0 & 3) << 4) | (b1 >> 4)] as char);
        if chunk.len() > 1 {
            result.push(TABLE[((b1 & 0xf) << 2) | (b2 >> 6)] as char);
        } else {
            result.push('=');
        }
        if chunk.len() > 2 {
            result.push(TABLE[b2 & 0x3f] as char);
        } else {
            result.push('=');
        }
    }
    result
}

// Find aapt or aapt2 in Android SDK build-tools (cross-platform)
fn find_sdk_tool(tool_name: &str) -> Option<String> {
    // On Windows executables have .exe extension
    let exe = if cfg!(target_os = "windows") {
        format!("{}.exe", tool_name)
    } else {
        tool_name.to_string()
    };

    let build_tools_base = if cfg!(target_os = "windows") {
        let local = std::env::var("LOCALAPPDATA").unwrap_or_default();
        format!("{}\\Android\\Sdk\\build-tools", local)
    } else {
        let home = std::env::var("HOME").unwrap_or_default();
        format!("{}/Library/Android/sdk/build-tools", home)
    };

    if let Ok(entries) = std::fs::read_dir(&build_tools_base) {
        let mut dirs: Vec<_> = entries
            .flatten()
            .filter(|e| e.path().is_dir())
            .collect();
        dirs.sort_by(|a, b| b.file_name().cmp(&a.file_name()));
        for entry in dirs {
            let tool_path = entry.path().join(&exe);
            if tool_path.exists() {
                return Some(tool_path.to_string_lossy().to_string());
            }
        }
    }
    None
}

// Cross-platform: open a file/URL with the system default application
fn open_with_system(path: &str) {
    #[cfg(target_os = "macos")]
    let _ = Command::new("open").arg(path).spawn();
    #[cfg(target_os = "windows")]
    let _ = Command::new("cmd").args(["/c", "start", "", path]).spawn();
    #[cfg(not(any(target_os = "macos", target_os = "windows")))]
    let _ = Command::new("xdg-open").arg(path).spawn();
}

// Cross-platform: kill a process by PID
fn kill_pid(pid: u32) {
    #[cfg(unix)]
    let _ = Command::new("kill").arg(pid.to_string()).output();
    #[cfg(windows)]
    let _ = Command::new("taskkill")
        .args(["/PID", &pid.to_string(), "/F"])
        .output();
}

// Extract app label + package name from `aapt dump badging` output
// Returns (app_name, package_name)
fn get_apk_badge_info(aapt: &str, apk_path: &str) -> (String, String) {
    let fallback_name = Path::new(apk_path)
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();

    let Ok(output) = Command::new(aapt)
        .args(["dump", "badging", apk_path])
        .output()
    else {
        return (fallback_name, String::new());
    };

    let stdout = String::from_utf8_lossy(&output.stdout);
    let mut default_label: Option<String> = None;
    let mut en_label: Option<String> = None;
    let mut package_name = String::new();

    for line in stdout.lines() {
        // package: name='com.example.app' ...
        if line.starts_with("package:") && package_name.is_empty() {
            for part in line.split_whitespace() {
                if let Some(name) = part.strip_prefix("name='") {
                    package_name = name.trim_end_matches('\'').to_string();
                    break;
                }
            }
        }
        if line.starts_with("application-label:'") && default_label.is_none() {
            default_label = Some(
                line.trim_start_matches("application-label:'")
                    .trim_end_matches('\'')
                    .to_string(),
            );
        }
        if line.starts_with("application-label-en:'") && en_label.is_none() {
            en_label = Some(
                line.trim_start_matches("application-label-en:'")
                    .trim_end_matches('\'')
                    .to_string(),
            );
        }
    }

    let app_name = en_label.or(default_label).unwrap_or(fallback_name);
    (app_name, package_name)
}

// Find the highest-density PNG path for a mipmap resource using `aapt2 dump resources`
fn get_apk_icon_b64(aapt2: &str, apk_path: &str) -> Option<String> {
    let output = Command::new(aapt2)
        .args(["dump", "resources", apk_path])
        .output()
        .ok()?;

    let stdout = String::from_utf8_lossy(&output.stdout);

    // Prefer ic_launcher_foreground, fall back to app_icon
    let resource_names = ["mipmap/ic_launcher_foreground", "mipmap/app_icon"];
    // Prefer highest density without locale qualifier
    let density_patterns = [
        "(xxxhdpi) (file)",
        "(xxhdpi) (file)",
        "(xhdpi) (file)",
        "(hdpi) (file)",
        "(mdpi) (file)",
    ];

    for resource_name in &resource_names {
        let mut in_section = false;
        let mut best: Option<(usize, String)> = None; // (priority, res/Xx.png)

        for line in stdout.lines() {
            let trimmed = line.trim();

            if trimmed.contains(resource_name) && trimmed.starts_with("resource") {
                in_section = true;
                continue;
            }

            if in_section {
                if trimmed.starts_with("resource") {
                    break; // entered next resource section
                }
                if !trimmed.contains("type=PNG") {
                    continue;
                }
                for (priority, &pattern) in density_patterns.iter().enumerate() {
                    if trimmed.contains(pattern) {
                        if best.as_ref().map(|(p, _)| priority < *p).unwrap_or(true) {
                            if let Some(file_start) = trimmed.find("(file) ") {
                                let rest = &trimmed[file_start + 7..];
                                if let Some(type_end) = rest.find(" type=") {
                                    best = Some((priority, rest[..type_end].to_string()));
                                }
                            }
                        }
                        break;
                    }
                }
                if best.as_ref().map(|(p, _)| *p == 0).unwrap_or(false) {
                    break; // already found best density
                }
            }
        }

        if let Some((_, path)) = best {
            // Extract file from APK (APK is a zip)
            if let Ok(out) = Command::new("unzip").args(["-p", apk_path, &path]).output() {
                if !out.stdout.is_empty() {
                    return Some(format!("data:image/png;base64,{}", to_base64(&out.stdout)));
                }
            }
        }
    }
    None
}

// Read iOS app name from Info.plist (XML plist — Unity always generates XML)
fn get_ios_name(project_dir: &Path) -> String {
    let fallback = project_dir
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();

    let plist = project_dir.join("Info.plist");
    let Ok(content) = std::fs::read_to_string(&plist) else {
        return fallback;
    };

    // Parse: <key>CFBundleDisplayName</key>\n<string>VALUE</string>
    for key in &["CFBundleDisplayName", "CFBundleName"] {
        let search = format!("<key>{}</key>", key);
        if let Some(pos) = content.find(&search) {
            let rest = &content[pos + search.len()..];
            if let Some(start) = rest.find("<string>") {
                let rest = &rest[start + 8..];
                if let Some(end) = rest.find("</string>") {
                    let name = rest[..end].trim().to_string();
                    if !name.is_empty() {
                        return name;
                    }
                }
            }
        }
    }
    fallback
}

// Find AppIcon.appiconset and return largest PNG as base64
fn get_ios_icon_b64(project_dir: &Path) -> Option<String> {
    // BFS up to 5 levels deep to find .appiconset
    let mut queue = vec![project_dir.to_path_buf()];
    let mut appiconset: Option<PathBuf> = None;

    'outer: for _ in 0..5 {
        let mut next = Vec::new();
        for dir in &queue {
            if let Ok(entries) = std::fs::read_dir(dir) {
                for entry in entries.flatten() {
                    let path = entry.path();
                    if path.is_dir() {
                        if path.extension().map(|e| e == "appiconset").unwrap_or(false) {
                            appiconset = Some(path);
                            break 'outer;
                        }
                        next.push(path);
                    }
                }
            }
        }
        queue = next;
    }

    let appiconset = appiconset?;

    // Find largest PNG
    let largest = std::fs::read_dir(&appiconset)
        .ok()?
        .flatten()
        .filter(|e| {
            e.path()
                .extension()
                .map(|x| x.eq_ignore_ascii_case("png"))
                .unwrap_or(false)
        })
        .max_by_key(|e| e.metadata().map(|m| m.len()).unwrap_or(0))?;

    let bytes = std::fs::read(largest.path()).ok()?;
    if bytes.is_empty() {
        return None;
    }
    Some(format!("data:image/png;base64,{}", to_base64(&bytes)))
}

// Extract WebGL build name from index.html <title>
fn get_webgl_name(build_dir: &Path) -> String {
    let fallback = build_dir
        .file_name()
        .map(|n| n.to_string_lossy().to_string())
        .unwrap_or_default();

    let index = build_dir.join("index.html");
    let Ok(content) = std::fs::read_to_string(&index) else {
        return fallback;
    };

    // Extract <title>...</title>
    if let Some(start) = content.find("<title>") {
        let rest = &content[start + 7..];
        if let Some(end) = rest.find("</title>") {
            let raw = rest[..end].trim();
            // Unity default: "Unity Web Player | Game Name"
            if let Some(pipe) = raw.rfind(" | ") {
                return raw[pipe + 3..].to_string();
            }
            return raw.to_string();
        }
    }
    fallback
}

// Convert TemplateData/favicon.ico to base64
fn get_webgl_icon_b64(build_dir: &Path) -> Option<String> {
    let favicon = build_dir.join("TemplateData").join("favicon.ico");
    if !favicon.exists() {
        return None;
    }

    // macOS: convert ICO → PNG via sips for better rendering
    #[cfg(target_os = "macos")]
    {
        let tmp = std::env::temp_dir().join("webgl_favicon_tmp.png");
        if let Ok(status) = Command::new("sips")
            .args(["-s", "format", "png", favicon.to_str()?, "--out", tmp.to_str()?])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
        {
            if status.success() {
                if let Ok(bytes) = std::fs::read(&tmp) {
                    if !bytes.is_empty() {
                        return Some(format!("data:image/png;base64,{}", to_base64(&bytes)));
                    }
                }
            }
        }
    }

    // Fallback: serve ICO directly (all modern browsers support it)
    let bytes = std::fs::read(&favicon).ok()?;
    if bytes.is_empty() {
        return None;
    }
    Some(format!("data:image/x-icon;base64,{}", to_base64(&bytes)))
}

// Find adb path (cross-platform)
fn find_adb() -> Result<String, String> {
    // Build candidate list based on OS
    let mut candidates: Vec<String> = Vec::new();

    if cfg!(target_os = "windows") {
        let local = std::env::var("LOCALAPPDATA").unwrap_or_default();
        candidates.push(format!("{}\\Android\\Sdk\\platform-tools\\adb.exe", local));
        candidates.push(format!("{}\\Android\\android-sdk\\platform-tools\\adb.exe", local));
        candidates.push("C:\\Android\\platform-tools\\adb.exe".to_string());
        candidates.push("C:\\android-sdk\\platform-tools\\adb.exe".to_string());
    } else {
        let home = std::env::var("HOME").unwrap_or_default();
        candidates.push("/opt/homebrew/bin/adb".to_string());
        candidates.push("/usr/local/bin/adb".to_string());
        candidates.push(format!("{}/Library/Android/sdk/platform-tools/adb", home));
        candidates.push(format!("{}/Android/Sdk/platform-tools/adb", home));
    }

    // ANDROID_HOME / ANDROID_SDK_ROOT env vars
    for env_var in &["ANDROID_HOME", "ANDROID_SDK_ROOT"] {
        if let Ok(sdk) = std::env::var(env_var) {
            let adb = if cfg!(target_os = "windows") {
                format!("{}\\platform-tools\\adb.exe", sdk)
            } else {
                format!("{}/platform-tools/adb", sdk)
            };
            candidates.push(adb);
        }
    }

    for path in &candidates {
        if std::path::Path::new(path).exists() {
            return Ok(path.to_string());
        }
    }

    // Try PATH lookup
    let which_cmd = if cfg!(target_os = "windows") { "where" } else { "/usr/bin/which" };
    if let Ok(output) = Command::new(which_cmd).arg("adb").output() {
        let path = String::from_utf8_lossy(&output.stdout)
            .lines()
            .next()
            .unwrap_or("")
            .trim()
            .to_string();
        if !path.is_empty() && std::path::Path::new(&path).exists() {
            return Ok(path);
        }
    }

    Err("ADB를 찾을 수 없습니다. Android SDK를 설치하거나 ANDROID_HOME 환경변수를 설정해주세요.".to_string())
}

// Parse ADB error messages to Korean
fn parse_adb_error(error: &str) -> String {
    let error_lower = error.to_lowercase();

    if error_lower.contains("install_failed_already_exists") {
        return "설치 실패: 이미 설치된 앱이 있습니다. 기존 앱을 삭제하고 다시 시도해주세요.".to_string();
    }
    if error_lower.contains("install_failed_insufficient_storage") {
        return "설치 실패: 디바이스 저장 공간이 부족합니다.".to_string();
    }
    if error_lower.contains("install_failed_invalid_apk") {
        return "설치 실패: APK 파일이 손상되었거나 유효하지 않습니다.".to_string();
    }
    if error_lower.contains("install_failed_version_downgrade") {
        return "설치 실패: 이미 설치된 버전보다 낮은 버전입니다. 기존 앱을 삭제하고 다시 시도해주세요.".to_string();
    }
    if error_lower.contains("install_failed_update_incompatible") {
        return "설치 실패: 기존 앱과 서명이 다릅니다. 기존 앱을 삭제하고 다시 시도해주세요.".to_string();
    }
    if error_lower.contains("install_failed_older_sdk") {
        return "설치 실패: 디바이스의 Android 버전이 앱의 최소 요구 버전보다 낮습니다.".to_string();
    }
    if error_lower.contains("install_failed_no_matching_abis") {
        return "설치 실패: 디바이스의 CPU 아키텍처와 APK가 호환되지 않습니다.".to_string();
    }
    if error_lower.contains("install_parse_failed") {
        return "설치 실패: APK 파일을 파싱할 수 없습니다. 파일이 손상되었을 수 있습니다.".to_string();
    }
    if error_lower.contains("install_failed_test_only") {
        return "설치 실패: 테스트 전용 APK입니다. -t 옵션이 필요합니다.".to_string();
    }
    if error_lower.contains("device not found") || error_lower.contains("no devices") {
        return "설치 실패: 디바이스를 찾을 수 없습니다. USB 연결을 확인해주세요.".to_string();
    }
    if error_lower.contains("device offline") {
        return "설치 실패: 디바이스가 오프라인 상태입니다. USB 연결을 다시 확인해주세요.".to_string();
    }
    if error_lower.contains("unauthorized") {
        return "설치 실패: 디바이스에서 USB 디버깅을 허용해주세요.".to_string();
    }
    if error_lower.contains("install_failed_user_restricted") {
        return "설치 실패: 사용자 제한으로 설치할 수 없습니다. 디바이스 설정을 확인해주세요.".to_string();
    }
    if error_lower.contains("failure") {
        if let Some(start) = error.find('[') {
            if let Some(end) = error.find(']') {
                let reason = &error[start + 1..end];
                return format!("설치 실패: {}", reason);
            }
        }
    }

    format!("설치 실패: {}", error)
}

#[derive(Serialize, Deserialize)]
pub struct DeviceInfo {
    pub id: String,
    pub model: String,
    pub manufacturer: String,
    pub android_version: String,
    pub is_tablet: bool,
    pub authorized: bool,
}

#[derive(Serialize, Deserialize)]
pub struct ApkInfo {
    pub name: String,           // filename (e.g. ld.apk)
    pub app_name: String,       // extracted app label
    pub package_name: String,   // e.g. com.example.app
    pub path: String,
    pub icon_base64: Option<String>,
}

#[derive(Serialize, Deserialize)]
pub struct IosProject {
    pub name: String,       // folder name
    pub app_name: String,   // CFBundleDisplayName
    pub path: String,
    pub icon_base64: Option<String>,
}

#[derive(Serialize, Deserialize)]
pub struct WebglBuild {
    pub name: String,       // folder name
    pub app_name: String,   // title from index.html
    pub path: String,
    pub icon_base64: Option<String>,
}

#[derive(Serialize, Deserialize)]
pub struct MissingFolders {
    pub missing: Vec<String>,
    pub path: String,
}

// Config file path: <app_data_dir>/build_path.txt
fn config_file(app: &AppHandle) -> Option<PathBuf> {
    app.path().app_data_dir().ok().map(|d| d.join("build_path.txt"))
}

#[tauri::command]
fn save_build_path(app: AppHandle, path: String) -> Result<(), String> {
    let file = config_file(&app).ok_or("설정 경로를 찾을 수 없습니다.")?;
    if let Some(parent) = file.parent() {
        std::fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    std::fs::write(&file, &path).map_err(|e| e.to_string())
}

#[tauri::command]
fn load_build_path(app: AppHandle) -> Option<String> {
    let file = config_file(&app)?;
    let path = std::fs::read_to_string(&file).ok()?.trim().to_string();
    if path.is_empty() || !std::path::Path::new(&path).exists() {
        return None;
    }
    Some(path)
}

// Path Commands
#[tauri::command]
fn get_current_dir() -> Result<String, String> {
    std::env::current_dir()
        .map(|p| p.to_string_lossy().to_string())
        .map_err(|e| format!("현재 디렉토리를 가져올 수 없습니다: {}", e))
}

#[tauri::command]
fn check_build_folders(path: String) -> Result<MissingFolders, String> {
    let base_path = PathBuf::from(&path);

    if !base_path.exists() {
        return Err("지정한 경로가 존재하지 않습니다.".to_string());
    }

    if !base_path.is_dir() {
        return Err("지정한 경로가 폴더가 아닙니다.".to_string());
    }

    let required_folders = ["Android", "iOS", "MacOS", "WebGL"];
    let mut missing = Vec::new();

    for folder in &required_folders {
        let folder_path = base_path.join(folder);
        if !folder_path.exists() {
            missing.push(folder.to_string());
        }
    }

    Ok(MissingFolders { missing, path })
}

#[tauri::command]
fn create_build_folders(path: String, folders: Vec<String>) -> Result<String, String> {
    let base_path = PathBuf::from(&path);

    for folder in &folders {
        let folder_path = base_path.join(folder);
        std::fs::create_dir_all(&folder_path)
            .map_err(|e| format!("{} 폴더 생성 실패: {}", folder, e))?;
    }

    Ok(format!("{} 폴더가 생성되었습니다.", folders.join(", ")))
}

// Android Commands
#[tauri::command]
fn get_android_devices() -> Result<Vec<DeviceInfo>, String> {
    let adb_path = find_adb()?;

    let output = Command::new(&adb_path)
        .args(["devices", "-l"])
        .output()
        .map_err(|e| format!("ADB 실행 실패: {}", e))?;

    let stdout = String::from_utf8_lossy(&output.stdout);
    let mut devices = Vec::new();

    for line in stdout.lines().skip(1) {
        if line.trim().is_empty() {
            continue;
        }
        if line.contains("offline") {
            continue;
        }

        let parts: Vec<&str> = line.split_whitespace().collect();
        if parts.is_empty() {
            continue;
        }

        let id = parts[0].to_string();

        if line.contains("unauthorized") {
            devices.push(DeviceInfo {
                id,
                model: "알 수 없는 기기".to_string(),
                manufacturer: String::new(),
                android_version: String::new(),
                is_tablet: false,
                authorized: false,
            });
            continue;
        }

        if parts.len() >= 2 && parts[1] == "device" {
            // One shell call to get all properties at once
            let props_output = Command::new(&adb_path)
                .args([
                    "-s", &id, "shell",
                    "getprop ro.product.model; echo '---'; \
                     getprop ro.product.manufacturer; echo '---'; \
                     getprop ro.build.version.release; echo '---'; \
                     getprop ro.product.characteristics",
                ])
                .output();

            let (model, manufacturer, android_version, is_tablet) =
                if let Ok(out) = props_output {
                    let text = String::from_utf8_lossy(&out.stdout);
                    let mut parts = text.split("---\n");
                    let model = parts.next().unwrap_or("").trim().to_string();
                    let manufacturer = parts.next().unwrap_or("").trim().to_string();
                    let version = parts.next().unwrap_or("").trim().to_string();
                    let characteristics = parts.next().unwrap_or("").trim().to_string();
                    let is_tablet = characteristics.contains("tablet");
                    (
                        if model.is_empty() { id.clone() } else { model },
                        manufacturer,
                        version,
                        is_tablet,
                    )
                } else {
                    (id.clone(), String::new(), String::new(), false)
                };

            devices.push(DeviceInfo {
                id,
                model,
                manufacturer,
                android_version,
                is_tablet,
                authorized: true,
            });
        }
    }

    Ok(devices)
}

#[tauri::command]
fn get_apk_list(build_path: String) -> Result<Vec<ApkInfo>, String> {
    let android_path = PathBuf::from(&build_path).join("Android");

    if !android_path.exists() {
        return Err("Android 폴더를 찾을 수 없습니다.".to_string());
    }

    let aapt = find_sdk_tool("aapt");
    let aapt2 = find_sdk_tool("aapt2");

    let mut apks = Vec::new();

    if let Ok(entries) = std::fs::read_dir(&android_path) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.extension().map(|e| e == "apk").unwrap_or(false) {
                let path_str = path.to_string_lossy().to_string();
                let filename = path
                    .file_name()
                    .map(|n| n.to_string_lossy().to_string())
                    .unwrap_or_default();

                let (app_name, package_name) = aapt
                    .as_deref()
                    .map(|a| get_apk_badge_info(a, &path_str))
                    .unwrap_or_else(|| (filename.clone(), String::new()));

                let icon_base64 = aapt2
                    .as_deref()
                    .and_then(|a| get_apk_icon_b64(a, &path_str));

                apks.push(ApkInfo {
                    name: filename,
                    app_name,
                    package_name,
                    path: path_str,
                    icon_base64,
                });
            }
        }
    }

    apks.sort_by(|a, b| a.app_name.cmp(&b.app_name));
    Ok(apks)
}

#[tauri::command]
async fn install_apk(
    window: Window,
    device_id: String,
    apk_path: String,
    package_name: String,
    launch_after: bool,
) -> Result<String, String> {
    let adb_path = find_adb()?;

    if !std::path::Path::new(&apk_path).exists() {
        let err_msg = "APK 파일을 찾을 수 없습니다.".to_string();
        let _ = window.emit(
            "install-progress",
            serde_json::json!({ "status": "error", "progress": 0, "message": &err_msg }),
        );
        return Err(err_msg);
    }

    let file_size = std::fs::metadata(&apk_path)
        .map(|m| m.len())
        .unwrap_or(0);
    let file_size_mb = file_size as f64 / 1_000_000.0;

    let _ = window.emit(
        "install-progress",
        serde_json::json!({ "status": "starting", "progress": 0, "message": "설치 준비 중..." }),
    );

    let mut child = Command::new(&adb_path)
        .args(["-s", &device_id, "install", "-r", &apk_path])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| {
            let err_msg = format!("ADB 실행 실패: {}", e);
            let _ = window.emit(
                "install-progress",
                serde_json::json!({ "status": "error", "progress": 0, "message": &err_msg }),
            );
            err_msg
        })?;

    let _ = window.emit(
        "install-progress",
        serde_json::json!({
            "status": "installing",
            "progress": 10,
            "message": format!("APK 전송 중... ({:.1} MB)", file_size_mb)
        }),
    );

    let stdout = child.stdout.take();
    let stderr = child.stderr.take();

    let window_clone = window.clone();
    let progress_handle = std::thread::spawn(move || {
        let mut output_lines = Vec::new();
        if let Some(stdout) = stdout {
            let reader = BufReader::new(stdout);
            for line in reader.lines().map_while(Result::ok) {
                output_lines.push(line.clone());
                if line.contains("Performing") || line.contains("pkg:") {
                    let _ = window_clone.emit(
                        "install-progress",
                        serde_json::json!({
                            "status": "installing",
                            "progress": 50,
                            "message": "디바이스에 설치 중..."
                        }),
                    );
                }
            }
        }
        output_lines
    });

    let status = child.wait().map_err(|e| {
        let err_msg = format!("ADB 프로세스 대기 실패: {}", e);
        let _ = window.emit(
            "install-progress",
            serde_json::json!({ "status": "error", "progress": 0, "message": &err_msg }),
        );
        err_msg
    })?;

    let stdout_lines = progress_handle.join().unwrap_or_default();
    let stdout_output = stdout_lines.join("\n");

    let stderr_output = if let Some(stderr) = stderr {
        let reader = BufReader::new(stderr);
        reader
            .lines()
            .map_while(Result::ok)
            .collect::<Vec<_>>()
            .join("\n")
    } else {
        String::new()
    };

    let combined_output = format!("{}\n{}", stdout_output, stderr_output);

    if status.success()
        && (combined_output.contains("Success") || stdout_output.contains("Success"))
    {
        // Launch app if requested
        if launch_after && !package_name.is_empty() {
            let _ = window.emit(
                "install-progress",
                serde_json::json!({ "status": "installing", "progress": 95, "message": "앱 실행 중..." }),
            );
            let _ = Command::new(&adb_path)
                .args([
                    "-s", &device_id,
                    "shell", "monkey",
                    "-p", &package_name,
                    "-c", "android.intent.category.LAUNCHER",
                    "1",
                ])
                .output();
        }

        let success_msg = if launch_after { "APK 설치 완료! 앱을 실행했습니다.".to_string() } else { "APK 설치 완료!".to_string() };
        let _ = window.emit(
            "install-progress",
            serde_json::json!({ "status": "completed", "progress": 100, "message": &success_msg }),
        );
        Ok(success_msg)
    } else {
        let error_msg = parse_adb_error(&combined_output);
        let _ = window.emit(
            "install-progress",
            serde_json::json!({ "status": "error", "progress": 0, "message": &error_msg }),
        );
        Err(error_msg)
    }
}

// iOS Commands
#[tauri::command]
fn get_ios_projects(build_path: String) -> Result<Vec<IosProject>, String> {
    let ios_path = PathBuf::from(&build_path).join("iOS");

    if !ios_path.exists() {
        return Err("iOS 폴더를 찾을 수 없습니다.".to_string());
    }

    let mut projects = Vec::new();

    if let Ok(entries) = std::fs::read_dir(&ios_path) {
        for entry in entries.flatten() {
            let project_dir = entry.path();
            if project_dir.is_dir() {
                // Find .xcworkspace directly inside project_dir
                if let Ok(sub_entries) = std::fs::read_dir(&project_dir) {
                    for sub_entry in sub_entries.flatten() {
                        let sub_path = sub_entry.path();
                        if sub_path.extension().map(|e| e == "xcworkspace").unwrap_or(false) {
                            let folder_name = project_dir
                                .file_name()
                                .map(|n| n.to_string_lossy().to_string())
                                .unwrap_or_default();

                            let app_name = get_ios_name(&project_dir);
                            let icon_base64 = get_ios_icon_b64(&project_dir);

                            projects.push(IosProject {
                                name: folder_name,
                                app_name,
                                path: sub_path.to_string_lossy().to_string(),
                                icon_base64,
                            });
                            break;
                        }
                    }
                }
            }
        }
    }

    projects.sort_by(|a, b| a.app_name.cmp(&b.app_name));
    Ok(projects)
}

#[tauri::command]
fn open_xcode_project(workspace_path: String) -> Result<String, String> {
    #[cfg(target_os = "windows")]
    {
        let _ = workspace_path;
        return Err("Windows에서는 Xcode를 사용할 수 없습니다. macOS가 필요합니다.".to_string());
    }

    #[cfg(not(target_os = "windows"))]
    {
        open_with_system(&workspace_path);
        Ok("Xcode 프로젝트를 열었습니다.".to_string())
    }
}

// WebGL Commands
#[tauri::command]
fn get_webgl_builds(build_path: String) -> Result<Vec<WebglBuild>, String> {
    let webgl_path = PathBuf::from(&build_path).join("WebGL");

    if !webgl_path.exists() {
        return Err("WebGL 폴더를 찾을 수 없습니다.".to_string());
    }

    let mut builds = Vec::new();

    if let Ok(entries) = std::fs::read_dir(&webgl_path) {
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                let index_path = path.join("index.html");
                if index_path.exists() {
                    let folder_name = path
                        .file_name()
                        .map(|n| n.to_string_lossy().to_string())
                        .unwrap_or_default();

                    let app_name = get_webgl_name(&path);
                    let icon_base64 = get_webgl_icon_b64(&path);

                    builds.push(WebglBuild {
                        name: folder_name,
                        app_name,
                        path: path.to_string_lossy().to_string(),
                        icon_base64,
                    });
                }
            }
        }
    }

    builds.sort_by(|a, b| a.app_name.cmp(&b.app_name));
    Ok(builds)
}

#[tauri::command]
fn start_webgl_server(
    state: State<AppState>,
    build_path: String,
    port: u16,
) -> Result<String, String> {
    stop_webgl_server_internal(&state);

    // Try python3 first, fall back to python (common on Windows)
    let child = Command::new("python3")
        .args(["-m", "http.server", &port.to_string()])
        .current_dir(&build_path)
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .or_else(|_| {
            Command::new("python")
                .args(["-m", "http.server", &port.to_string()])
                .current_dir(&build_path)
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
        })
        .map_err(|e| format!("서버 시작 실패: {} (Python이 설치되어 있는지 확인해주세요)", e))?;

    let pid = child.id();
    *state.webgl_server_pid.lock().unwrap() = Some(pid);

    let url = format!("http://localhost:{}", port);
    open_with_system(&url);

    Ok(format!("서버 시작됨: {} (PID: {})", url, pid))
}

fn stop_webgl_server_internal(state: &State<AppState>) {
    if let Some(pid) = state.webgl_server_pid.lock().unwrap().take() {
        kill_pid(pid);
    }
}

#[tauri::command]
fn stop_webgl_server(state: State<AppState>) -> Result<String, String> {
    stop_webgl_server_internal(&state);
    Ok("서버가 중지되었습니다.".to_string())
}

#[tauri::command]
fn get_webgl_server_status(state: State<AppState>) -> bool {
    state.webgl_server_pid.lock().unwrap().is_some()
}

#[tauri::command]
fn get_platform() -> &'static str {
    if cfg!(target_os = "windows") {
        "windows"
    } else if cfg!(target_os = "macos") {
        "macos"
    } else {
        "linux"
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_dialog::init())
        .manage(AppState {
            webgl_server_pid: Mutex::new(None),
        })
        .invoke_handler(tauri::generate_handler![
            save_build_path,
            load_build_path,
            get_current_dir,
            check_build_folders,
            create_build_folders,
            get_android_devices,
            get_apk_list,
            install_apk,
            get_ios_projects,
            open_xcode_project,
            get_webgl_builds,
            start_webgl_server,
            stop_webgl_server,
            get_webgl_server_status,
            get_platform,
            ai::list_tools,
            ai::has_code,
            ai::open_config,
            ai::install_tool,
            ai::open_in_vscode,
            ai::list_mcps,
            ai::add_mcp,
            deps::check_dependency,
            deps::install_dependency,
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
