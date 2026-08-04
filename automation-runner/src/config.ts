import type { RunnerConfig } from "./contracts.js";

function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`${name} is required.`);
  return value;
}

function integer(name: string, fallback: number, minimum: number, maximum: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const value = Number.parseInt(raw, 10);
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new Error(`${name} must be between ${minimum} and ${maximum}.`);
  }
  return value;
}

function url(name: string, fallback?: string): URL {
  const value = process.env[name]?.trim() || fallback;
  if (!value) throw new Error(`${name} is required.`);
  const result = new URL(value);
  if (result.protocol !== "https:" && !(result.protocol === "http:" && ["127.0.0.1", "localhost"].includes(result.hostname))) {
    throw new Error(`${name} must use HTTPS, except for localhost.`);
  }
  return result;
}

export function loadConfig(): RunnerConfig {
  const platformVersion = process.env.ANDROID_PLATFORM_VERSION?.trim();
  return {
    devBridgeUrl: url("DEVBRIDGE_URL"),
    enrollmentToken: required("DEVBRIDGE_ENROLLMENT_TOKEN"),
    agentName: process.env.DEVBRIDGE_AGENT_NAME?.trim() || "DevBridge Android Runner",
    appiumUrl: url("APPIUM_URL", "http://127.0.0.1:4723"),
    deviceName: process.env.ANDROID_DEVICE_NAME?.trim() || "emulator-5554",
    ...(platformVersion ? { platformVersion } : {}),
    pollMs: integer("DEVBRIDGE_POLL_MS", 3000, 1000, 60000),
    allowControlled: process.env.DEVBRIDGE_ALLOW_CONTROLLED?.toLowerCase() === "true",
    maximumSteps: integer("DEVBRIDGE_MAX_STEPS", 60, 1, 100),
    maximumRunSeconds: integer("DEVBRIDGE_MAX_RUN_SECONDS", 600, 30, 3600),
    runOnce: process.env.DEVBRIDGE_RUN_ONCE?.toLowerCase() === "true",
  };
}
