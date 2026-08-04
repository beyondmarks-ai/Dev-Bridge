# DevBridge Automation Runner

The runner executes safety-bounded Android test plans on a local Android Studio emulator or dedicated USB device. It connects outbound to DevBridge over HTTPS and controls Android through a local Appium 3 server with the official UiAutomator2 driver. The included launcher binds Appium to `127.0.0.1`; ADB and Appium must never be exposed to the public internet.

## Prerequisites

- Node.js 20.19 or newer.
- Android Studio and Android SDK platform tools.
- A complete JDK 17 installation. The Windows launcher auto-detects Android Studio's bundled JDK or Microsoft OpenJDK; `JAVA_HOME` overrides detection.
- A running Android emulator or dedicated USB-debugging device.
- `adb devices` shows the configured device as `device`.
- A DevBridge session with a package name, uploaded APK, AI-generated executable test, and one-use runner enrollment token.

## Setup

```powershell
cd automation-runner
npm ci
npm run setup:android
Copy-Item .env.example .env
```

Set the environment values in the current shell or use your preferred local secret manager. The runner intentionally does not persist its scoped agent token. Do not commit `.env`.

Start Appium in one terminal:

```powershell
npm run appium
```

The setup script keeps Appium extensions under `~/.devbridge/appium`, outside the repository dependency tree. Override that location with `DEVBRIDGE_APPIUM_HOME` when runner isolation requires it.

Start the runner in another terminal after copying a fresh enrollment token from the dashboard:

```powershell
$env:DEVBRIDGE_URL = 'https://your-devbridge.azurewebsites.net'
$env:DEVBRIDGE_ENROLLMENT_TOKEN = 'ONE_USE_TOKEN'
$env:ANDROID_DEVICE_NAME = 'emulator-5554'
npm start
```

Set `DEVBRIDGE_RUN_ONCE=true` for CI jobs that should execute one assigned run and exit.

By default only `safe` steps run. Enable `controlled` steps only in an isolated QA environment:

```powershell
$env:DEVBRIDGE_ALLOW_CONTROLLED = 'true'
```

`forbidden` steps never run. Secrets referenced by a plan must start with `TEST_` and are read only from the runner environment.

## Locator order

1. Accessibility/semantic identifier.
2. Android resource ID.
3. Exact visible text.

Each action waits for a displayed element, uploads a screenshot, reports step progress, and verifies assertions. A failed step produces correlated evidence for DevBridge's Azure AI diagnosis.

## Validation

```powershell
npm run check
npm test
npm run build
```
