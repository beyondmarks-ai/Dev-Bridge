# Contributing to DevBridge Server

Thank you for improving DevBridge.

## Development workflow

1. Create a focused branch from `main`.
2. Keep secrets and generated artifacts out of the repository.
3. Make the smallest coherent change that solves the issue.
4. Update documentation when behavior, configuration, API contracts, or security assumptions change.
5. Run formatting and the Release build before opening a pull request.

```powershell
dotnet restore .\src\DevBridge.Server\DevBridge.Server.csproj
dotnet format .\src\DevBridge.Server\DevBridge.Server.csproj --verify-no-changes --no-restore
dotnet build .\src\DevBridge.Server\DevBridge.Server.csproj --configuration Release --no-restore
```

## Pull requests

Include:

- A concise problem statement and solution summary.
- Testing performed and relevant output.
- Security, compatibility, deployment, and migration impacts.
- Screenshots for dashboard changes.
- API examples when contracts change.

Do not include Azure subscription IDs, tenant IDs, app secrets, access keys, signing material, production URLs, tester data, or real diagnostic payloads.

## Coding conventions

- Nullable reference types and warnings-as-errors remain enabled.
- Prefer explicit validation at trust boundaries.
- Use cryptographically secure randomness for credentials.
- Preserve one-use and expiry guarantees when changing authentication flows.
- Avoid logging tokens, APK URLs, authorization headers, or sensitive tester data.
