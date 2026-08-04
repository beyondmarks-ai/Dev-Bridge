import { existsSync, mkdirSync, readdirSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const runnerRoot = resolve(scriptDirectory, "..");
const appiumEntry = join(runnerRoot, "node_modules", "appium", "index.js");
const appiumHome = process.env.DEVBRIDGE_APPIUM_HOME?.trim() || join(homedir(), ".devbridge", "appium");
mkdirSync(appiumHome, { recursive: true });

const inheritedSdk = process.env.ANDROID_HOME?.trim() || process.env.ANDROID_SDK_ROOT?.trim();
const windowsSdk = process.env.LOCALAPPDATA?.trim()
  ? join(process.env.LOCALAPPDATA, "Android", "Sdk")
  : "";
const androidSdk = inheritedSdk || (windowsSdk && existsSync(windowsSdk) ? windowsSdk : "");
if (!androidSdk) {
  console.error("Android SDK not found. Set ANDROID_HOME or ANDROID_SDK_ROOT before starting Appium.");
  process.exit(1);
}
const javaExecutable = process.platform === "win32" ? "java.exe" : "java";
const isUsableJavaHome = (candidate) => Boolean(candidate) &&
  existsSync(join(candidate, "bin", javaExecutable)) &&
  existsSync(join(candidate, "lib", "jvm.cfg"));
const inheritedJava = process.env.JAVA_HOME?.trim();
const androidStudioJava = process.env.ProgramFiles?.trim()
  ? join(process.env.ProgramFiles, "Android", "Android Studio", "jbr")
  : "";
const microsoftJavaRoot = process.env.ProgramFiles?.trim()
  ? join(process.env.ProgramFiles, "Microsoft")
  : "";
const microsoftJava = microsoftJavaRoot && existsSync(microsoftJavaRoot)
  ? readdirSync(microsoftJavaRoot, { withFileTypes: true })
      .filter((entry) => entry.isDirectory() && entry.name.startsWith("jdk-"))
      .map((entry) => join(microsoftJavaRoot, entry.name))
      .filter(isUsableJavaHome)
      .sort()
      .at(-1) ?? ""
  : "";
const javaHome = [inheritedJava, androidStudioJava, microsoftJava].find(isUsableJavaHome) ?? "";
if (!javaHome) {
  console.error("A complete JDK was not found. Set JAVA_HOME, install Microsoft OpenJDK 17, or repair Android Studio's bundled JDK.");
  process.exit(1);
}

const requested = process.argv.slice(2);
const command = requested[0] ?? "";
const extensionCommand = ["driver", "plugin", "setup"].includes(command);
const serverArgs = requested.some((value) => value === "--address" || value.startsWith("--address="))
  ? requested
  : ["--address", "127.0.0.1", ...requested];
const args = command === "setup"
  ? ["driver", "install", "uiautomator2"]
  : extensionCommand || requested.includes("--version")
    ? requested
    : serverArgs;

const child = spawn(process.execPath, [appiumEntry, ...args], {
  cwd: appiumHome,
  env: {
    ...process.env,
    APPIUM_HOME: appiumHome,
    ANDROID_HOME: androidSdk,
    ANDROID_SDK_ROOT: androidSdk,
    JAVA_HOME: javaHome,
  },
  stdio: "inherit",
  windowsHide: true,
});

child.once("error", (error) => {
  console.error(`Could not start Appium: ${error.message}`);
  process.exitCode = 1;
});
child.once("exit", (code, signal) => {
  if (signal) console.error(`Appium stopped by signal ${signal}.`);
  process.exitCode = code ?? 1;
});
