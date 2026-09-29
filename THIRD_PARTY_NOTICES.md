# Dependencies, provenance and licensing status

## Wrapper source

The Python registration core and native helper are adapted from the original lab prototype. This candidate replaces fixed tenant/account/path settings with explicit operator configuration, omits the old VM/SQL discovery and invocation-server paths, and adds a standalone command host, browser collector, synthetic tests and a clone-to-lab walkthrough.

The wrapper is licensed under the [MIT license](LICENSE), with the owner-confirmed notice `Copyright (c) 2026 zenc-cp`. The Microsoft dependency retains its separate upstream copyright and license notice below.

## Microsoft Agent 365 CLI

- Dependency: `Microsoft.Agents.A365.DevTools.Cli`, version **1.1.221**.
- Obtained by each operator from the public NuGet package feed using the documented local tool install.
- The sample calls `GraphApiService.RegisterAgentInstanceAsyncV2` and implements its authentication interfaces. These version-pinned implementation APIs may change; this is not a claim of a supported general-purpose registry SDK.
- The upstream Microsoft MIT notice is preserved in [A365 CLI license](third_party/A365-CLI-LICENSE.txt).
- No CLI source tree, package cache, DLL, PDB or compiled helper is part of the source release. Dependencies and their notices remain subject to their own licenses.

## Dify

- Compatibility reference: [Dify 0.15.4 console app listing](https://github.com/langgenius/dify/blob/0.15.4/api/controllers/console/app/app.py) and [Studio request authentication](https://github.com/langgenius/dify/blob/0.15.4/web/service/base.ts).
- The collector is a small independently written metadata projection client. The repo does not redistribute Dify, its workflow definitions or its server/UI source.
- Operators are responsible for the license and security posture of their own Dify installation. Historical version pinning here is for reproduction, not an endorsement for public deployment.

## Runtime and test tools

Python uses only its standard library. JavaScript tests use Node's built-in test runner. The native project requires .NET 8 SDK and assemblies from the pinned public CLI package. Those runtimes/tools are not redistributed.

## Reference dates and validation limits

Compatibility decisions and official Microsoft documentation were reviewed on 2026-09-29. The original live demo predates this portable export. Local build and synthetic verification do not establish fresh-tenant permissions, enrollment, licensing, sign-in, portal visibility or end-to-end live success.
