## Summary

Describe what changed and why.

## Validation

- [ ] `dotnet format src/DevBridge.Server/DevBridge.Server.csproj --verify-no-changes`
- [ ] `dotnet build src/DevBridge.Server/DevBridge.Server.csproj --configuration Release`
- [ ] `az bicep build --file infra/main.bicep`
- [ ] No credentials, generated packages, or local data are included

## Notes

List deployment considerations, screenshots, API changes, or follow-up work.
