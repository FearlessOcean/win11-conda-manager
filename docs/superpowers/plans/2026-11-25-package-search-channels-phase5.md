# Package Search and App Channels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Search packages available from Conda channels and manage per-installation channel overrides without modifying `.condarc`.

**Architecture:** A channel policy and atomic app store produce an immutable inherited/custom snapshot. A search adapter uses the verified Conda launcher and parses only safe JSON fields, while a search view model rejects stale results. WPF dialogs edit channels, search available packages, and prefill the existing install flow; the package operator uses one channel snapshot for preview and apply.

**Tech Stack:** Windows 11, C# 13, .NET 10, WPF/XAML, `IProcessRunner`, `System.Text.Json`, and the existing dependency-free checks.

## Global constraints

- App-scoped profiles only; no mutating `conda config` command or `.condarc` write.
- Inherited mode adds no channel flags. Custom mode has at least one validated ordered entry and appends repeated `-c <channel>` plus `--override-channels` to search and package install/update/remove.
- A preview and its apply use the same immutable snapshot. Environment creation keeps Conda's existing channel behavior.
- Channel names match `[A-Za-z0-9][A-Za-z0-9._-]{0,127}`. URL entries must be HTTPS with no credentials, query, fragment, whitespace, or `/t/<token>/` path. Reject HTTP, local and file channels.
- Search submits a validated 3–64 character query as one `*<query>*` MatchSpec with `--skip-flexible-search --json`. Search is explicit, cancellable, and cannot show a stale result.
- Search stdout longer than 8 MiB, malformed/unknown JSON, or nonzero errors other than known no-match exceptions do not yield results. Display only safe package fields and channel labels; cap display at 300 rows.
- No raw Conda JSON, URL, token or traceback is displayed. Use only the verified launcher and separate arguments. Automated checks never mutate real Conda environments or configuration.

## File map and interfaces

- `src/CondaManager.Core/ChannelPolicy.cs`: `ChannelSnapshot.Inherit`, `ChannelSnapshot.Custom(IEnumerable<string>)`, `IsOverride`, `Channels`, `AppendArguments(List<string>)`, and `ChannelPolicy.Normalize(string)`.
- `src/CondaManager.Core/ChannelProfileStore.cs`: `IChannelProfileStore.Load(root)` returning `ChannelProfileLoadResult(ChannelSnapshot,string? Warning)` and `Save(root,snapshot)`; atomic `%LOCALAPPDATA%/CondaManager/channel-profiles.json` map.
- `src/CondaManager.Core/CondaPackageSearch.cs`: `AvailablePackage`, `PackageSearchPage`, `ICondaPackageSearch.SearchAsync` and `GetConfiguredChannelsAsync`; safe Conda search/config JSON parsing.
- `src/CondaManager.Core/AvailablePackageSearchViewModel.cs`: explicit search state, cancellation, selected result and generation checks.
- `src/CondaManager.Core/CondaPackageOperator.cs`: add `PackageOperationRequest.Channels` init property defaulting to `ChannelSnapshot.Inherit`, and append flags to preview and apply.
- `src/CondaManager.App/ChannelManagerDialog.xaml(.cs)`: inherited/custom mode, add/remove/reorder/save, configured-channel read-only display.
- `src/CondaManager.App/PackageSearchDialog.xaml(.cs)`: query, results, loading/error/empty/cancel, selected result.
- `src/CondaManager.App/MainWindow.xaml(.cs)`, `MainWindow.PackageOperations.cs`, `InstallPackageDialog.xaml(.cs)`: launch dialogs, show channel mode, prefill install and revalidate target.
- `tests/CondaManager.Checks/ChannelChecks.cs`, `PackageSearchChecks.cs`, `SearchViewModelChecks.cs`, and `PackageOperationChecks.cs`: fake-runner and disposable-settings verification.
- `README.md`: usage, channel scope and validation, manual acceptance.

### Task 1: Channel policy and profiles

- [ ] Add failing checks for accepted channel names/HTTPS URLs, rejected credentials/HTTP/file/whitespace/token paths, normalization/deduplication, inherited/custom argument order and immutable snapshots.
- [ ] Implement `ChannelPolicy` and `ChannelSnapshot` with exact normalized entries and no channel flags in inherited mode; run focused checks green.
- [ ] Add failing checks for per-root persistence, malformed settings fallback, and atomic write failures without false persistence; implement `ChannelProfileStore` and run focused checks green.
- [ ] Run `scripts/verify.ps1`, review the task diff, and commit the passing slice.

### Task 2: Conda search adapter and view model

**Consumes:** `ChannelSnapshot` from Task 1. **Produces:** `ICondaPackageSearch.SearchAsync(VerifiedInstallation,string,ChannelSnapshot,CancellationToken)` returning `PackageSearchPage(IReadOnlyList<AvailablePackage> Items,bool Truncated)`, and `GetConfiguredChannelsAsync(VerifiedInstallation,CancellationToken)` returning safe labels.

- [ ] Add failing fake-runner checks for wildcard query and exact argument order, inherited/custom channels, name-to-array JSON, 300-row cap, 8 MiB refusal, no-match exception names, malformed results, URLs/tokens in records/errors, and cancellation.
- [ ] Implement `CondaPackageSearch` with bounded timeouts, child-only `CONDA_REPORT_ERRORS=false`, and safe field/label extraction. Map only recognized no-match errors to empty results.
- [ ] Add failing view-model checks for explicit search, loading/error/empty, cancellation, changed query/channel/installation, and late result rejection; implement `AvailablePackageSearchViewModel`.
- [ ] Run focused and full checks, review the diff, and commit.

### Task 3: Channel-aware package transactions

- [ ] Add failing checks that inherited requests keep phase-4 arguments, custom requests use ordered `-c` flags plus `--override-channels` in both preview/apply, and a mutated caller list cannot change the snapshot.
- [ ] Add the `Channels` request property and append validated snapshot flags for every package solver operation. Preserve all existing preview provenance, target checks, and result parsing.
- [ ] Run focused and full checks, review the diff, and commit.

### Task 4: WPF search and channel management

- [ ] Add the channel editor with inherited/custom modes, validation, ordered editing, and save/cancel. Add a separate search dialog with explicit query, cancel, safe results and no-result/error states.
- [ ] Add `搜索可用包` and `管理渠道` actions and mode label in the main window. Load per-installation profiles, invalidate search on channel change, and keep installed-package filtering separate.
- [ ] Prefill the install form from a selected search result, explain that build/channel selection is finalized in dry-run, and put the current channel snapshot on the package operation request. Base remains read-only.
- [ ] Extend the ignored WPF smoke with fake search/channel services to cover both dialogs, ordering, selection, prefill, and safe cancellation without touching Conda. Run full build/checks and commit.

### Task 5: Delivery

- [ ] Update README, run `scripts/verify.ps1`, WPF smoke, self-contained `win-x64` publish, independent whole-branch review, `git diff --check`, and commit final documentation/fixes.

## Self-review

- [x] The plan covers search, profile persistence, package command consistency and UI integration.
- [x] All new process calls use fake-runner checks and no real user Conda mutation.
- [x] The two search experiences remain separately named and operated.
