# Dify discovery to Agent 365: reproduce the lab

Discover the apps that actually exist in **your Dify workspace**, then create or reuse the corresponding **Entra agent identities and Agent 365 registrations**. This is an on-demand lab sample, not a runtime gateway or unattended service.

```text
Your signed-in Dify Studio
  -> complete console-API metadata checkpoint
  -> read-only Entra / A365 preflight
  -> review the exact plan and its hash
  -> explicitly approved, bounded registration
  -> identity + registration readbacks
```

**Validation boundary:** the portable source has offline tests and a local build. The original lab-specific implementation completed a five-app live demonstration; that does not prove this parameterized version has run in a second tenant. Fresh-lab end-to-end execution is still unverified. No production-readiness claim is made.

## What you need

| Requirement | Why |
|---|---|
| Git, Python 3.11+, .NET 8 SDK | Clone, run the standard-library Python host, and compile the native helper. No Python packages are required. |
| Node.js 20+ (tests only) | Test the browser metadata collector without contacting Dify. |
| An isolated Dify **0.15.4** lab and a signed-in Studio account | This collector uses that version's console API and browser session. It is not a Dify application API-key client. |
| A workspace containing **1-5 apps** with distinct trimmed names | Deliberate write and review bound. No silent truncation or selected-ID fixture is used. |
| An eligible Microsoft tenant with Agent ID and Agent 365 registry access | Feature rollout, licensing, operator roles and Conditional Access must permit your scenario. Cloning this repo grants none of them. |
| An existing agent identity blueprint, a tenant-owned public client application, and an authorized operator | The sample does not create the blueprint/client, grant consent, assign roles or configure permissions. |
| Direct outbound HTTPS to Microsoft login and Graph | The native helper rejects redirects and does not use an HTTP proxy. Do not weaken organizational network or authentication controls. |

Dify 0.15.4 is a historical compatibility target, **not a recommendation to deploy an old version on the public internet**. Use an existing isolated lab, HTTPS or a loopback tunnel that you control. Newer Dify releases are not claimed compatible. The Studio and `/console/api` must be on the same origin.

### Microsoft prerequisites: arrange these before running the sample

1. Record your tenant ID and the **application ID** of an existing agent identity blueprint. See [create a blueprint](https://learn.microsoft.com/entra/agent-id/create-blueprint).
2. Arrange a tenant-owned public client with device-code sign-in allowed, and record its **application/client ID**, not its service-principal object ID. See [custom client registration](https://learn.microsoft.com/microsoft-agent-365/developer/custom-client-app-registration). That article describes broader CLI setup; do not indiscriminately grant all its permissions for this sample.
3. Have your administrator verify the delegated permissions used by this implementation:
   - `User.Read`: verify the signed-in operator.
   - `AgentIdentity.Read.All`: lookup and read identity bindings.
   - `AgentRegistration.ReadWrite.All`: the pinned registry read/write path requires this permission even for the read-only preflight.
   - `AgentIdentity.Create.All`: this sample's delegated identity-create guard additionally requires it when a new identity is planned. Confirm availability/authorization in your tenant. If unavailable, have an authorized administrator create the identities separately, then supply their explicit bindings for reuse.
4. Record the operator's user object ID and UPN. The operator becomes the **sponsor of each new identity and owner/creator of its registration**. Review this choice before applying.
5. Permissions must already be consented and the operator must be authorized for the blueprint and operations. Sign-in requests the existing Graph `.default` grant, plus `openid profile`. **Stop on a consent screen or an unexpected tenant/account/application.** This sample does not automate consent or repair permissions.

The current official [identity creation guide](https://learn.microsoft.com/entra/agent-id/create-delete-agent-identities) also describes a blueprint-credential flow. This lab deliberately retains the original delegated-operator path; it does not silently switch authentication models when that path is unavailable.

## 1. Clone and check the offline sample

Run these commands from a terminal. Use `python3` instead of `python` if that is your Python 3.11+ executable.

```sh
git clone https://github.com/zenc-cp/dify-a365-discovery-wrapper.git
cd dify-a365-discovery-wrapper
python --version
dotnet --version
python -B lab.py --help
python -B lab.py check --config lab.example.json --inventory examples/inventory.synthetic.json
python -B -m unittest discover -s tests -v
node --test tests/discovery.test.cjs
```

Expected local check: `local_inputs_valid`, `source_apps: 2`, `cloud_calls: 0`. The examples use synthetic identifiers and an `.invalid` domain. They cannot enter live mode and are not evidence that your tenant is ready.

## 2. Install the public dependency and build

Review your organization's package policy before installing. These commands install the **public Agent 365 CLI 1.1.221** under this clone, not globally. They do not sign in or modify a tenant.

```sh
dotnet tool install Microsoft.Agents.A365.DevTools.Cli --version 1.1.221 --tool-path .tools/a365 --add-source https://api.nuget.org/v3/index.json
python -B lab.py build
dotnet native/bin/Release/net8.0/WrapperNative.dll --self-test
```

Expected: build succeeds and self-tests report `live_http: false`. No DLLs are distributed by this repo. The helper compiles against the installed public package; its build uses no additional NuGet package feeds.

Already have **exactly 1.1.221** installed in a different local directory? Point the build at its `tools/net8.0/any` directory:

```sh
python -B lab.py build --cli-package "/absolute/path/to/tools/net8.0/any"
```

Do not substitute a newer CLI casually: the sample calls the version-pinned internal `GraphApiService.RegisterAgentInstanceAsyncV2` method. It is not a stable public integration SDK. The native payload guard intentionally stops if its contract changes.

## 3. Discover your Dify apps, not a preselected list

1. Open **your Dify Studio** in your normal browser, sign in and choose the workspace you want to register.
2. Read [the metadata collector](tools/discover-dify.js), then paste that file's contents into the browser's developer console. Do not disable an organizational browser policy to do this. This defines a function; it makes no requests yet.
3. Call it with the exact origin visible in your address bar (scheme, hostname and optional port, no path or trailing slash):

```js
const inventory = await difyA365Discover("https://YOUR-DIFY-ORIGIN");
console.table(inventory.apps);
copy(JSON.stringify(inventory, null, 2));
```

`copy()` is the Chromium DevTools clipboard helper, not a function supplied by this repo. If your browser lacks it, display `JSON.stringify(inventory, null, 2)` and save that metadata text manually.

4. Create a private local directory and save the copied JSON as `.local/inventory.json`:

```sh
python -c "from pathlib import Path; Path('.local').mkdir(exist_ok=True)"
```

The collector reads `/console/api/workspaces/current` and all pages of `/console/api/apps` with `mode=all` and `is_created_by_me=false`. It projects **ID, name and mode only**, rechecks the workspace, and rejects incomplete pagination. The existing Studio token stays in page memory and is never part of the result. Do not paste a token into a terminal, config file or issue.

Check `complete: true`, the total, workspace ID and names against Studio. The registration lab rejects more than five apps rather than hiding any. Use a separate small lab workspace if necessary. App types can include Chatbot and Chatflow; discovery does not mean every item uses Dify's Agent mode.

## 4. Configure your own lab

Copy [the configuration example](lab.example.json) to a private, ignored local file:

```sh
python -c "from pathlib import Path; Path('lab.local.json').write_bytes(Path('lab.example.json').read_bytes())"
```

Edit `lab.local.json` once. Replace **every** example value:

| Field | Value to use |
|---|---|
| `dify_origin` | Exact `instance_url` from your checkpoint. |
| `workspace_id` | Exact workspace UUID from the checkpoint. |
| `tenant_id` | Your Microsoft tenant ID. |
| `blueprint_id` | Your existing agent identity blueprint's application ID. |
| `client_id` | Your preconfigured public client's application ID. |
| `operator_id` | The approved operator's user object ID. |
| `operator_upn` | That same operator's sign-in UPN. |

No client secret, certificate, Dify app key or access token belongs in this config.

```sh
python -B lab.py check --config lab.local.json --inventory .local/inventory.json
```

### Optional: reuse identities or registrations already in your lab

Start without a binding file for a clean lab. If a matching identity name already exists, preflight stops rather than adopting it by name.

Use [the synthetic binding format](examples/bindings.synthetic.json) to create `.local/bindings.json` with your explicit, verified IDs and exact identity display names. Keep only entries that really exist. Set `registration_id` to `null` when reusing an identity that has no known registration. A full existing registration ID must be copied from its authoritative read/link, not reconstructed by adding or removing a portal prefix.

The binding source includes your Dify origin, workspace, tenant and blueprint. The Dify app UUID is the `items` key. The Entra identity ID and Graph registration ID are **different fields**. Native registration uses `sourceAgentId = Entra identity ID`, not the Dify UUID.

## 5. Sign in for read-only preflight

This command performs delegated sign-in and Graph **reads**, then writes a private local review plan. It creates no identity or registration.

```sh
python -B lab.py preflight --config lab.local.json --inventory .local/inventory.json --approve-read
```

Use the device sign-in URL/code displayed in your terminal. Sign in only as the configured operator, and stop on consent or wrong account/application. No token cache is persisted. A later command requires a new device sign-in.

Expected result: `review_required`, `graph_writes: 0`, a plan path and `scope_sha256`. Inventory must be no older than 24 hours. The plan expires after one hour.

Open the complete plan and check:

- Tenant, client, operator, blueprint, Dify instance and workspace.
- Every source app and normalized registration name.
- Existing identity/registration bindings being reused.
- Maximum new identities and registration POSTs. A fresh five-app workspace can propose **5 + 5**; the original demo's **1 + 2** is not a fixed expectation.
- The configured operator's sponsor and registration-owner assignment.

## 6. Apply only the plan you approved

**This step changes your Microsoft tenant.** Get the approval required by your organization. Substitute the exact plan path and 64-character hash printed by preflight:

```sh
python -B lab.py apply --plan ".local/preflight-RUN-ID/plan.json" --approve-sha256 "APPROVED-PLAN-HASH" --ack-stop-on-consent
```

The placeholders above are deliberate: do not use a sample hash. The command signs in again, rechecks existing bindings and name absence, then performs each required POST at most once. It verifies every resulting identity and registration.

Expected result: `registration_readbacks_verified`, per-operation counts, bindings and `runtime_enabled: false`. The current binding cache is saved locally. **No Dify app is executed, renamed or changed. No application permissions, licenses, deployment or runtime access are granted.**

A plan is single-use. Do not replay it. On an error or interruption, keep all local records and follow [failure recovery](docs/TROUBLESHOOTING.md).

## 7. Verify and repeat without new writes

```sh
python -B lab.py verify --config lab.local.json --inventory .local/inventory.json --approve-read
```

Expected: `bindings_verified`, an identity and registration read for each app, and `graph_writes: 0`. It uses the saved source checkpoint and binding cache, so `fresh_source_scan: false` is intentional.

In Microsoft 365 admin center, open the Agent 365 registry and inspect the corresponding records. Display visibility and availability can lag; the API readbacks are the binding evidence. Portal availability does not prove invocation, telemetry or that a Block control has been exercised.

To demonstrate a **fresh** discovery later, rerun the Studio collector and repeat preflight using the current local binding cache. Existing verified bindings should yield zero proposed creates/registration POSTs. Source removal, duplicates, ambiguous matches or changed context stop the lab; it does not automatically rename or delete anything.

## Safety, compatibility and source layout

- [MIT license](LICENSE): copyright (c) 2026 zenc-cp; upstream dependency notices are retained separately.
- [Security boundaries](SECURITY.md): approval, token handling and limitations.
- [Troubleshooting and interrupted runs](docs/TROUBLESHOOTING.md).
- [Dependencies, provenance and licensing status](THIRD_PARTY_NOTICES.md).
- [Python entrypoint](lab.py), [command host](dify_a365/runner.py), [registration core](dify_a365/platform_registration.py), [native helper](native/Program.cs), [browser collector](tools/discover-dify.js).

Keep personal configs, inventories, approvals and receipts private. [.gitignore](.gitignore) excludes the standard local paths, but is not a secret scanner. Review what you publish; do not upload your whole working directory.

No cleanup/delete command, scheduling, AGT integration, inbound JWT validation or runtime hosting is included. This repo addresses discovery and registration only.
