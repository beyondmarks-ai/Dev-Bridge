import { mkdir, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { remote, type Browser } from "webdriverio";
import type { AutomationStep, Build, RunnerConfig, StepResult, TestRun } from "./contracts.js";
import { DevBridgeApi } from "./api.js";
import { resolveInput, SafetyViolation, validatePlan } from "./safety.js";

type AndroidBrowser = Browser;

export class AutomationExecutor {
  constructor(private readonly config: RunnerConfig, private readonly api: DevBridgeApi) {}

  async execute(run: TestRun, build: Build, packageName: string): Promise<void> {
    const steps = run.testCase.automationSteps;
    const captureId = `automation-${run.id}-${Date.now()}`;
    await this.api.start(run, captureId);
    let apkPath: string | undefined;
    let driver: AndroidBrowser | undefined;
    let currentSequence = 0;
    const started = Date.now();
    try {
      validatePlan(steps, this.config);
      apkPath = await this.api.downloadBuild(build);
      driver = await this.createDriver(apkPath, packageName);
      for (const step of steps) {
        currentSequence = step.sequence;
        if (Date.now() - started > this.config.maximumRunSeconds * 1000) throw new Error("The automation run exceeded its safety timeout.");
        const result = await this.runStep(driver, packageName, step);
        await this.capture(driver, run, step.sequence, captureId, result.fingerprint);
        await this.api.progress(run, step.sequence, steps.length, result);
        if (result.status === "failed") throw new StepFailure(result.message);
        if (step.action === "stop") break;
      }
      await this.api.complete(run, "passed", `All ${steps.length} automation steps passed.`, captureId);
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      const sequence = Math.max(1, currentSequence);
      if (error instanceof SafetyViolation) {
        await this.api.progress(run, sequence, steps.length, { status: "skipped", message }).catch(() => undefined);
        await this.api.complete(run, "blocked", message, captureId).catch(() => undefined);
        throw error;
      }
      if (!(error instanceof StepFailure)) {
        const fingerprint = `automation-${run.id}-failure`;
        if (driver) await this.capture(driver, run, sequence, captureId, fingerprint).catch(() => undefined);
        await this.api.progress(run, sequence, steps.length, { status: "failed", message, fingerprint }).catch(() => undefined);
      }
      await this.api.complete(run, "failed", message, captureId).catch(() => undefined);
      throw error;
    } finally {
      if (driver) await driver.deleteSession().catch(() => undefined);
      if (apkPath) await unlink(apkPath).catch(() => undefined);
    }
  }

  private async createDriver(apkPath: string, packageName: string): Promise<AndroidBrowser> {
    const path = this.config.appiumUrl.pathname === "/" ? "/" : this.config.appiumUrl.pathname;
    return remote({
      protocol: this.config.appiumUrl.protocol.replace(":", "") as "http" | "https",
      hostname: this.config.appiumUrl.hostname,
      port: Number(this.config.appiumUrl.port || (this.config.appiumUrl.protocol === "https:" ? 443 : 80)),
      path,
      logLevel: "warn",
      capabilities: {
        platformName: "Android",
        "appium:automationName": "UiAutomator2",
        "appium:deviceName": this.config.deviceName,
        "appium:udid": this.config.deviceName,
        ...(this.config.platformVersion ? { "appium:platformVersion": this.config.platformVersion } : {}),
        "appium:app": apkPath,
        "appium:appPackage": packageName,
        "appium:autoGrantPermissions": true,
        "appium:noReset": false,
        "appium:adbExecTimeout": 60_000,
        "appium:appWaitDuration": 60_000,
        "appium:newCommandTimeout": 300,
      },
    });
  }

  private async runStep(driver: AndroidBrowser, packageName: string, step: AutomationStep): Promise<StepResult> {
    const timeout = step.timeoutSeconds * 1000;
    try {
      switch (step.action) {
        case "resetApp":
          await driver.terminateApp(packageName).catch(() => false);
          await driver.execute("mobile: clearApp", { appId: packageName });
          break;
        case "launchApp": await driver.activateApp(packageName); break;
        case "tap": await (await this.find(driver, packageName, this.target(step), timeout)).click(); break;
        case "type": await (await this.find(driver, packageName, this.target(step), timeout)).setValue(resolveInput(step)); break;
        case "clear": await (await this.find(driver, packageName, this.target(step), timeout)).clearValue(); break;
        case "back": await driver.back(); break;
        case "waitFor": await this.find(driver, packageName, this.target(step), timeout); break;
        case "assertVisible": await this.find(driver, packageName, this.target(step), timeout); break;
        case "assertNotVisible": {
          const element = await this.findOptional(driver, packageName, this.target(step));
          if (element && await element.isDisplayed()) throw new Error(`${step.target} is still visible.`);
          break;
        }
        case "assertEnabled": {
          const element = await this.find(driver, packageName, this.target(step), timeout);
          if (!await element.isEnabled()) throw new Error(`${step.target} is disabled.`);
          break;
        }
        case "assertText": {
          const element = await this.find(driver, packageName, this.target(step), timeout);
          const actual = await element.getText();
          if (actual.trim() !== (step.expected ?? "").trim()) throw new Error(`Expected “${step.expected ?? ""}” but found “${actual}”.`);
          break;
        }
        case "scroll":
          await driver.execute("mobile: scrollGesture", { left: 50, top: 200, width: 900, height: 1400, direction: step.value === "up" ? "up" : "down", percent: 0.75 });
          break;
        case "swipe":
          await driver.execute("mobile: swipeGesture", { left: 50, top: 200, width: 900, height: 1400, direction: step.value ?? "up", percent: 0.7 });
          break;
        case "takeScreenshot": case "stop": break;
      }
      return { status: "passed", message: `Step ${step.sequence} ${step.action} passed.` };
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      return { status: "failed", message: `Step ${step.sequence} ${step.action} failed: ${detail}`, fingerprint: `step-${step.sequence}-${step.action}` };
    }
  }

  private target(step: AutomationStep): string {
    if (!step.target?.trim()) throw new Error(`Step ${step.sequence} requires a target.`);
    return step.target.trim();
  }

  private async find(driver: AndroidBrowser, packageName: string, target: string, timeout: number) {
    const selectors = this.selectors(packageName, target);
    let lastError: unknown;
    for (const selector of selectors) {
      try {
        const element = await driver.$(selector);
        await element.waitForDisplayed({ timeout: Math.max(750, Math.floor(timeout / selectors.length)), interval: 200 });
        return element;
      } catch (error) { lastError = error; }
    }
    throw new Error(`Element “${target}” was not found. ${lastError instanceof Error ? lastError.message : ""}`.trim());
  }

  private async findOptional(driver: AndroidBrowser, packageName: string, target: string) {
    for (const selector of this.selectors(packageName, target)) {
      const element = await driver.$(selector);
      if (await element.isExisting()) return element;
    }
    return undefined;
  }

  private selectors(packageName: string, target: string): string[] {
    const escaped = target.replaceAll("\\", "\\\\").replaceAll('"', '\\"');
    const resource = target.includes(":id/") ? target : `${packageName}:id/${target.replaceAll(".", "_")}`;
    return [`~${target}`, `id=${resource}`, `android=new UiSelector().text("${escaped}")`];
  }

  private async capture(driver: AndroidBrowser, run: TestRun, sequence: number, captureId: string, fingerprint?: string): Promise<void> {
    const directory = join(tmpdir(), "devbridge-automation");
    await mkdir(directory, { recursive: true });
    const path = join(directory, `${run.id}-${sequence}-${Date.now()}.png`);
    await writeFile(path, await driver.takeScreenshot(), "base64");
    try { await this.api.uploadScreenshot(path, captureId, fingerprint); }
    finally { await unlink(path).catch(() => undefined); }
  }
}

class StepFailure extends Error {}
