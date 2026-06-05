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
  env.PATH = `${extraPathEntries.join(process.platform === "win32" ? ";" : ":")}${process.platform === "win32" ? ";" : ":"}${env.PATH ?? ""}`;
}

if (!commandExists("cargo") && !cargoDir) {
  console.error("cargo를 찾을 수 없습니다. Rust 설치 또는 PATH 설정을 확인해주세요.");
  process.exit(1);
}

const isWindows = process.platform === "win32";
const tauriBinary = isWindows
  ? resolve("node_modules", ".bin", "tauri.cmd")
  : resolve("node_modules", ".bin", "tauri");

// Node 18.20+/20.12+/22+ 에서는 보안 변경(CVE-2024-27980)으로 인해
// Windows에서 .cmd/.bat 를 shell 없이 spawn 하면 EINVAL 로 즉시 실패합니다.
// 그래서 Windows 에서는 shell 을 통해 실행하고 경로를 따옴표로 감쌉니다.
const command = isWindows ? `"${tauriBinary}"` : tauriBinary;

const result = spawnSync(command, process.argv.slice(2), {
  stdio: "inherit",
  env,
  shell: isWindows,
});

process.exit(result.status ?? 1);
