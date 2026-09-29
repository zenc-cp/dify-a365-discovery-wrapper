# Troubleshooting without duplicate writes

## Before any tenant writes

| Symptom | Meaning and next step |
|---|---|
| `pinned_cli_missing` / native build error | Install the exact public CLI 1.1.221 in the documented local tool directory and use .NET 8 SDK. No tenant sign-in is needed for building. Do not point to an arbitrary DLL download. |
| Dify HTTP 401, HTML response, or missing Studio token | Confirm the Dify version, signed-in Studio session, selected workspace and same-origin console API. Do not export the token to a terminal. |
| Workspace/origin mismatch | The checkpoint belongs to a different source. Recollect from the intended workspace and correct the local config. |
| More than five apps, duplicate names or incomplete paging | This bounded lab refuses the inventory. Use a separate 1-5-app lab workspace. Do not cut JSON rows or forge `complete`. |
| `inventory_stale` | Collect again in Studio. Preflight requires a source checkpoint no older than 24 hours. |
| `synthetic_example_is_offline_only` | Replace the example domain, UPN and IDs with your own lab configuration. Synthetic tests are not live inputs. |
| Existing identity name found | No implicit adoption. Independently inspect it and supply its identity ID, exact name and optional authoritative registration ID in a local binding file. Preflight verifies them again. |
| Device-code sign-in blocked, consent requested, wrong account/client, or missing scope | Stop. Ask the appropriate lab administrator to review prerequisites outside this sample. Do not switch identities or grant broader permissions to get past the error. |
| Graph 403/404, unavailable Agent ID/registry feature | Confirm tenant availability, eligibility, roles, blueprint and preconsented permissions. A public repo cannot supply these prerequisites. |
| `plan_expired_or_future`, `native_build_changed`, or `binding_cache_changed` | Produce a new read-only preflight and review the new plan. Old approval does not cover changed inputs. |
| `plan_already_consumed` | Do not replay. Use the existing local binding cache and the read-only verification command; use fresh preflight for future changes. |

An incomplete name lookup is not evidence that no identity exists. HTTP 429, 5xx, redirects and pagination uncertainty stop this sample. Even for an apparently transient error, it does not automatically retry a cloud write.

## If apply started and did not finish

A pending marker is acquired **before authentication**. It can remain after an authentication failure even when no POST happened. It can also indicate an identity/registration was created but its readback, output delivery or local save failed. Treat the result as unknown until you inspect evidence.

1. **Do not rerun apply, delete the local folder or clear the marker just to unblock the command.** Preserve the full local run directory and the plan.
2. Inspect the pending marker's run directory. The native journal records an intent before every POST, its payload hash, any received HTTP status and candidate ID. A status alone is not a verified binding.
3. Have an authorized operator perform read-only checks in the intended tenant. Use the exact source, identity and registration IDs from the plan/journal where known. If a POST may have succeeded without a usable response, identify the corresponding record independently. Do not guess a portal prefix or create a replacement blindly.
4. Build an explicit local binding seed from independently verified results, using the documented example shape. Record which actions did and did not happen. The read-only `verify` command may be used while the pending marker exists, but requires a complete binding set; partial recovery needs separate manual reads.
5. Only after the uncertainty has been resolved and an operator authorizes recovery should they **archive**, rather than erase, the pending marker/run. Keep the consumed-plan marker. This sample intentionally provides no automated reset or deletion command.
6. Recollect inventory if necessary, run fresh read-only preflight using the recovered bindings, and obtain approval for that new exact plan before any remaining writes.

If the binding save succeeded but the completion-marker rename failed, the tenant operations may already be fully complete. Verify those bindings first. Never interpret a terminal error as proof that a POST did not happen.

## What the portal can and cannot prove

Use exact API readbacks for binding correctness. Portal search filters can hide records and UI IDs can be truncated. A visible/Available record does not prove invocation, telemetry, runtime permissions or revocation enforcement. This sample never exercises the portal Block control.

## Cleanup

No cleanup command is supplied because deleting identities or registrations changes shared tenant state. Plan any cleanup separately with exact IDs, dependencies and explicit authorization. Do not repurpose setup commands as an unreviewed reset.
