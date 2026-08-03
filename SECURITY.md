# Security Policy

## Supported versions

DevBridge is currently an internal-QA preview. Only the latest commit on `main` receives security fixes.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Contact the repository owner privately with:

- Affected version or commit.
- Reproduction steps.
- Expected and observed behavior.
- Potential impact.
- Suggested mitigation, if available.

Do not include real access keys, tokens, tester information, APK signing keys, or production artifacts in a report.

## Deployment guidance

- Require HTTPS for every public endpoint.
- Generate a long random developer access key and store it outside source control.
- Restrict Azure permissions using managed identities and least privilege.
- Rotate any credential that appears in logs, chat, screenshots, shell history, or commits.
- Treat uploaded APKs and diagnostic events as untrusted input.
- Add durable identity, rate limiting, audit logging, malware scanning, and signing-certificate verification before opening a deployment to untrusted users.
