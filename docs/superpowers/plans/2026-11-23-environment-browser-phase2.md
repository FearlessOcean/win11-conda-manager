# Environment Browser Phase 2 Implementation Plan

**Goal:** Show all environments known to the selected Conda installation and inspect the selected environment's path, Python version, and installed packages in the Windows desktop UI.

**Architecture:** A catalog in `CondaManager.Core` invokes the verified launcher and parses Conda JSON. A separate WPF-independent browser view model owns asynchronous environment and package states, selection, and package search. `MainWindow` binds those states beside the phase 1 installation selector.

**Tech stack:** C# 13, .NET 10, WPF/XAML, `System.Diagnostics.Process`, `System.Text.Json`, and the existing console checks.

## Task 1: Environment catalog

- [x] Add failing checks for exact launcher arguments, nonzero exits, malformed JSON, normalized prefix deduplication, base inclusion, and custom-prefix package lookup.
- [x] Add `ICondaEnvironmentCatalog` and `CondaEnvironmentCatalog`. Query environment prefixes through `env list --json`; query packages only for the selected absolute prefix. Use the phase 1 parser and process runner.
- [x] Verify the catalog checks, then the full solution.

## Task 2: Browser state and concurrency

- [x] Add failing checks for installation switching, environment switching, refresh selection, Python version extraction, search, empty/error states, and a late response that ignores cancellation.
- [x] Implement `EnvironmentBrowserViewModel` with independent list/detail states, cancellation, request generations, and stable prefix-based selection. WPF calls it on the dispatcher and its awaits resume on that context.
- [x] Preserve the in-memory selected Conda installation across an installation rescan when it is still present.
- [x] Verify browser checks and the full solution.

## Task 3: Desktop integration and delivery

- [x] Add environment selector, full prefix, Python version, package count, package search, and package table to `MainWindow`. Keep installation diagnostics accessible and show loading/empty/error messages with retry actions.
- [x] Wire installation selection, environment selection, refresh, and window cancellation to browser operations. Do not query package details for every environment.
- [x] Update the README with phase 2 scope, usage, and the known Conda package-list caveat.
- [x] Run the complete checks and build, publish a self-contained `win-x64` build, perform a local GUI smoke test, and commit the phase.

## Boundaries

- No create, delete, or package mutation actions in this phase.
- Use full normalized prefixes as environment identities. Environment names can collide.
- Keep package details scoped to one selected environment.
- Do not run live package enumeration against the user's base environment during automated verification.

## Verification record (2026-11-24)

- `pwsh -NoProfile -File scripts/verify.ps1`: 51/51 checks passed; build had zero warnings and errors.
- `dotnet publish src/CondaManager.App/CondaManager.App.csproj -c Release -r win-x64 --self-contained true --source . -p:NuGetAudit=false -m:1 -p:UseSharedCompilation=false`: passed and produced the publish folder.
- A temporary WPF smoke host constructed and rendered `MainWindow` offscreen with its startup scan handler removed. The XAML loaded, bindings rendered, and no real Conda command ran.
- Independent review identified a repeated-refresh race. A controlled delayed-response check reproduced it; the view model now retains the intended prefix and the complete checks pass.
