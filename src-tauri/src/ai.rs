// AI Helper — open a folder in VSCode and run an installed AI CLI
// (claude / opencode / codex) inside the integrated terminal. Also opens each
// tool's config file and installs missing tools via an OS-appropriate terminal.
//
// Absorbed from the standalone "ai-helper" app; exposed as the "AI" category.

use std::path::{Path, PathBuf};
use std::process::Command;

use serde::Serialize;
use serde_json::{json, Map, Value};

/// Static metadata for each supported AI CLI.
struct ToolMeta {
    /// Stable id used across the UI and IPC.
    id: &'static str,
    /// Executable name to look for on PATH.
    bin: &'static str,
    /// npm package name used for installation.
    npm: &'static str,
}

const TOOLS: [ToolMeta; 3] = [
    ToolMeta {
        id: "claude",
        bin: "claude",
        npm: "@anthropic-ai/claude-code",
    },
    ToolMeta {
        id: "opencode",
        bin: "opencode",
        npm: "opencode-ai",
    },
    ToolMeta {
        id: "codex",
        bin: "codex",
        npm: "@openai/codex",
    },
];

fn tool_meta(id: &str) -> Option<&'static ToolMeta> {
    TOOLS.iter().find(|t| t.id == id)
}

/// Build a list of directories likely to contain user-installed CLIs, so a
/// GUI-launched app (which on macOS does *not* inherit the shell PATH) can
/// still locate binaries.
fn extra_path_dirs() -> Vec<PathBuf> {
    let mut dirs = Vec::new();
    if let Some(home) = dirs::home_dir() {
        for sub in [
            ".local/bin",
            ".opencode/bin",
            ".bun/bin",
            ".cargo/bin",
            ".deno/bin",
            ".npm-global/bin",
            ".volta/bin",
            "bin",
            ".codex/bin",
            "AppData/Roaming/npm",
            "AppData/Local/Programs/Microsoft VS Code/bin",
        ] {
            dirs.push(home.join(sub));
        }
    }
    for p in [
        "/opt/homebrew/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        "/snap/bin",
        "/usr/share/code/bin",
        "/Applications/Visual Studio Code.app/Contents/Resources/app/bin",
        "C:\\Program Files\\Microsoft VS Code\\bin",
        "C:\\Program Files (x86)\\Microsoft VS Code\\bin",
    ] {
        dirs.push(PathBuf::from(p));
    }
    dirs
}

/// Candidate file names for a binary across platforms (Windows adds .cmd/.exe).
fn binary_names(stem: &str) -> Vec<String> {
    if cfg!(windows) {
        vec![
            format!("{stem}.cmd"),
            format!("{stem}.exe"),
            format!("{stem}.bat"),
            stem.to_string(),
        ]
    } else {
        vec![stem.to_string()]
    }
}

/// Try hard to find an executable: first the augmented PATH dirs, then the
/// process PATH itself.
pub(crate) fn find_binary(stem: &str) -> Option<PathBuf> {
    let names = binary_names(stem);

    for dir in extra_path_dirs() {
        for name in &names {
            let candidate = dir.join(name);
            if candidate.is_file() {
                return Some(candidate);
            }
        }
    }

    if let Ok(path) = std::env::var("PATH") {
        let sep = if cfg!(windows) { ';' } else { ':' };
        for dir in path.split(sep) {
            if dir.is_empty() {
                continue;
            }
            for name in &names {
                let candidate = Path::new(dir).join(name);
                if candidate.is_file() {
                    return Some(candidate);
                }
            }
        }
    }

    None
}

/// Resolve the on-disk config file for a tool, per OS conventions.
fn config_path(id: &str) -> Option<PathBuf> {
    let home = dirs::home_dir()?;
    let path = match id {
        // Claude Code keeps user settings under ~/.claude/settings.json.
        "claude" => home.join(".claude").join("settings.json"),
        // opencode follows XDG: $XDG_CONFIG_HOME/opencode/opencode.json,
        // defaulting to ~/.config/opencode/opencode.json on every OS.
        "opencode" => {
            let base = std::env::var_os("XDG_CONFIG_HOME")
                .map(PathBuf::from)
                .unwrap_or_else(|| home.join(".config"));
            base.join("opencode").join("opencode.json")
        }
        // Codex uses TOML at ~/.codex/config.toml.
        "codex" => home.join(".codex").join("config.toml"),
        _ => return None,
    };
    Some(path)
}

/// A minimal starter file so opening a never-configured tool gives the user
/// something editable (and saveable) rather than a phantom path.
fn config_template(id: &str) -> &'static str {
    match id {
        "claude" => "{\n  \n}\n",
        "opencode" => "{\n  \"$schema\": \"https://opencode.ai/config.json\"\n}\n",
        "codex" => "# Codex configuration (https://github.com/openai/codex)\n",
        _ => "",
    }
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ToolInfo {
    id: String,
    installed: bool,
    config_path: String,
    config_exists: bool,
}

/// Report, for each supported tool, whether it is installed and where its
/// config lives. Drives the whole UI.
#[tauri::command]
pub fn list_tools() -> Vec<ToolInfo> {
    TOOLS
        .iter()
        .map(|t| {
            let cfg = config_path(t.id);
            ToolInfo {
                id: t.id.to_string(),
                installed: find_binary(t.bin).is_some(),
                config_path: cfg
                    .as_ref()
                    .map(|p| p.to_string_lossy().to_string())
                    .unwrap_or_default(),
                config_exists: cfg.map(|p| p.is_file()).unwrap_or(false),
            }
        })
        .collect()
}

/// True if the `code` command can be located.
#[tauri::command]
pub fn has_code() -> bool {
    find_binary("code").is_some()
}

/// Parse a JSON/JSONC file into a Value, tolerating comments and trailing
/// commas (common in VSCode config files).
fn read_jsonc(path: &Path) -> Option<Value> {
    let text = std::fs::read_to_string(path).ok()?;
    json5::from_str::<Value>(&text).ok()
}

/// Open an arbitrary path: prefer VSCode (consistent with this app), otherwise
/// fall back to the OS default handler.
fn open_path(path: &Path) -> Result<(), String> {
    if let Some(code) = find_binary("code") {
        Command::new(code)
            .arg(path)
            .spawn()
            .map_err(|e| format!("failed to open in VSCode: {e}"))?;
        return Ok(());
    }

    let result = if cfg!(target_os = "macos") {
        Command::new("open").arg(path).spawn()
    } else if cfg!(windows) {
        Command::new("cmd")
            .args(["/c", "start", ""])
            .arg(path)
            .spawn()
    } else {
        Command::new("xdg-open").arg(path).spawn()
    };
    result.map(|_| ()).map_err(|e| format!("failed to open file: {e}"))
}

/// Open (creating if needed) the config file for `tool`.
#[tauri::command]
pub fn open_config(tool: String) -> Result<String, String> {
    let path = config_path(&tool).ok_or_else(|| format!("Unknown tool: {tool}"))?;
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("create config dir: {e}"))?;
    }
    if !path.exists() {
        std::fs::write(&path, config_template(&tool))
            .map_err(|e| format!("create config file: {e}"))?;
    }
    open_path(&path)?;
    Ok(format!("Opened {}", path.to_string_lossy()))
}

/// Launch an OS-appropriate terminal running `cmd` so the user can watch the
/// install (and provide any required input).
pub(crate) fn run_in_terminal(cmd: &str) -> Result<(), String> {
    if cfg!(target_os = "macos") {
        let escaped = cmd.replace('\\', "\\\\").replace('"', "\\\"");
        let script = format!(
            "tell application \"Terminal\"\nactivate\ndo script \"{escaped}\"\nend tell"
        );
        Command::new("osascript")
            .arg("-e")
            .arg(script)
            .spawn()
            .map_err(|e| format!("failed to open Terminal: {e}"))?;
        Ok(())
    } else if cfg!(windows) {
        // `start` opens a new window; `cmd /k` keeps it open after the install.
        Command::new("cmd")
            .args(["/c", "start", "AI Helper Install", "cmd", "/k", cmd])
            .spawn()
            .map_err(|e| format!("failed to open cmd: {e}"))?;
        Ok(())
    } else {
        for term in ["x-terminal-emulator", "gnome-terminal", "konsole", "xterm"] {
            if find_binary(term).is_some() {
                let hold = format!("{cmd}; echo; echo '[done]'; exec $SHELL");
                return Command::new(term)
                    .arg("-e")
                    .arg("bash")
                    .arg("-lc")
                    .arg(hold)
                    .spawn()
                    .map(|_| ())
                    .map_err(|e| format!("failed to open terminal: {e}"));
            }
        }
        Err("No terminal emulator found.".to_string())
    }
}

/// Install a missing tool via `npm install -g <package>` in a visible terminal.
#[tauri::command]
pub fn install_tool(tool: String) -> Result<String, String> {
    let meta = tool_meta(&tool).ok_or_else(|| format!("Unknown tool: {tool}"))?;
    let cmd = format!("npm install -g {}", meta.npm);
    run_in_terminal(&cmd)?;
    Ok(format!(
        "Installing {} in a terminal: `{}`. Click Refresh when it finishes.",
        meta.id, cmd
    ))
}

/// Merge our auto-run task into a (possibly existing) `.vscode/tasks.json`,
/// preserving any tasks the user already had. `command` is the (absolute)
/// program the folderOpen task runs.
fn write_tasks_json(vscode_dir: &Path, tool: &str, command: &str) -> Result<(), String> {
    let tasks_path = vscode_dir.join("tasks.json");
    let label = format!("AI Helper: {tool}");

    let mut root = read_jsonc(&tasks_path).unwrap_or_else(|| json!({}));
    if !root.is_object() {
        root = json!({});
    }
    let obj = root.as_object_mut().unwrap();
    obj.entry("version")
        .or_insert_with(|| Value::String("2.0.0".to_string()));

    let mut tasks: Vec<Value> = obj
        .get("tasks")
        .and_then(|t| t.as_array())
        .map(|arr| {
            arr.iter()
                .filter(|t| {
                    t.get("label")
                        .and_then(|l| l.as_str())
                        .map(|l| !l.starts_with("AI Helper:"))
                        .unwrap_or(true)
                })
                .cloned()
                .collect()
        })
        .unwrap_or_default();

    let mut presentation = Map::new();
    presentation.insert("reveal".into(), json!("always"));
    presentation.insert("panel".into(), json!("dedicated"));
    presentation.insert("focus".into(), json!(true));
    presentation.insert("clear".into(), json!(true));

    let new_task = json!({
        "label": label,
        "type": "shell",
        "command": command,
        "presentation": Value::Object(presentation),
        "runOptions": { "runOn": "folderOpen" },
        "problemMatcher": []
    });
    tasks.push(new_task);

    obj.insert("tasks".into(), Value::Array(tasks));

    let pretty =
        serde_json::to_string_pretty(&root).map_err(|e| format!("serialize tasks.json: {e}"))?;
    std::fs::write(&tasks_path, pretty).map_err(|e| format!("write tasks.json: {e}"))?;
    Ok(())
}

/// Path to VSCode's global (user) settings.json. `dirs::config_dir()` maps to
/// the right place on every OS:
///   macOS   ~/Library/Application Support/Code/User
///   Windows %APPDATA%\Code\User
///   Linux   ~/.config/Code/User
fn vscode_user_settings_path() -> Option<PathBuf> {
    Some(dirs::config_dir()?.join("Code").join("User").join("settings.json"))
}

/// Insert `"<key>": "<value>"` into a JSONC settings document, preserving the
/// user's existing comments/formatting. Returns None if the key already exists.
fn insert_jsonc_setting(text: &str, key: &str, value: &str) -> Option<String> {
    if text.contains(key) {
        return None; // respect whatever the user already set
    }
    if text.trim().is_empty() {
        return Some(format!("{{\n  \"{key}\": \"{value}\"\n}}\n"));
    }
    if let Some(idx) = text.find('{') {
        let after = &text[idx + 1..];
        let entry = if after.trim_start().starts_with('}') {
            format!("\n  \"{key}\": \"{value}\"\n") // object was empty
        } else {
            format!("\n  \"{key}\": \"{value}\",")
        };
        return Some(format!("{}{}{}", &text[..=idx], entry, after));
    }
    Some(format!("{{\n  \"{key}\": \"{value}\"\n}}\n"))
}

fn enable_automatic_tasks() -> Result<(), String> {
    let path = vscode_user_settings_path().ok_or("could not locate VSCode user settings")?;
    let text = std::fs::read_to_string(&path).unwrap_or_default();

    let Some(new_text) = insert_jsonc_setting(&text, "task.allowAutomaticTasks", "on") else {
        return Ok(());
    };

    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("create settings dir: {e}"))?;
    }
    std::fs::write(&path, new_text).map_err(|e| format!("write VSCode user settings: {e}"))?;
    Ok(())
}

/// Open `folder` in VSCode, optionally auto-running `tool` in the terminal.
#[tauri::command]
pub fn open_in_vscode(folder: String, tool: String, auto_run: bool) -> Result<String, String> {
    let meta = tool_meta(&tool).ok_or_else(|| format!("Unsupported tool: {tool}"))?;
    let tool_bin = find_binary(meta.bin)
        .ok_or_else(|| format!("{} is not installed.", meta.id))?;

    let folder_path = PathBuf::from(&folder);
    if !folder_path.is_dir() {
        return Err(format!("Not a directory: {folder}"));
    }

    if auto_run {
        let vscode_dir = folder_path.join(".vscode");
        std::fs::create_dir_all(&vscode_dir).map_err(|e| format!("create .vscode dir: {e}"))?;
        // Use the absolute path so the task shell finds the tool regardless of
        // its PATH.
        write_tasks_json(&vscode_dir, &tool, &tool_bin.to_string_lossy())?;
        // The setting that actually permits auto-run lives in user settings.
        enable_automatic_tasks()?;
    }

    let code_bin = find_binary("code").ok_or_else(|| {
        "Could not find the 'code' command. Install VSCode and run \"Shell Command: Install 'code' command in PATH\" from the command palette.".to_string()
    })?;

    Command::new(&code_bin)
        .arg("-n")
        .arg(&folder_path)
        .spawn()
        .map_err(|e| format!("failed to launch VSCode: {e}"))?;

    if auto_run {
        Ok(format!(
            "Opened {} in VSCode — '{}' will start in the integrated terminal.",
            folder, tool
        ))
    } else {
        Ok(format!("Opened {} in VSCode.", folder))
    }
}

// --- MCP servers ------------------------------------------------------------

/// Where each tool stores its MCP server definitions (differs from the general
/// config file for Claude, which keeps MCP in ~/.claude.json).
fn mcp_path(id: &str) -> Option<PathBuf> {
    let home = dirs::home_dir()?;
    match id {
        "claude" => Some(home.join(".claude.json")),
        "opencode" => config_path("opencode"),
        "codex" => config_path("codex"),
        _ => None,
    }
}

#[derive(Serialize)]
pub struct McpEntry {
    name: String,
    detail: String,
}

/// Human-readable summary of a JSON-shaped MCP definition (claude / opencode).
fn json_mcp_detail(v: &Value) -> String {
    if let Some(url) = v.get("url").and_then(|x| x.as_str()) {
        return url.to_string();
    }
    let args_of = |key: &str| {
        v.get(key)
            .and_then(|a| a.as_array())
            .map(|a| {
                a.iter()
                    .filter_map(|x| x.as_str())
                    .collect::<Vec<_>>()
                    .join(" ")
            })
            .unwrap_or_default()
    };
    match v.get("command") {
        Some(Value::String(s)) => format!("{s} {}", args_of("args")).trim().to_string(),
        // opencode uses an array: ["npx", "-y", "pkg"]
        Some(Value::Array(arr)) => arr
            .iter()
            .filter_map(|x| x.as_str())
            .collect::<Vec<_>>()
            .join(" "),
        _ => String::new(),
    }
}

fn list_json_mcps(path: &Path, container: &str) -> Vec<McpEntry> {
    let Some(root) = read_jsonc(path) else {
        return Vec::new();
    };
    root.get(container)
        .and_then(|c| c.as_object())
        .map(|obj| {
            obj.iter()
                .map(|(name, v)| McpEntry {
                    name: name.clone(),
                    detail: json_mcp_detail(v),
                })
                .collect()
        })
        .unwrap_or_default()
}

fn list_codex_mcps(path: &Path) -> Vec<McpEntry> {
    let Ok(text) = std::fs::read_to_string(path) else {
        return Vec::new();
    };
    let Ok(doc) = text.parse::<toml_edit::DocumentMut>() else {
        return Vec::new();
    };
    let Some(servers) = doc.get("mcp_servers").and_then(|s| s.as_table()) else {
        return Vec::new();
    };
    servers
        .iter()
        .map(|(name, item)| {
            let cmd = item.get("command").and_then(|c| c.as_str()).unwrap_or("");
            let args = item
                .get("args")
                .and_then(|a| a.as_array())
                .map(|a| {
                    a.iter()
                        .filter_map(|x| x.as_str())
                        .collect::<Vec<_>>()
                        .join(" ")
                })
                .unwrap_or_default();
            McpEntry {
                name: name.to_string(),
                detail: format!("{cmd} {args}").trim().to_string(),
            }
        })
        .collect()
}

/// List the MCP servers currently registered for `tool`.
#[tauri::command]
pub fn list_mcps(tool: String) -> Result<Vec<McpEntry>, String> {
    let path = mcp_path(&tool).ok_or_else(|| format!("Unknown tool: {tool}"))?;
    Ok(match tool.as_str() {
        "claude" => list_json_mcps(&path, "mcpServers"),
        "opencode" => list_json_mcps(&path, "mcp"),
        "codex" => list_codex_mcps(&path),
        _ => Vec::new(),
    })
}

/// Split a command line into program + args (simple whitespace tokenizer).
fn tokenize(command: &str) -> Result<(String, Vec<String>), String> {
    let mut parts = command.split_whitespace().map(|s| s.to_string());
    let prog = parts.next().ok_or("Command is empty.")?;
    Ok((prog, parts.collect()))
}

fn add_json_mcp(
    path: &Path,
    container: &str,
    name: &str,
    entry: Value,
) -> Result<(), String> {
    let mut root = read_jsonc(path).unwrap_or_else(|| json!({}));
    if !root.is_object() {
        root = json!({});
    }
    let obj = root.as_object_mut().unwrap();
    let map = obj
        .entry(container.to_string())
        .or_insert_with(|| json!({}));
    if !map.is_object() {
        *map = json!({});
    }
    map.as_object_mut().unwrap().insert(name.to_string(), entry);

    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("create dir: {e}"))?;
    }
    let pretty =
        serde_json::to_string_pretty(&root).map_err(|e| format!("serialize: {e}"))?;
    std::fs::write(path, pretty).map_err(|e| format!("write {}: {e}", path.display()))?;
    Ok(())
}

fn add_codex_mcp(
    path: &Path,
    name: &str,
    prog: &str,
    args: &[String],
) -> Result<(), String> {
    let text = std::fs::read_to_string(path).unwrap_or_default();
    let mut doc = text
        .parse::<toml_edit::DocumentMut>()
        .map_err(|e| format!("parse config.toml: {e}"))?;

    if !doc.contains_key("mcp_servers") {
        doc["mcp_servers"] = toml_edit::Item::Table(toml_edit::Table::new());
    }
    let servers = doc["mcp_servers"]
        .as_table_mut()
        .ok_or("mcp_servers is not a table")?;

    let mut entry = toml_edit::Table::new();
    entry["command"] = toml_edit::value(prog);
    let mut arr = toml_edit::Array::new();
    for a in args {
        arr.push(a.as_str());
    }
    entry["args"] = toml_edit::value(arr);
    servers[name] = toml_edit::Item::Table(entry);

    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(|e| format!("create dir: {e}"))?;
    }
    std::fs::write(path, doc.to_string()).map_err(|e| format!("write config.toml: {e}"))?;
    Ok(())
}

/// Register a local (stdio) MCP server for `tool`, given a name and the full
/// command line to launch it (e.g. `npx -y @modelcontextprotocol/server-foo`).
#[tauri::command]
pub fn add_mcp(tool: String, name: String, command: String) -> Result<String, String> {
    let name = name.trim();
    if name.is_empty() {
        return Err("MCP name is required.".to_string());
    }
    let (prog, args) = tokenize(command.trim())?;
    let path = mcp_path(&tool).ok_or_else(|| format!("Unknown tool: {tool}"))?;

    match tool.as_str() {
        "claude" => add_json_mcp(
            &path,
            "mcpServers",
            name,
            json!({ "command": prog, "args": args }),
        )?,
        "opencode" => {
            let mut cmd = vec![Value::String(prog)];
            cmd.extend(args.into_iter().map(Value::String));
            add_json_mcp(
                &path,
                "mcp",
                name,
                json!({ "type": "local", "command": cmd, "enabled": true }),
            )?
        }
        "codex" => add_codex_mcp(&path, name, &prog, &args)?,
        _ => return Err(format!("Unknown tool: {tool}")),
    }
    Ok(format!("Registered MCP '{name}' for {tool} in {}", path.display()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn merges_into_existing_jsonc_preserving_user_tasks() {
        let dir = std::env::temp_dir().join(format!("aih-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(
            dir.join("tasks.json"),
            r#"{
  // user's own config
  "version": "2.0.0",
  "tasks": [
    { "label": "build", "type": "shell", "command": "make", },
    { "label": "AI Helper: codex", "type": "shell", "command": "codex" }
  ],
}"#,
        )
        .unwrap();

        write_tasks_json(&dir, "claude", "/abs/path/claude").unwrap();

        let parsed = read_jsonc(&dir.join("tasks.json")).unwrap();
        let tasks = parsed["tasks"].as_array().unwrap();
        let labels: Vec<&str> = tasks.iter().map(|t| t["label"].as_str().unwrap()).collect();

        assert!(labels.contains(&"build"));
        assert!(labels.contains(&"AI Helper: claude"));
        assert_eq!(
            labels.iter().filter(|l| l.starts_with("AI Helper:")).count(),
            1
        );

        let ours = tasks
            .iter()
            .find(|t| t["label"] == "AI Helper: claude")
            .unwrap();
        assert_eq!(ours["command"], "/abs/path/claude");
        assert_eq!(ours["runOptions"]["runOn"], "folderOpen");

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn adds_and_lists_codex_mcp_roundtrip() {
        let dir = std::env::temp_dir().join(format!("aih-mcp-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("config.toml");
        std::fs::write(&path, "model = \"o3\"\n").unwrap();

        add_codex_mcp(
            &path,
            "files",
            "npx",
            &["-y".into(), "@modelcontextprotocol/server-filesystem".into()],
        )
        .unwrap();

        // Pre-existing key survives.
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("model = \"o3\""));

        let entries = list_codex_mcps(&path);
        let files = entries.iter().find(|e| e.name == "files").unwrap();
        assert!(files.detail.contains("npx"));
        assert!(files.detail.contains("server-filesystem"));

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn adds_and_lists_json_mcp_roundtrip() {
        let dir = std::env::temp_dir().join(format!("aih-mcpj-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join(".claude.json");
        std::fs::write(&path, r#"{ "numStartups": 3 }"#).unwrap();

        add_json_mcp(
            &path,
            "mcpServers",
            "files",
            json!({ "command": "npx", "args": ["-y", "server-filesystem"] }),
        )
        .unwrap();

        let root = read_jsonc(&path).unwrap();
        assert_eq!(root["numStartups"], 3); // other keys preserved
        let entries = list_json_mcps(&path, "mcpServers");
        let files = entries.iter().find(|e| e.name == "files").unwrap();
        assert_eq!(files.detail, "npx -y server-filesystem");

        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn inserts_setting_preserving_existing_content_and_comments() {
        // Existing keys + comment are preserved; our key is added; valid JSONC.
        let src = "{\n  // user comment\n  \"editor.fontSize\": 13\n}\n";
        let out = insert_jsonc_setting(src, "task.allowAutomaticTasks", "on").unwrap();
        assert!(out.contains("// user comment"));
        assert!(out.contains("\"editor.fontSize\": 13"));
        assert!(out.contains("\"task.allowAutomaticTasks\": \"on\""));
        assert_eq!(json5::from_str::<Value>(&out).unwrap()["task.allowAutomaticTasks"], "on");

        // Empty object → no trailing comma, still valid.
        let out2 = insert_jsonc_setting("{}", "task.allowAutomaticTasks", "on").unwrap();
        assert_eq!(json5::from_str::<Value>(&out2).unwrap()["task.allowAutomaticTasks"], "on");

        // Empty file → fresh document.
        let out3 = insert_jsonc_setting("", "task.allowAutomaticTasks", "on").unwrap();
        assert_eq!(json5::from_str::<Value>(&out3).unwrap()["task.allowAutomaticTasks"], "on");

        // Already present → left untouched.
        assert!(insert_jsonc_setting(&out, "task.allowAutomaticTasks", "on").is_none());
    }

    #[test]
    fn config_paths_are_defined_for_all_tools() {
        for t in TOOLS.iter() {
            let p = config_path(t.id).expect("config path");
            assert!(p.is_absolute());
            assert!(!config_template(t.id).is_empty());
        }
    }
}
