# Security boundary

This is a trusted-operator **lab sample**, not a production identity service, security product or inbound authentication validator.

## What the code does

- Browser discovery accepts an explicit same-origin Dify target, uses the existing Studio credential only inside that page, performs GETs and emits allowlisted metadata. It does not collect prompts, workflow definitions, app keys or full raw responses.
- Configuration fixes the Microsoft tenant, public client, operator, blueprint and source workspace. Example configuration cannot enter live mode.
- Preflight has a read-only native operation gate. Its token still needs the registry's delegated read/write permission; the process gate is not a reduction of the token's server-side privileges.
- Apply requires the exact local plan hash and expires that plan after one hour. It fixes target names, IDs, sponsor/owner, build hashes and maximum operations. This is an operator-consent mechanism, not a signed authorization service or protection against someone who can edit this trusted code/local state.
- A create-only pending marker is acquired before apply authentication. A consumed plan cannot be replayed. An uncertain POST poisons the native session; there is no automatic retry, fallback identity or rollback.
- Native HTTP uses fixed Microsoft public-cloud hosts, rejects redirects/cookies, applies response/time/command limits, and checks each allowed request and registration payload. Existing registration IDs are used exactly as returned.
- Identity and registry readbacks verify the IDs, blueprint and binding, not just a display name or HTTP 201.

## Credentials and authentication

No secrets are required in config. Device authentication is interactive, stops on OAuth consent/errors, has no broker/cache fallback and keeps tokens in native process memory. The operator must also stop if the browser asks for consent; code cannot click or inspect that browser screen for them. Device codes appear in the terminal for sign-in and are not written to sample receipts.

JWT claim decoding in the custom adapter is **context checking, not cryptographic signature verification**. Tokens are acquired directly from Microsoft's fixed HTTPS token endpoint; a successful Graph `/me` call is also required to accept the expected operator. Do not reuse this helper to validate externally supplied tokens or to implement a web API's authentication middleware. Offline tests replace HTTP and use synthetic token claims; they do not test Microsoft's live authentication or JWT signatures.

The example targets Microsoft public cloud. Sovereign-cloud endpoints, proxy deployments, managed identities, OBO, app-only credentials and unattended refresh are not implemented. Do not bypass Conditional Access or organizational controls when a scenario fails.

## Local state

Personal configs, source inventories, proposed/approved plans, UPNs, response projections and journals are local sensitive work data. Keep them in the standard ignored local paths, protect directory ACLs, and do not put this working copy in a public or shared sync folder. POSIX new receipt files request mode 0600; Windows protection depends on the enclosing directory ACL.

Do not paste tokens, device codes, config contents or raw journals into public issues. [.gitignore](.gitignore) is a convenience, not a security boundary. Scan and review the exact public file list before publishing.

## Deliberately absent

No invocation endpoint, runtime credential broker, permissions/consent automation, scheduler, deletion, deployment, Dify tool interception or AGT enforcement. A registry record does not prove an agent can run or that revocation/Block behavior is enforced.

See [recovery instructions](docs/TROUBLESHOOTING.md) before handling an interrupted apply. No public vulnerability-reporting address has been designated; do not invent a contact or disclose secrets in an issue.
