import { dirname, resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import process from "node:process";

function commandExists(command) {
  const lookup = process.platform === "win32" ? "where" : "which";
  const result = spawnSync(lookup, [command], { stdio: "ignore" });
  return result.status === 0;
}

function resolveCargoDir() {
  if (commandExists("cargo")) {
    return null;
  }

  if (!commandExists("rustup")) {
    return null;
  }

  const result = spawnSync("rustup", ["which", "cargo"], {
    encoding: "utf8",
  });

  if (result.status !== 0) {
    return null;
  }

  const cargoPath = result.stdout.trim();
  return cargoPath ? dirname(cargoPath) : null;
}

const env = { ...process.env };
const cargoDir = resolveCargoDir();
const extraPathEntries = [];

const cargoHomeBin = resolve(process.env.HOME ?? "~", ".cargo", "bin");
if (existsSync(cargoHomeBin)) {
  extraPathEntries.push(cargoHomeBin);
}

if (cargoDir) {
  extraPathEntries.push(cargoDir);
}

const isWindowsCrossBuild = process.argv.includes("cargo-xwin")
  || process.argv.some((arg) => arg.includes("pc-windows-msvc"));

if (process.platform === "darwin" && isWindowsCrossBuild) {
  const homebrewLlvmBin = "/opt/homebrew/opt/llvm/bin";
  if (existsSync(homebrewLlvmBin)) {
    extraPathEntries.push(homebrewLlvmBin);
  }
}

if (extraPathEntries.length > 0) {
  const sep = process.platform === "win32" ? ";" : ":";
  // Windows 의 환경변수 키는 보통 "Path" 라서 대소문자를 무시하고 기존 키를 찾습니다.
  // "env.PATH" 로 바로 쓰면 plain object 에서는 "Path" 를 못 찾아 PATH 가 통째로
  // cargo 경로로 덮여 버리고(예: npm 이 사라짐) beforeBuildCommand 가 실패합니다.
  const pathKey = Object.keys(env).find((key) => key.toLowerCase() === "path") ?? "PATH";
  env[pathKey] = `${extraPathEntries.join(sep)}${sep}${env[pathKey] ?? ""}`;
}

if (!commandExists("cargo") && !cargoDir) {
  console.error("cargo를 찾을 수 없습니다. Rust 설치 또는 PATH 설정을 확인해주세요.");
  process.exit(1);
}

// tauri CLI 의 JS 진입점을 node 로 직접 실행합니다.
// node_modules/.bin/tauri.cmd 같은 .cmd 셸을 거치지 않으므로
// Windows + Node 18.20+/20.12+/22+ 의 보안 변경(CVE-2024-27980)으로 인한
// spawn EINVAL 문제를 원천적으로 피합니다. (CI/로컬 모두 동일하게 동작)
const cliEntry = resolve("node_modules", "@tauri-apps", "cli", "tauri.js");

let result;
if (existsSync(cliEntry)) {
  result = spawnSync(process.execPath, [cliEntry, ...process.argv.slice(2)], {
    stdio: "inherit",
    env,
  });
} else {
  // 폴백: JS 진입점을 못 찾으면 .bin 셸 스크립트를 셸을 통해 실행합니다.
  const tauriBinary = process.platform === "win32"
    ? resolve("node_modules", ".bin", "tauri.cmd")
    : resolve("node_modules", ".bin", "tauri");
  result = spawnSync(
    process.platform === "win32" ? `"${tauriBinary}"` : tauriBinary,
    process.argv.slice(2),
    { stdio: "inherit", env, shell: process.platform === "win32" },
  );
}

if (result.error) {
  console.error("tauri CLI 실행 실패:", result.error.message);
}

process.exit(result.status ?? 1);
