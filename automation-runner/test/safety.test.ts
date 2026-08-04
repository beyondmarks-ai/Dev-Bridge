import assert from "node:assert/strict";
import test from "node:test";
import type { AutomationStep, RunnerConfig } from "../src/contracts.js";
import { resolveInput, validatePlan } from "../src/safety.js";

const config = {
  maximumSteps: 60,
  allowControlled: false,
} as RunnerConfig;

const step = (values: Partial<AutomationStep> = {}): AutomationStep => ({
  sequence: 1,
  action: "tap",
  target: "login.submit",
  value: null,
  secret: null,
  expected: null,
  timeoutSeconds: 10,
  risk: "safe",
  ...values,
});

test("accepts a bounded safe plan", () => {
  assert.doesNotThrow(() => validatePlan([step()], config));
});

test("rejects forbidden and disguised destructive steps", () => {
  assert.throws(() => validatePlan([step({ risk: "forbidden" })], config), /forbidden/);
  assert.throws(() => validatePlan([step({ target: "Pay now" })], config), /destructive/);
});

test("requires explicit permission for controlled steps", () => {
  assert.throws(() => validatePlan([step({ risk: "controlled" })], config), /ALLOW_CONTROLLED/);
  assert.doesNotThrow(() => validatePlan([step({ risk: "controlled" })], { ...config, allowControlled: true }));
});

test("resolves only TEST_ secret references", () => {
  process.env.TEST_AUTOMATION_EMAIL = "qa@example.invalid";
  assert.equal(resolveInput(step({ action: "type", secret: "TEST_AUTOMATION_EMAIL" })), "qa@example.invalid");
  assert.throws(() => validatePlan([step({ secret: "PROD_PASSWORD" })], config), /invalid secret/);
  delete process.env.TEST_AUTOMATION_EMAIL;
});
