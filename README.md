# DevBridge Server

Secure, QR-based Android test delivery from a developer dashboard to remote devices.

DevBridge removes ad-hoc APK sharing through messaging apps, removable drives, and public download links. A developer creates a short-lived session, pairs a tester's phone through a one-time QR code, uploads a signed APK, and receives structured feedback from the paired device. The PC and phone can use completely different networks when the server is deployed behind public HTTPS.

> Development status: functional internal-QA preview. Review the [known limitations](#known-limitations) before production or multi-tenant use.

## What works today

- Named projects, apps, and optional Android package identifiers.
- One-time QR pairing with short-lived, cryptographically random tokens.
- Remote pairing through Azure HTTPS or local pairing over a private LAN.
- Protected developer session creation.
- APK upload with percentage progress and explicit failure reporting.
- APK extension/package-header validation and configurable size limits.
- Server-side SHA-256 calculation.
- Authenticated, one-use download tickets with short expiry.
- Connected-device, build, event, and tester-feedback views.
- Structured crash, ANR, performance, launch, and installation event batches.
- Automatic issue fingerprinting, occurrence counts, and affected-device grouping.
- Evidence-grounded Azure AI issue diagnoses with plain-English impact, probable cause, location, solution, confidence, and limitations.
- Correlated pre-failure action/network breadcrumbs, automatic failure screenshots, and recording keyframes for multimodal diagnosis.
- Azure AI-generated guided test cases that can be assigned to a paired device and build.
- Evidence-linked pass, fail, and blocked test runs with automatic failure diagnosis in the companion.
- Consolidated release verdict, pass rate, diagnosed-failure count, and Markdown report export.
- Secret redaction and managed-identity archival to Azure Table Storage.
- Application Insights and Log Analytics infrastructure for service monitoring.
- One-use, two-minute SDK enrollment handoffs with separate build-scoped telemetry tokens.
- Azure Blob-backed session snapshots that restore devices, builds, SDK clients, and attachment metadata after restarts.
- Authenticated PNG/JPEG screenshot and MP4/WebM recording attachments.
- Automatic session and artifact expiry.
- Reusable Bicep infrastructure and PowerShell deployment scripts.

The companion application adds encrypted device credentials, APK download progress, client-side SHA-256 verification, and Android Package Installer integration.

## Architecture

```text
Developer browser                            Android companion
       │                                            │
       │ HTTPS: create session / upload APK         │ HTTPS: pair / poll
       ▼                                            ▼
                  DevBridge ASP.NET Core API
                  ├── session and device authorization
                  ├── APK validation and hashing
                  ├── one-use download tickets
                  ├── diagnostics and feedback endpoints
                  └── issue fingerprinting and aggregation
                                  │
                  Azure Table Storage + Application Insights
```

Both clients make outbound HTTPS requests. The developer machine does not need a public IP, router port forwarding, VPN, or a direct connection to the phone.

## Repository layout

```text
.
├── infra/
│   ├── main.bicep       Azure infrastructure
│   ├── deploy.ps1       Resource deployment
│   └── publish.ps1      Linux-compatible application packaging/deployment
├── src/DevBridge.Server/
│   ├── Models/          API and domain models
│   ├── Options/         Typed configuration
│   ├── Services/        Sessions, tokens, cleanup, network discovery
│   ├── wwwroot/         Developer and browser-pairing dashboards
│   └── Program.cs       ASP.NET Core endpoints and middleware
└── Directory.Build.props
```

## Requirements

- .NET SDK 10
- Azure CLI and Bicep CLI for cloud deployment
- An Azure subscription for the hosted mode
- PowerShell 5.1+ for the included deployment scripts
- A signed Android APK for testing

## Local development

```powershell
dotnet restore .\src\DevBridge.Server\DevBridge.Server.csproj
dotnet run --project .\src\DevBridge.Server\DevBridge.Server.csproj
```

Open `http://localhost:5074`.

For phone access on a private LAN, publish the workstation's reachable address:

```powershell
$env:DevBridge__PublicBaseUrl = "http://192.168.1.20:5074"
$env:DevBridge__DeveloperAccessKey = "replace-with-a-long-random-secret"
dotnet run --project .\src\DevBridge.Server\DevBridge.Server.csproj
```

Plain HTTP is for private development networks only. Use HTTPS for every public deployment.

## Configuration

Configuration uses the `DevBridge` section and standard ASP.NET Core environment-variable mapping.

| Setting | Environment variable | Default | Purpose |
|---|---|---:|---|
| `PublicBaseUrl` | `DevBridge__PublicBaseUrl` | Auto-detected | Origin encoded into QR and download URLs |
| `DeveloperAccessKey` | `DevBridge__DeveloperAccessKey` | Empty locally | Protects session creation |
| `PairingLifetimeMinutes` | `DevBridge__PairingLifetimeMinutes` | `5` | One-time QR validity |
| `SessionLifetimeHours` | `DevBridge__SessionLifetimeHours` | `8` | Session and artifact retention |
| `MaximumApkMegabytes` | `DevBridge__MaximumApkMegabytes` | `500` | Request and form upload limit |
| `ArtifactPath` | `DevBridge__ArtifactPath` | `Data/Artifacts` | APK storage location |
| `AiEndpoint` | `DevBridge__AiEndpoint` | Empty | Microsoft Foundry/Azure AI inference endpoint |
| `AiModelDeployment` | `DevBridge__AiModelDeployment` | Empty | Model deployment used for structured issue analysis |
| `AiMaximumEvidenceEvents` | `DevBridge__AiMaximumEvidenceEvents` | `40` | Maximum matching events supplied to one diagnosis |

Never commit access keys, connection strings, signing keys, refresh tokens, or Azure credentials.

## Azure deployment

Authenticate first:

```powershell
az login
az account set --subscription "YOUR_SUBSCRIPTION"
```

Create a strong developer access key and deploy the infrastructure:

```powershell
$accessKey = Read-Host "Developer access key" -AsSecureString
.\infra\deploy.ps1 `
  -ResourceGroup "devbridge-rg" `
  -Location "centralindia" `
  -ResourcePrefix "devbridge" `
  -AiFoundryResourceGroupName "YOUR_AI_RESOURCE_GROUP" `
  -AiFoundryAccountName "YOUR_AI_ACCOUNT" `
  -AiFoundryEndpoint "https://YOUR_AI_ACCOUNT.cognitiveservices.azure.com" `
  -AiModelDeployment "YOUR_MODEL_DEPLOYMENT" `
  -DeveloperAccessKey $accessKey
```

Publish the server using the web-app name returned by the deployment:

```powershell
.\infra\publish.ps1 `
  -ResourceGroup "devbridge-rg" `
  -WebAppName "YOUR_WEB_APP_NAME"
```

`publish.ps1` intentionally creates ZIP entries with Linux-compatible forward slashes. Windows `Compress-Archive` can produce backslash paths that prevent an Azure Linux host from locating `wwwroot`.

## API overview

| Endpoint | Authentication | Purpose |
|---|---|---|
| `POST /api/sessions` | Developer access key | Create a pairing session |
| `POST /api/sessions/{id}/pair` | One-time QR token | Register a device |
| `GET /api/sessions/{id}/dashboard` | Dashboard token | Retrieve developer view |
| `POST /api/sessions/{id}/builds` | Dashboard token | Upload an APK |
| `GET /api/sessions/{id}/device` | Device bearer token | Retrieve app/build status |
| `POST /api/sessions/{id}/builds/{buildId}/ticket` | Device bearer token | Issue one-use download URL |
| `POST /api/sessions/{id}/events` | Device bearer token | Submit structured diagnostics |
| `POST /api/sessions/{id}/diagnostics/batch` | Device bearer token | Submit crash/performance event batches |
| `POST /api/sessions/{id}/installations` | Device bearer token | Record Android installation outcomes |
| `POST /api/sessions/{id}/builds/{buildId}/sdk-ticket` | Device bearer token | Create one-use target SDK handoff |
| `POST /api/sdk/exchange` | One-use SDK handoff | Issue a build-scoped SDK token |
| `POST /api/sessions/{id}/attachments` | Device or SDK token | Upload diagnostic evidence |
| `GET /api/sessions/{id}/attachments/{attachmentId}` | Dashboard token | View protected evidence |
| `POST /api/sessions/{id}/feedback` | Device bearer token | Submit tester feedback |
| `POST /api/sessions/{id}/issues/{fingerprint}/analysis` | Dashboard token | Generate or refresh an evidence-grounded AI diagnosis |
| `POST /api/sessions/{id}/test-cases` | Dashboard token | Add a manual guided test case |
| `POST /api/sessions/{id}/test-cases/generate` | Dashboard token | Generate a bounded test plan with Azure AI |
| `POST /api/sessions/{id}/test-runs` | Dashboard token | Assign a test case to a device/build |
| `POST /api/sessions/{id}/test-runs/{runId}/start` | Device bearer token | Start a capture-linked guided run |
| `POST /api/sessions/{id}/test-runs/{runId}/complete` | Device bearer token | Save the verdict and automatically diagnose a failure |
| `GET /api/sessions/{id}/report.md` | Dashboard token | Export the evidence-backed release report |
| `GET /health` | Public | Service health probe |

## Security model

- Pairing, dashboard, device, and download credentials have separate scopes.
- Raw pairing and device credentials are not retained; SHA-256 hashes are stored in memory.
- Azure AI uses the web app's managed identity and the least-privilege inference role; no model API key is stored.
- Diagnostic evidence is treated as untrusted model input, and diagnoses must disclose missing evidence instead of inventing source locations.
- Visual evidence is sent to the configured model only when it is correlated to the issue by fingerprint, capture ID, device/build, or a bounded time window.
- Network breadcrumbs exclude query strings, headers, and bodies; target applications must avoid placing personal data in URL paths.
- Pairing tokens are one-use and expire after five minutes by default.
- Download tickets are one-use and expire after two minutes.
- Public Blob access is disabled by the infrastructure template.
- The web app enforces HTTPS, TLS 1.2+, disables FTP, and uses a managed identity.
- Uploaded names never control server filesystem paths.
- Downloaded APKs are independently verified by the Android companion.

See [SECURITY.md](SECURITY.md) for reporting and deployment guidance.

## Known limitations

- Session snapshots are durable, but concurrent multi-instance session coordination is not yet supported.
- APK files use App Service persistent storage rather than the provisioned Blob container.
- Dashboard and companion updates use polling rather than Azure Web PubSub.
- The developer access key is a single shared bootstrap secret, not user-level identity or RBAC.
- APK validation checks the ZIP/APK header and hash, but does not yet verify Android signing certificates or scan for malware.
- The target application must include the opt-in Flutter diagnostics package to report its private runtime errors; Android does not allow the companion to read another app's logcat.

## Roadmap

### Reliability and scale

- Azure Blob Storage for artifacts with managed-identity authorization.
- Multi-instance optimistic concurrency and transactional session state.
- Azure Web PubSub with HTTPS polling fallback.
- Idempotent uploads, resumable large-file transfer, retention policies, and audit logs.

### Developer workflow

- Android Studio plugin and local desktop agent.
- Watch Android build-output folders and publish successful builds automatically.
- Azure DevOps and GitHub Actions build/sign/distribute templates.
- Release notes generated from commits and build provenance tied to Git SHAs.

### Diagnostics

- Native Android SDK and Android 11+ historical exit/ANR ingestion.
- Redacted HTTP breadcrumbs and persistent offline event buffering.
- Screenshot annotation, voice/text feedback, and source-line navigation.
- Notifications and issue integrations for GitHub, Azure Boards, and Jira.

### Enterprise controls

- Microsoft Entra ID, organizations, projects, teams, and role-based access.
- Signing-certificate allowlists, malware scanning, and policy enforcement.
- Android Enterprise support for managed-device installation.
- Multi-region deployments, private endpoints, compliance retention, and export controls.

## Validation

```powershell
dotnet format .\src\DevBridge.Server\DevBridge.Server.csproj --verify-no-changes --no-restore
dotnet build .\src\DevBridge.Server\DevBridge.Server.csproj --configuration Release
```

CI runs the same checks for pull requests and pushes to `main`.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request.

## License

No open-source license has been assigned yet. Until a license is added, all rights are reserved by the repository owner.
