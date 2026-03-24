import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open } from "@tauri-apps/plugin-dialog";

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

  setupTabs();
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
