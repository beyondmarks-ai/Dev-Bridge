import { createHash } from "node:crypto";
import { createWriteStream } from "node:fs";
import { readFile, unlink } from "node:fs/promises";
import { basename, join } from "node:path";
import { tmpdir } from "node:os";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";
import type { Build, DeviceSnapshot, RunnerConfig, RunnerCredentials, StepResult, TestRun } from "./contracts.js";

export class DevBridgeApi {
  private constructor(private readonly credentials: RunnerCredentials) {}

  static async enroll(config: RunnerConfig): Promise<DevBridgeApi> {
    const response = await fetch(new URL("/api/automation/exchange", config.devBridgeUrl), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        enrollmentToken: config.enrollmentToken,
        agentName: config.agentName,
        platformVersion: process.platform,
        capabilities: {
          automationName: "UiAutomator2",
          deviceName: config.deviceName,
          runnerVersion: "0.1.0",
        },
      }),
    });
    if (!response.ok) throw new Error(`Runner enrollment failed (HTTP ${response.status}).`);
    return new DevBridgeApi((await response.json()) as RunnerCredentials);
  }

  get packageName(): string | null { return this.credentials.packageName; }
  get expiresAt(): Date { return new Date(this.credentials.sessionExpiresAt); }

  private async request(path: string, init: RequestInit = {}): Promise<Response> {
    const headers = new Headers(init.headers);
    headers.set("Authorization", `Bearer ${this.credentials.agentToken}`);
    const response = await fetch(new URL(path, this.credentials.baseUrl), { ...init, headers });
    if (response.status === 401) throw new Error("Runner authorization expired.");
    return response;
  }

  async snapshot(): Promise<DeviceSnapshot> {
    const response = await this.request(`/api/sessions/${this.credentials.sessionId}/device`);
    if (!response.ok) throw new Error(`Could not poll test jobs (HTTP ${response.status}).`);
    return (await response.json()) as DeviceSnapshot;
  }

  async start(run: TestRun, captureId: string): Promise<void> {
    const response = await this.request(`/api/sessions/${this.credentials.sessionId}/test-runs/${run.id}/start`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ captureId }),
    });
    if (!response.ok) throw new Error(`Could not claim test run (HTTP ${response.status}).`);
  }

  async progress(run: TestRun, stepIndex: number, totalSteps: number, result: StepResult): Promise<void> {
    const response = await this.request(`/api/sessions/${this.credentials.sessionId}/test-runs/${run.id}/progress`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ stepIndex, totalSteps, status: result.status, message: result.message, fingerprint: result.fingerprint ?? null }),
    });
    if (!response.ok) throw new Error(`Could not report step progress (HTTP ${response.status}).`);
  }

  async complete(run: TestRun, verdict: "passed" | "failed" | "blocked", notes: string, captureId: string): Promise<void> {
    const response = await this.request(`/api/sessions/${this.credentials.sessionId}/test-runs/${run.id}/complete`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ verdict, notes, captureId }),
    });
    if (!response.ok) throw new Error(`Could not complete test run (HTTP ${response.status}).`);
  }

  async downloadBuild(build: Build): Promise<string> {
    const ticketResponse = await this.request(`/api/sessions/${this.credentials.sessionId}/builds/${build.buildId}/ticket`, { method: "POST" });
    if (!ticketResponse.ok) throw new Error(`Could not authorize APK download (HTTP ${ticketResponse.status}).`);
    const ticket = (await ticketResponse.json()) as { downloadUrl: string };
    const response = await fetch(ticket.downloadUrl);
    if (!response.ok || !response.body) throw new Error(`APK download failed (HTTP ${response.status}).`);
    const path = join(tmpdir(), `devbridge-${build.buildId}.apk`);
    await pipeline(Readable.fromWeb(response.body as never), createWriteStream(path));
    const digest = createHash("sha256").update(await readFile(path)).digest("hex");
    if (digest.toUpperCase() !== build.sha256.toUpperCase()) {
      await unlink(path).catch(() => undefined);
      throw new Error("Downloaded APK failed SHA-256 verification.");
    }
    return path;
  }

  async uploadScreenshot(path: string, captureId: string, fingerprint?: string): Promise<string> {
    const form = new FormData();
    form.set("kind", "automation-step");
    form.set("captureId", captureId);
    form.set("capturedAt", new Date().toISOString());
    if (fingerprint) form.set("fingerprint", fingerprint);
    form.set("file", new Blob([await readFile(path)], { type: "image/png" }), basename(path));
    const response = await this.request(`/api/sessions/${this.credentials.sessionId}/attachments`, { method: "POST", body: form });
    if (!response.ok) throw new Error(`Screenshot upload failed (HTTP ${response.status}).`);
    const result = (await response.json()) as { attachmentId: string };
    return result.attachmentId;
  }
}
