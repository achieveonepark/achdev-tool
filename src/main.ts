import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open, ask, message } from "@tauri-apps/plugin-dialog";

// 외부 CLI 의존성 상태 (백엔드 deps.rs 의 DepStatus 와 1:1 대응)
interface DepStatus {
  id: string;
  display_name: string;
  installed: boolean;
  install_cmd: string;
  installable: boolean;
}

/**
 * 외부 CLI 도구가 설치돼 있는지 확인하고, 없으면 "설치할까요?" 팝업을 띄운 뒤
 * 사용자가 동의하면 설치를 시작하는 공통 헬퍼.
 *
 * 외부 도구가 필요한 기능에서 실행 직전에 호출하면 됩니다. 예:
 *
 *   if (!(await ensureDependency("libimobiledevice"))) return; // 아직 준비 안 됨
 *   // ...idevice_id 를 쓰는 실제 작업...
 *
 * @returns 도구가 이미 설치돼 바로 진행해도 되면 true,
 *          (미설치라서) 설치를 시작했거나 사용자가 취소했으면 false.
 */
export async function ensureDependency(id: string): Promise<boolean> {
  let status: DepStatus;
  try {
    status = await invoke<DepStatus>("check_dependency", { id });
  } catch (e) {
    await message(String(e), { title: "도구 확인 실패", kind: "error" });
    return false;
  }

  if (status.installed) return true;

  const name = status.display_name;
  if (!status.installable) {
    await message(
      `${name} 가(이) 설치되어 있지 않습니다.\n이 OS 에서는 자동 설치를 지원하지 않으니 직접 설치해주세요.`,
      { title: "필수 도구 필요", kind: "warning" },
    );
    return false;
  }

  const confirmed = await ask(
    `${name} 가(이) 설치되어 있지 않습니다.\n지금 설치할까요?\n\n설치 명령: ${status.install_cmd}`,
    { title: "필수 도구 설치", kind: "warning", okLabel: "설치", cancelLabel: "취소" },
  );
  if (!confirmed) return false;

  try {
    const msg = await invoke<string>("install_dependency", { id });
    await message(msg, { title: "설치 진행 중", kind: "info" });
  } catch (e) {
    await message(String(e), { title: "설치 실패", kind: "error" });
  }
  return false;
}

// 다른 화면/스크립트에서 호출할 수 있도록 전역에도 노출해 둡니다.
(window as unknown as { ensureDependency: typeof ensureDependency }).ensureDependency =
  ensureDependency;

interface DeviceInfo {
  id: string;
  model: string;
  manufacturer: string;
  android_version: string;
  is_tablet: boolean;
  authorized: boolean;
}

interface ApkInfo {
  name: string;
  app_name: string;
  package_name: string;
  path: string;
  icon_base64: string | null;
}

interface IosProject {
  name: string;
  app_name: string;
  path: string;
  icon_base64: string | null;
}

interface WebglBuild {
  name: string;
  app_name: string;
  path: string;
  icon_base64: string | null;
}

interface InstallProgress {
  status: "starting" | "installing" | "completed" | "error";
  progress: number;
  message: string;
}

interface MissingFolders {
  missing: string[];
  path: string;
}

// DOM Elements
let buildPathInput: HTMLInputElement;
let launchAfterToggle: HTMLInputElement;
let webglPortInput: HTMLInputElement;
let messageEl: HTMLElement;
let serverStatusEl: HTMLElement;
let installBtn: HTMLButtonElement;
let progressContainer: HTMLElement;
let progressBar: HTMLElement;
let progressText: HTMLElement;
let folderModal: HTMLElement;
let modalMessage: HTMLElement;

let deviceGrid: HTMLElement;
let apkGrid: HTMLElement;
let iosGrid: HTMLElement;
let webglGrid: HTMLElement;

// Selected state
let selectedDeviceId = "";
let selectedApkPath = "";
let selectedApkPackage = "";
let selectedIosPath = "";
let selectedWebglPath = "";

// Pending folder creation data
let pendingFolderCreation: MissingFolders | null = null;

function getBuildPath(): string {
  return buildPathInput.value.trim();
}

function showMessage(text: string, type: "success" | "error" | "info") {
  messageEl.textContent = text;
  messageEl.className = `message ${type}`;
  const timeout = type === "error" ? 8000 : 5000;
  setTimeout(() => {
    messageEl.className = "message";
  }, timeout);
}

function showProgress() {
  progressContainer.style.display = "block";
  progressBar.style.width = "0%";
  progressBar.className = "progress-fill";
  progressText.textContent = "준비 중...";
  installBtn.disabled = true;
}

function updateProgress(progress: number, message: string, status: string) {
  progressBar.style.width = `${progress}%`;
  progressText.textContent = message;
  if (status === "completed") {
    progressBar.classList.add("completed");
  } else if (status === "error") {
    progressBar.classList.add("error");
  }
}

function hideProgress() {
  setTimeout(() => {
    progressContainer.style.display = "none";
    installBtn.disabled = false;
  }, 2000);
}

// ── Loading helpers ──────────────────────────────────────────────────────────

function setGridLoading(grid: HTMLElement, message = "불러오는 중...") {
  grid.innerHTML = `<div class="grid-loading">${message}</div>`;
}

function withButtonLoading<T>(btnId: string, fn: () => Promise<T>): Promise<T> {
  const btn = document.getElementById(btnId) as HTMLButtonElement | null;
  btn?.classList.add("loading");
  // requestAnimationFrame ensures loading state is painted before work starts
  return new Promise<T>((resolve, reject) => {
    requestAnimationFrame(() => {
      fn().then(resolve, reject).finally(() => btn?.classList.remove("loading"));
    });
  });
}

// ── Card grid rendering ──────────────────────────────────────────────────────

function makeIconEl(iconBase64: string | null, fallbackEmoji: string): HTMLElement {
  if (iconBase64) {
    const img = document.createElement("img");
    img.className = "card-icon";
    img.src = iconBase64;
    img.alt = "icon";
    img.onerror = () => {
      // Replace with placeholder on load error
      const ph = document.createElement("div");
      ph.className = "card-icon-placeholder";
      ph.textContent = fallbackEmoji;
      img.replaceWith(ph);
    };
    return img;
  }
  const ph = document.createElement("div");
  ph.className = "card-icon-placeholder";
  ph.textContent = fallbackEmoji;
  return ph;
}

function renderBuildCards<T extends { name: string; app_name: string; path: string; icon_base64: string | null }>(
  grid: HTMLElement,
  items: T[],
  selectedPath: string,
  fallbackEmoji: string,
  onSelect: (item: T) => void
) {
  grid.innerHTML = "";

  if (items.length === 0) {
    const ph = document.createElement("div");
    ph.className = "grid-placeholder";
    ph.textContent = "빌드 파일이 없습니다";
    grid.appendChild(ph);
    return;
  }

  items.forEach((item) => {
    const card = document.createElement("div");
    card.className = "build-card" + (item.path === selectedPath ? " selected" : "");
    card.title = item.name;

    const iconEl = makeIconEl(item.icon_base64, fallbackEmoji);

    const nameEl = document.createElement("div");
    nameEl.className = "card-app-name";
    nameEl.textContent = item.app_name;

    const fileEl = document.createElement("div");
    fileEl.className = "card-filename";
    fileEl.textContent = item.name;

    card.appendChild(iconEl);
    card.appendChild(nameEl);
    card.appendChild(fileEl);

    card.addEventListener("click", () => {
      grid.querySelectorAll(".build-card").forEach((c) => c.classList.remove("selected"));
      card.classList.add("selected");
      onSelect(item);
    });

    grid.appendChild(card);
  });
}

// ── Modal ────────────────────────────────────────────────────────────────────

function showFolderModal(missing: string[], path: string) {
  pendingFolderCreation = { missing, path };
  modalMessage.innerHTML = `
    선택한 경로에 다음 폴더가 없습니다:<br><br>
    <strong>${missing.join(", ")}</strong><br><br>
    해당 폴더들을 생성하시겠습니까?
  `;
  folderModal.style.display = "flex";
}

function hideModal() {
  folderModal.style.display = "none";
  pendingFolderCreation = null;
}

async function confirmFolderCreation() {
  if (!pendingFolderCreation) return;
  try {
    const result = await invoke<string>("create_build_folders", {
      path: pendingFolderCreation.path,
      folders: pendingFolderCreation.missing,
    });
    showMessage(result, "success");
    buildPathInput.value = pendingFolderCreation.path;
    await saveBuildPath(pendingFolderCreation.path);
    hideModal();
    refreshAll();
  } catch (e) {
    showMessage(`${e}`, "error");
    hideModal();
  }
}

// ── Browse ───────────────────────────────────────────────────────────────────

async function saveBuildPath(path: string) {
  await invoke("save_build_path", { path }).catch(() => {});
}

async function browsePath() {
  try {
    const selected = await open({
      directory: true,
      multiple: false,
      title: "빌드 폴더 선택",
    });
    if (selected && typeof selected === "string") {
      const result = await invoke<MissingFolders>("check_build_folders", { path: selected });
      if (result.missing.length > 0) {
        showFolderModal(result.missing, selected);
      } else {
        buildPathInput.value = selected;
        await saveBuildPath(selected);
        refreshAll();
      }
    }
  } catch (e) {
    showMessage(`폴더 선택 실패: ${e}`, "error");
  }
}

// ── Sidebar category switching ──────────────────────────────────────────────

let aiInitialized = false;
let gitInitialized = false;

function setupSidebar() {
  const navBtns = document.querySelectorAll<HTMLButtonElement>(".nav-btn");
  const views = document.querySelectorAll<HTMLElement>(".view");

  navBtns.forEach((btn) => {
    btn.addEventListener("click", () => {
      const viewId = btn.dataset.view;
      navBtns.forEach((b) => b.classList.remove("active"));
      views.forEach((v) => v.classList.remove("active"));
      btn.classList.add("active");
      document.getElementById(`view-${viewId}`)?.classList.add("active");

      // Lazily load AI tools the first time the AI category is opened.
      if (viewId === "ai" && !aiInitialized) {
        aiInitialized = true;
        loadAiTools();
      }

      // Git repository discovery can touch many folders, so only start it
      // once the user actually opens the Git category.
      if (viewId === "git" && !gitInitialized) {
        gitInitialized = true;
        initializeGit();
      }
    });
  });
}

// ── Tab switching ─────────────────────────────────────────────────────────────

function setupTabs() {
  const tabBtns = document.querySelectorAll<HTMLButtonElement>(".tab-btn");
  const tabContents = document.querySelectorAll<HTMLElement>(".tab-content");

  tabBtns.forEach((btn) => {
    btn.addEventListener("click", () => {
      const tabId = btn.dataset.tab;
      tabBtns.forEach((b) => b.classList.remove("active"));
      tabContents.forEach((c) => c.classList.remove("active"));
      btn.classList.add("active");
      document.getElementById(tabId!)?.classList.add("active");
    });
  });
}

// ── Android ──────────────────────────────────────────────────────────────────

function renderDeviceCards(devices: DeviceInfo[]) {
  deviceGrid.innerHTML = "";

  if (devices.length === 0) {
    const ph = document.createElement("div");
    ph.className = "grid-placeholder";
    ph.textContent = "연결된 디바이스가 없습니다";
    deviceGrid.appendChild(ph);
    return;
  }

  devices.forEach((device) => {
    const card = document.createElement("div");
    card.className = "build-card" + (device.id === selectedDeviceId ? " selected" : "");

    const icon = document.createElement("div");
    icon.className = "card-icon-placeholder";
    icon.style.fontSize = "32px";
    icon.textContent = !device.authorized ? "🔒" : device.is_tablet ? "📟" : "📱";

    const nameEl = document.createElement("div");
    nameEl.className = "card-app-name";
    nameEl.textContent = device.authorized ? device.model : "권한 없음";

    const subEl = document.createElement("div");
    subEl.className = "device-card-sub";
    if (device.authorized) {
      subEl.innerHTML = [
        device.manufacturer,
        device.android_version ? `Android ${device.android_version}` : "",
      ].filter(Boolean).join("<br>");
    } else {
      subEl.textContent = "USB 디버깅 허용 필요";
    }

    card.appendChild(icon);
    card.appendChild(nameEl);
    card.appendChild(subEl);

    if (device.authorized) {
      card.addEventListener("click", () => {
        deviceGrid.querySelectorAll(".build-card").forEach((c) => c.classList.remove("selected"));
        card.classList.add("selected");
        selectedDeviceId = device.id;
      });
    } else {
      card.style.opacity = "0.5";
      card.style.cursor = "default";
    }

    deviceGrid.appendChild(card);
  });
}

async function loadAndroidDevices() {
  setGridLoading(deviceGrid, "디바이스 검색 중...");
  return withButtonLoading("refresh-devices", async () => {
    try {
      const devices = await invoke<DeviceInfo[]>("get_android_devices");
      renderDeviceCards(devices);
      if (devices.length === 0) showMessage("연결된 Android 디바이스가 없습니다", "info");
    } catch (e) {
      showMessage(`디바이스 목록 로드 실패: ${e}`, "error");
      deviceGrid.innerHTML = '<div class="grid-placeholder">디바이스 로드 실패</div>';
    }
  });
}

async function loadApkList() {
  setGridLoading(apkGrid, "APK 파일을 불러오는 중...");
  return withButtonLoading("refresh-apks", async () => {
    try {
      const apks = await invoke<ApkInfo[]>("get_apk_list", { buildPath: getBuildPath() });
      renderBuildCards(apkGrid, apks, selectedApkPath, "📦", (item) => {
        selectedApkPath = item.path;
        selectedApkPackage = item.package_name;
      });
    } catch (_) {
      apkGrid.innerHTML = '<div class="grid-placeholder">APK 파일이 없습니다</div>';
    }
  });
}

async function installApk() {
  if (!selectedDeviceId) {
    showMessage("디바이스를 선택해주세요", "error");
    return;
  }
  if (!selectedApkPath) {
    showMessage("APK 파일을 선택해주세요", "error");
    return;
  }

  showProgress();

  try {
    const result = await invoke<string>("install_apk", {
      deviceId: selectedDeviceId,
      apkPath: selectedApkPath,
      packageName: selectedApkPackage,
      launchAfter: launchAfterToggle.checked,
    });
    showMessage(result, "success");
  } catch (e) {
    showMessage(`${e}`, "error");
  }
}

// ── iOS ───────────────────────────────────────────────────────────────────────

async function loadIosProjects() {
  setGridLoading(iosGrid, "프로젝트를 불러오는 중...");
  return withButtonLoading("refresh-ios", async () => {
    try {
      const projects = await invoke<IosProject[]>("get_ios_projects", { buildPath: getBuildPath() });
      renderBuildCards(iosGrid, projects, selectedIosPath, "📱", (item) => {
        selectedIosPath = item.path;
      });
    } catch (_) {
      iosGrid.innerHTML = '<div class="grid-placeholder">iOS 프로젝트가 없습니다</div>';
    }
  });
}

async function openXcode() {
  if (!selectedIosPath) {
    showMessage("프로젝트를 선택해주세요", "error");
    return;
  }
  try {
    const result = await invoke<string>("open_xcode_project", { workspacePath: selectedIosPath });
    showMessage(result, "success");
  } catch (e) {
    showMessage(`${e}`, "error");
  }
}

// ── WebGL ─────────────────────────────────────────────────────────────────────

async function loadWebglBuilds() {
  setGridLoading(webglGrid, "빌드를 불러오는 중...");
  return withButtonLoading("refresh-webgl", async () => {
    try {
      const builds = await invoke<WebglBuild[]>("get_webgl_builds", { buildPath: getBuildPath() });
      renderBuildCards(webglGrid, builds, selectedWebglPath, "🌐", (item) => {
        selectedWebglPath = item.path;
      });
    } catch (_) {
      webglGrid.innerHTML = '<div class="grid-placeholder">WebGL 빌드가 없습니다</div>';
    }
  });
}

async function startServer() {
  const port = parseInt(webglPortInput.value, 10);

  if (!selectedWebglPath) {
    showMessage("빌드를 선택해주세요", "error");
    return;
  }
  if (isNaN(port) || port < 1024 || port > 65535) {
    showMessage("올바른 포트 번호를 입력해주세요 (1024-65535)", "error");
    return;
  }

  try {
    const result = await invoke<string>("start_webgl_server", { buildPath: selectedWebglPath, port });
    showMessage(result, "success");
    updateServerStatus();
  } catch (e) {
    showMessage(`${e}`, "error");
  }
}

async function stopServer() {
  try {
    const result = await invoke<string>("stop_webgl_server");
    showMessage(result, "info");
    updateServerStatus();
  } catch (e) {
    showMessage(`${e}`, "error");
  }
}

async function updateServerStatus() {
  const isRunning = await invoke<boolean>("get_webgl_server_status");
  if (isRunning) {
    serverStatusEl.textContent = "서버 실행 중";
    serverStatusEl.className = "status-box running";
  } else {
    serverStatusEl.textContent = "서버 중지됨";
    serverStatusEl.className = "status-box";
  }
}

// ── Refresh all ───────────────────────────────────────────────────────────────

async function refreshAll() {
  return withButtonLoading("refresh-all", async () => {
    await Promise.all([
      loadAndroidDevices(),
      loadApkList(),
      loadIosProjects(),
      loadWebglBuilds(),
    ]);
    updateServerStatus();
  });
}

// ── Progress listener ─────────────────────────────────────────────────────────

async function setupProgressListener() {
  await listen<InstallProgress>("install-progress", (event) => {
    const { status, progress, message } = event.payload;
    updateProgress(progress, message, status);
    if (status === "completed" || status === "error") {
      hideProgress();
    }
  });
}

// ── AI category (absorbed from ai-helper) ──────────────────────────────────────

type ToolId = "claude" | "opencode" | "codex";

interface ToolInfo {
  id: ToolId;
  installed: boolean;
  configPath: string;
  configExists: boolean;
}

interface McpEntry {
  name: string;
  detail: string;
}

const AI_RECENT_KEY = "ai-helper.recent";
const AI_TOOL_KEY = "ai-helper.tool";
const AI_AUTORUN_KEY = "ai-helper.autorun";
const AI_MAX_RECENT = 6;

let aiFolderInput: HTMLInputElement;
let aiPickBtn: HTMLButtonElement;
let aiOpenBtn: HTMLButtonElement;
let aiAutorunInput: HTMLInputElement;
let aiStatusEl: HTMLElement;
let aiRecentList: HTMLElement;
let aiToolsEl: HTMLElement;

let aiSelectedFolder = "";
let aiSelectedTool: ToolId | "" = (localStorage.getItem(AI_TOOL_KEY) as ToolId) || "";
let aiTools: ToolInfo[] = [];

function setAiStatus(message: string, kind: "info" | "error" | "ok" = "info") {
  aiStatusEl.textContent = message;
  aiStatusEl.dataset.kind = kind;
}

function aiLoadRecent(): string[] {
  try {
    return JSON.parse(localStorage.getItem(AI_RECENT_KEY) || "[]");
  } catch {
    return [];
  }
}

function aiPushRecent(folder: string) {
  const recent = [folder, ...aiLoadRecent().filter((f) => f !== folder)].slice(0, AI_MAX_RECENT);
  localStorage.setItem(AI_RECENT_KEY, JSON.stringify(recent));
  renderAiRecent();
}

function renderAiRecent() {
  aiRecentList.innerHTML = "";
  for (const folder of aiLoadRecent()) {
    const li = document.createElement("li");
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "recent-item";
    btn.title = folder;
    btn.textContent = folder.replace(/^.*[/\\]/, "") || folder;
    btn.addEventListener("click", () => setAiFolder(folder));
    li.appendChild(btn);
    aiRecentList.appendChild(li);
  }
}

function setAiFolder(folder: string) {
  aiSelectedFolder = folder;
  aiFolderInput.value = folder;
  refreshAiOpenButton();
}

function refreshAiOpenButton() {
  const tool = aiTools.find((t) => t.id === aiSelectedTool);
  aiOpenBtn.disabled = !aiSelectedFolder || !tool || !tool.installed;
}

function makeAiButton(label: string, act: string, cls = "btn-secondary"): HTMLButtonElement {
  const b = document.createElement("button");
  b.type = "button";
  b.className = cls;
  b.textContent = label;
  b.dataset.act = act;
  return b;
}

function renderAiTools() {
  aiToolsEl.innerHTML = "";

  // If the persisted choice isn't installed, fall back to the first installed.
  if (!aiTools.some((t) => t.id === aiSelectedTool && t.installed)) {
    aiSelectedTool = aiTools.find((t) => t.installed)?.id ?? "";
  }

  for (const tool of aiTools) {
    const card = document.createElement("div");
    card.className = "tool-card";
    card.dataset.tool = tool.id;

    const head = document.createElement("div");
    head.className = "tool-head";

    if (tool.installed) {
      const pick = document.createElement("label");
      pick.className = "tool-pick";
      const radio = document.createElement("input");
      radio.type = "radio";
      radio.name = "ai-tool";
      radio.value = tool.id;
      radio.checked = tool.id === aiSelectedTool;
      radio.addEventListener("change", () => {
        aiSelectedTool = tool.id;
        localStorage.setItem(AI_TOOL_KEY, tool.id);
        refreshAiOpenButton();
      });
      const name = document.createElement("span");
      name.className = "tool-name";
      name.textContent = tool.id;
      pick.append(radio, name);
      head.appendChild(pick);
    } else {
      const name = document.createElement("span");
      name.className = "tool-name muted";
      name.textContent = tool.id;
      head.appendChild(name);
    }

    const badge = document.createElement("span");
    badge.className = `badge ${tool.installed ? "ok" : "bad"}`;
    badge.textContent = tool.installed ? "설치됨" : "미설치";
    head.appendChild(badge);
    card.appendChild(head);

    const actions = document.createElement("div");
    actions.className = "tool-actions";
    if (tool.installed) {
      actions.appendChild(makeAiButton("설정 열기", "config"));
      actions.appendChild(makeAiButton("MCP 서버", "mcp"));
    } else {
      actions.appendChild(makeAiButton("설치", "install", "btn-secondary install"));
    }
    card.appendChild(actions);

    // Collapsible MCP panel (populated lazily).
    const mcp = document.createElement("div");
    mcp.className = "mcp-panel hidden";
    mcp.innerHTML = `
      <ul class="mcp-list"></ul>
      <div class="mcp-add">
        <input class="mcp-name" placeholder="이름 (예: filesystem)" />
        <input class="mcp-cmd" placeholder="명령 (예: npx -y @modelcontextprotocol/server-filesystem ~)" />
        <button type="button" class="btn-secondary" data-act="mcp-add">추가</button>
      </div>`;
    card.appendChild(mcp);

    aiToolsEl.appendChild(card);
  }

  refreshAiOpenButton();
}

async function loadAiTools() {
  aiTools = await invoke<ToolInfo[]>("list_tools");
  renderAiTools();
  const code = await invoke<boolean>("has_code");
  if (!code) {
    setAiStatus(
      "안내: 'code' 명령을 찾을 수 없습니다. VSCode에서 \"Shell Command: Install 'code' command in PATH\"를 실행하세요.",
      "error",
    );
  } else if (!aiTools.some((t) => t.installed)) {
    setAiStatus("아직 설치된 AI CLI가 없습니다 — 아래 설치 버튼을 사용하세요.", "info");
  }
}

async function renderMcpList(card: HTMLElement, tool: ToolId) {
  const list = card.querySelector<HTMLUListElement>(".mcp-list")!;
  list.innerHTML = `<li class="mcp-empty">불러오는 중…</li>`;
  try {
    const entries = await invoke<McpEntry[]>("list_mcps", { tool });
    list.innerHTML = "";
    if (entries.length === 0) {
      list.innerHTML = `<li class="mcp-empty">등록된 MCP 서버가 없습니다.</li>`;
      return;
    }
    for (const e of entries) {
      const li = document.createElement("li");
      li.className = "mcp-item";
      const n = document.createElement("span");
      n.className = "mcp-item-name";
      n.textContent = e.name;
      const d = document.createElement("span");
      d.className = "mcp-item-detail";
      d.textContent = e.detail;
      d.title = e.detail;
      li.append(n, d);
      list.appendChild(li);
    }
  } catch (err) {
    list.innerHTML = `<li class="mcp-empty">${String(err)}</li>`;
  }
}

function setupAiToolActions() {
  aiToolsEl.addEventListener("click", async (ev) => {
    const btn = (ev.target as HTMLElement).closest<HTMLButtonElement>("button[data-act]");
    if (!btn) return;
    const card = btn.closest<HTMLElement>(".tool-card")!;
    const tool = card.dataset.tool as ToolId;
    const act = btn.dataset.act!;

    if (act === "config") {
      try {
        setAiStatus(await invoke<string>("open_config", { tool }), "ok");
      } catch (err) {
        setAiStatus(String(err), "error");
      }
    } else if (act === "install") {
      try {
        setAiStatus(await invoke<string>("install_tool", { tool }), "info");
      } catch (err) {
        setAiStatus(String(err), "error");
      }
    } else if (act === "mcp") {
      const panel = card.querySelector<HTMLElement>(".mcp-panel")!;
      const willShow = panel.classList.contains("hidden");
      panel.classList.toggle("hidden");
      if (willShow) await renderMcpList(card, tool);
    } else if (act === "mcp-add") {
      const nameEl = card.querySelector<HTMLInputElement>(".mcp-name")!;
      const cmdEl = card.querySelector<HTMLInputElement>(".mcp-cmd")!;
      const name = nameEl.value.trim();
      const command = cmdEl.value.trim();
      if (!name || !command) {
        setAiStatus("MCP 이름과 명령을 모두 입력해야 합니다.", "error");
        return;
      }
      try {
        setAiStatus(await invoke<string>("add_mcp", { tool, name, command }), "ok");
        nameEl.value = "";
        cmdEl.value = "";
        await renderMcpList(card, tool);
      } catch (err) {
        setAiStatus(String(err), "error");
      }
    }
  });
}

async function aiPickFolder() {
  const result = await open({ directory: true, multiple: false });
  if (typeof result === "string") setAiFolder(result);
}

async function aiOpenInVscode() {
  if (!aiSelectedFolder || !aiSelectedTool) return;
  aiOpenBtn.disabled = true;
  setAiStatus("VSCode를 여는 중…", "info");
  try {
    const message = await invoke<string>("open_in_vscode", {
      folder: aiSelectedFolder,
      tool: aiSelectedTool,
      autoRun: aiAutorunInput.checked,
    });
    aiPushRecent(aiSelectedFolder);
    setAiStatus(message, "ok");
  } catch (err) {
    setAiStatus(String(err), "error");
  } finally {
    refreshAiOpenButton();
  }
}

function setupAi() {
  aiFolderInput = document.getElementById("ai-folder-input") as HTMLInputElement;
  aiPickBtn = document.getElementById("ai-pick-btn") as HTMLButtonElement;
  aiOpenBtn = document.getElementById("ai-open-btn") as HTMLButtonElement;
  aiAutorunInput = document.getElementById("ai-autorun") as HTMLInputElement;
  aiStatusEl = document.getElementById("ai-status") as HTMLElement;
  aiRecentList = document.getElementById("ai-recent-list") as HTMLElement;
  aiToolsEl = document.getElementById("ai-tools") as HTMLElement;

  setupAiToolActions();

  aiPickBtn.addEventListener("click", aiPickFolder);
  aiOpenBtn.addEventListener("click", aiOpenInVscode);
  document.getElementById("ai-refresh-btn")?.addEventListener("click", loadAiTools);

  aiAutorunInput.checked = localStorage.getItem(AI_AUTORUN_KEY) !== "false";
  aiAutorunInput.addEventListener("change", () => {
    localStorage.setItem(AI_AUTORUN_KEY, String(aiAutorunInput.checked));
  });

  renderAiRecent();
}

// ── Git category ────────────────────────────────────────────────────────────

interface GitRepository {
  name: string;
  path: string;
  branch: string;
  dirty: boolean;
}

interface GitUpdateResult {
  name: string;
  path: string;
  status: "updated" | "up_to_date" | "skipped" | "failed";
  message: string;
}

let gitFolderInput: HTMLInputElement;
let gitPickBtn: HTMLButtonElement;
let gitScanBtn: HTMLButtonElement;
let gitUpdateAllBtn: HTMLButtonElement;
let gitRepoList: HTMLElement;
let gitRepoCount: HTMLElement;
let gitStatusEl: HTMLElement;
let gitSelectedFolder = "";
let gitRepositories: GitRepository[] = [];
let gitUpdateResults = new Map<string, GitUpdateResult>();

function setGitStatus(message: string, kind: "info" | "error" | "ok" = "info") {
  gitStatusEl.textContent = message;
  gitStatusEl.dataset.kind = kind;
}

function setGitListLoading(message: string) {
  gitRepoList.innerHTML = `<div class="grid-loading">${message}</div>`;
}

function gitResultLabel(status: GitUpdateResult["status"]): string {
  switch (status) {
    case "updated": return "업데이트됨";
    case "up_to_date": return "최신";
    case "skipped": return "건너뜀";
    case "failed": return "실패";
  }
}

function renderGitRepositories() {
  gitRepoList.innerHTML = "";
  gitRepoCount.textContent = `${gitRepositories.length}개`;
  gitUpdateAllBtn.disabled = gitRepositories.length === 0;

  if (gitRepositories.length === 0) {
    gitRepoList.innerHTML = '<div class="grid-placeholder">GitHub 저장소가 없습니다.</div>';
    return;
  }

  for (const repository of gitRepositories) {
    const card = document.createElement("article");
    card.className = "git-repo-card";
    card.title = repository.path;

    const head = document.createElement("div");
    head.className = "git-repo-head";
    const name = document.createElement("strong");
    name.className = "git-repo-name";
    name.textContent = repository.name;
    const state = document.createElement("span");
    state.className = `git-repo-state ${repository.dirty ? "dirty" : "clean"}`;
    state.textContent = repository.dirty ? "변경사항 있음" : "깨끗함";
    head.append(name, state);

    const branch = document.createElement("div");
    branch.className = "git-repo-branch";
    branch.textContent = `⌘ ${repository.branch || "HEAD"}`;

    const path = document.createElement("div");
    path.className = "git-repo-path";
    path.textContent = repository.path;

    card.append(head, branch, path);

    const result = gitUpdateResults.get(repository.path);
    if (result) {
      const update = document.createElement("div");
      update.className = `git-update-status ${result.status}`;
      update.textContent = `${gitResultLabel(result.status)} · ${result.message}`;
      update.title = result.message;
      card.appendChild(update);
    }

    gitRepoList.appendChild(card);
  }
}

function setGitFolder(folder: string) {
  gitSelectedFolder = folder;
  gitFolderInput.value = folder;
  gitRepositories = [];
  gitUpdateResults.clear();
  gitRepoCount.textContent = "0개";
  gitUpdateAllBtn.disabled = true;
}

async function scanGitRepositories() {
  if (!gitSelectedFolder) {
    setGitStatus("먼저 기준 폴더를 선택해주세요.", "error");
    return;
  }

  gitScanBtn.disabled = true;
  gitScanBtn.classList.add("loading");
  gitUpdateAllBtn.disabled = true;
  gitUpdateResults.clear();
  setGitListLoading("GitHub 저장소를 찾는 중...");
  setGitStatus("", "info");

  try {
    gitRepositories = await invoke<GitRepository[]>("find_github_repositories", {
      path: gitSelectedFolder,
    });
    renderGitRepositories();
    setGitStatus(
      gitRepositories.length > 0
        ? `${gitRepositories.length}개의 GitHub 저장소를 찾았습니다.`
        : "GitHub 저장소를 찾지 못했습니다.",
      "info",
    );
  } catch (error) {
    gitRepositories = [];
    gitRepoList.innerHTML = '<div class="grid-placeholder">저장소를 불러오지 못했습니다.</div>';
    gitRepoCount.textContent = "0개";
    setGitStatus(String(error), "error");
  } finally {
    gitScanBtn.disabled = false;
    gitScanBtn.classList.remove("loading");
    gitUpdateAllBtn.disabled = gitRepositories.length === 0;
  }
}

async function pickGitFolder() {
  try {
    const selected = await open({
      directory: true,
      multiple: false,
      title: "GitHub 저장소가 있는 폴더 선택",
    });
    if (typeof selected !== "string") return;

    setGitFolder(selected);
    await invoke("save_git_path", { path: selected });
    await scanGitRepositories();
  } catch (error) {
    setGitStatus(`폴더 선택 실패: ${String(error)}`, "error");
  }
}

async function updateAllGitRepositories() {
  if (gitRepositories.length === 0) return;

  const confirmed = await ask(
    `${gitRepositories.length}개 저장소를 최신 상태로 업데이트할까요?\n\n로컬 변경사항이 있는 저장소와 추적 브랜치가 없는 저장소는 건너뜁니다.`,
    { title: "GitHub 저장소 일괄 업데이트", kind: "warning", okLabel: "업데이트", cancelLabel: "취소" },
  );
  if (!confirmed) return;

  gitUpdateAllBtn.disabled = true;
  gitUpdateAllBtn.classList.add("loading");
  setGitStatus("저장소를 순서대로 업데이트하는 중...", "info");
  try {
    const results = await invoke<GitUpdateResult[]>("update_github_repositories", {
      paths: gitRepositories.map((repository) => repository.path),
    });
    gitUpdateResults = new Map(results.map((result) => [result.path, result]));
    renderGitRepositories();

    const count = (status: GitUpdateResult["status"]) =>
      results.filter((result) => result.status === status).length;
    const summary = [
      count("updated") ? `${count("updated")}개 업데이트` : "",
      count("up_to_date") ? `${count("up_to_date")}개 최신` : "",
      count("skipped") ? `${count("skipped")}개 건너뜀` : "",
      count("failed") ? `${count("failed")}개 실패` : "",
    ].filter(Boolean).join(" · ");
    setGitStatus(summary || "업데이트할 저장소가 없습니다.", count("failed") ? "error" : "ok");
  } catch (error) {
    setGitStatus(`일괄 업데이트 실패: ${String(error)}`, "error");
  } finally {
    gitUpdateAllBtn.classList.remove("loading");
    gitUpdateAllBtn.disabled = gitRepositories.length === 0;
  }
}

async function initializeGit() {
  const saved = await invoke<string | null>("load_git_path").catch(() => null);
  if (!saved) {
    setGitStatus("GitHub 저장소가 있는 기준 폴더를 선택해주세요.", "info");
    return;
  }
  setGitFolder(saved);
  await scanGitRepositories();
}

function setupGit() {
  gitFolderInput = document.getElementById("git-folder-input") as HTMLInputElement;
  gitPickBtn = document.getElementById("git-pick-btn") as HTMLButtonElement;
  gitScanBtn = document.getElementById("git-scan-btn") as HTMLButtonElement;
  gitUpdateAllBtn = document.getElementById("git-update-all-btn") as HTMLButtonElement;
  gitRepoList = document.getElementById("git-repo-list") as HTMLElement;
  gitRepoCount = document.getElementById("git-repo-count") as HTMLElement;
  gitStatusEl = document.getElementById("git-status") as HTMLElement;

  gitPickBtn.addEventListener("click", pickGitFolder);
  gitScanBtn.addEventListener("click", scanGitRepositories);
  gitUpdateAllBtn.addEventListener("click", updateAllGitRepositories);
}

// ── Init ──────────────────────────────────────────────────────────────────────

async function initBuildPath() {
  // 저장된 경로 우선, 없으면 현재 디렉토리
  const saved = await invoke<string | null>("load_build_path").catch(() => null);
  const path = saved ?? await invoke<string>("get_current_dir").catch(() => "");

  if (!path) {
    showMessage("빌드 경로를 설정해주세요.", "info");
    return;
  }

  buildPathInput.value = path;

  try {
    const result = await invoke<MissingFolders>("check_build_folders", { path });
    if (result.missing.length > 0) {
      showFolderModal(result.missing, path);
    }
  } catch (_) {
    // 경로가 존재하지 않으면 무시
  }
}

window.addEventListener("DOMContentLoaded", () => {
  buildPathInput = document.getElementById("build-path") as HTMLInputElement;
  launchAfterToggle = document.getElementById("launch-after-install") as HTMLInputElement;
  webglPortInput = document.getElementById("webgl-port") as HTMLInputElement;
  messageEl = document.getElementById("message") as HTMLElement;
  serverStatusEl = document.getElementById("server-status") as HTMLElement;
  installBtn = document.getElementById("install-apk") as HTMLButtonElement;
  progressContainer = document.getElementById("install-progress-container") as HTMLElement;
  progressBar = document.getElementById("install-progress-bar") as HTMLElement;
  progressText = document.getElementById("install-progress-text") as HTMLElement;
  folderModal = document.getElementById("folder-modal") as HTMLElement;
  modalMessage = document.getElementById("modal-message") as HTMLElement;

  deviceGrid = document.getElementById("device-grid") as HTMLElement;
  apkGrid = document.getElementById("apk-grid") as HTMLElement;
  iosGrid = document.getElementById("ios-grid") as HTMLElement;
  webglGrid = document.getElementById("webgl-grid") as HTMLElement;

  setupSidebar();
  setupTabs();
  setupAi();
  setupGit();
  setupProgressListener();

  document.getElementById("browse-path")?.addEventListener("click", browsePath);
  document.getElementById("refresh-all")?.addEventListener("click", refreshAll);
  document.getElementById("refresh-devices")?.addEventListener("click", loadAndroidDevices);
  document.getElementById("refresh-apks")?.addEventListener("click", loadApkList);
  document.getElementById("refresh-ios")?.addEventListener("click", loadIosProjects);
  document.getElementById("refresh-webgl")?.addEventListener("click", loadWebglBuilds);

  document.getElementById("install-apk")?.addEventListener("click", installApk);
  document.getElementById("open-xcode")?.addEventListener("click", openXcode);
  document.getElementById("start-server")?.addEventListener("click", startServer);
  document.getElementById("stop-server")?.addEventListener("click", stopServer);

  document.getElementById("modal-cancel")?.addEventListener("click", hideModal);
  document.getElementById("modal-confirm")?.addEventListener("click", confirmFolderCreation);

  // Hide iOS tab on Windows (Xcode not available)
  invoke<string>("get_platform").then((platform) => {
    if (platform === "windows") {
      const iosTab = document.querySelector<HTMLButtonElement>('.tab-btn[data-tab="ios"]');
      const iosContent = document.getElementById("ios");
      if (iosTab) iosTab.style.display = "none";
      if (iosContent) iosContent.style.display = "none";
    }
  });

  initBuildPath().then(() => refreshAll());
});
