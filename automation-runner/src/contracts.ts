export const AUTOMATION_ACTIONS = [
  "resetApp",
  "launchApp",
  "tap",
  "type",
  "clear",
  "scroll",
  "swipe",
  "back",
  "waitFor",
  "assertVisible",
  "assertText",
  "assertNotVisible",
  "assertEnabled",
  "takeScreenshot",
  "stop",
] as const;

export type AutomationAction = (typeof AUTOMATION_ACTIONS)[number];
export type StepRisk = "safe" | "controlled" | "forbidden";

export interface AutomationStep {
  sequence: number;
  action: AutomationAction;
  target: string | null;
  value: string | null;
  secret: string | null;
  expected: string | null;
  timeoutSeconds: number;
  risk: StepRisk;
}

export interface TestCase {
  id: string;
  title: string;
  instructions: string;
  expectedResult: string;
  priority: string;
  automationSteps: AutomationStep[];
}

export interface TestRun {
  id: string;
  testCaseId: string;
  deviceId: string;
  buildId: string | null;
  status: "ready" | "running" | "passed" | "failed" | "blocked";
  captureId: string | null;
  testCase: TestCase;
}

export interface Build {
  buildId: string;
  versionName: string;
  versionCode: number;
  sizeBytes: number;
  sha256: string;
}

export interface DeviceSnapshot {
  id: string;
  projectName: string;
  appName: string;
  packageName: string | null;
  expiresAt: string;
  builds: Build[];
  testRuns: TestRun[];
}

export interface RunnerCredentials {
  baseUrl: string;
  sessionId: string;
  agentId: string;
  agentToken: string;
  packageName: string | null;
  sessionExpiresAt: string;
}

export interface RunnerConfig {
  devBridgeUrl: URL;
  enrollmentToken: string;
  agentName: string;
  appiumUrl: URL;
  deviceName: string;
  platformVersion?: string;
  pollMs: number;
  allowControlled: boolean;
  maximumSteps: number;
  maximumRunSeconds: number;
  runOnce: boolean;
}

export interface StepResult {
  status: "passed" | "failed" | "skipped";
  message: string;
  fingerprint?: string;
}
