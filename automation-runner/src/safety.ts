import { AUTOMATION_ACTIONS, type AutomationStep, type RunnerConfig } from "./contracts.js";

const dangerousWords = /\b(buy|purchase|pay now|place order|transfer|send money|delete account|production)\b/i;

export class SafetyViolation extends Error {}

function refuse(message: string): never { throw new SafetyViolation(message); }

export function validatePlan(steps: AutomationStep[], config: RunnerConfig): void {
  if (steps.length === 0) refuse("The test has no executable automation steps.");
  if (steps.length > config.maximumSteps) refuse(`The plan exceeds the ${config.maximumSteps}-step safety limit.`);
  for (const [index, step] of steps.entries()) {
    if (step.sequence !== index + 1) refuse(`Step ${index + 1} has an invalid sequence number.`);
    if (!AUTOMATION_ACTIONS.includes(step.action)) refuse(`Step ${step.sequence} uses an unsupported action.`);
    if (step.risk === "forbidden") refuse(`Step ${step.sequence} is forbidden by the safety policy.`);
    if (step.risk === "controlled" && !config.allowControlled) {
      refuse(`Step ${step.sequence} requires DEVBRIDGE_ALLOW_CONTROLLED=true.`);
    }
    const description = `${step.target ?? ""} ${step.value ?? ""} ${step.expected ?? ""}`;
    if (dangerousWords.test(description) && step.risk === "safe") {
      refuse(`Step ${step.sequence} appears destructive but is classified as safe.`);
    }
    if (step.secret && !/^TEST_[A-Z0-9_]{1,95}$/.test(step.secret)) {
      refuse(`Step ${step.sequence} has an invalid secret reference.`);
    }
    if (step.timeoutSeconds < 1 || step.timeoutSeconds > 30) {
      refuse(`Step ${step.sequence} has an invalid timeout.`);
    }
  }
}

export function resolveInput(step: AutomationStep): string {
  if (!step.secret) return step.value ?? "";
  const value = process.env[step.secret];
  if (!value) throw new Error(`Required test secret ${step.secret} is not configured.`);
  return value;
}
