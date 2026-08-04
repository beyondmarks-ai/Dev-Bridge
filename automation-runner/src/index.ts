import { setTimeout as delay } from "node:timers/promises";
import { loadConfig } from "./config.js";
import { DevBridgeApi } from "./api.js";
import { AutomationExecutor } from "./executor.js";

async function main(): Promise<void> {
  const config = loadConfig();
  console.log(`Enrolling ${config.agentName} with ${config.devBridgeUrl.origin}…`);
  const api = await DevBridgeApi.enroll(config);
  if (!api.packageName) throw new Error("The DevBridge session has no target Android package name.");
  console.log(`Runner enrolled. Polling until ${api.expiresAt.toISOString()}.`);
  const executor = new AutomationExecutor(config, api);
  while (Date.now() < api.expiresAt.getTime()) {
    const snapshot = await api.snapshot();
    const run = snapshot.testRuns.find((item) => item.status === "ready" && item.testCase.automationSteps.length > 0);
    if (!run) { await delay(config.pollMs); continue; }
    const build = run.buildId ? snapshot.builds.find((item) => item.buildId === run.buildId) : snapshot.builds[0];
    if (!build) {
      console.error(`Run ${run.id} is waiting because no APK build is available.`);
      await delay(config.pollMs);
      continue;
    }
    console.log(`Running “${run.testCase.title}” on ${config.deviceName}.`);
    try {
      await executor.execute(run, build, snapshot.packageName ?? api.packageName);
      console.log(`Run ${run.id} passed.`);
    } catch (error) {
      console.error(`Run ${run.id} failed:`, error instanceof Error ? error.message : error);
    }
    if (config.runOnce) return;
  }
  console.log("The DevBridge session expired; runner stopped safely.");
}

main().catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : error);
  process.exitCode = 1;
});
