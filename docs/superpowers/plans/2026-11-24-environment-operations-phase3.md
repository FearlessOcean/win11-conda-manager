# Conda Environment Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Windows user create an environment at a previewed, editable path and delete a selected environment after full-path confirmation.

**Architecture:** Pure core planning and persistence services validate destinations and remember custom prefixes. A Conda operator invokes the verified launcher with separate arguments and checks the resulting filesystem state. The WPF shell owns the create dialog, deletion confirmation, busy/cancel state, and refresh/selection flow.

**Tech Stack:** C# 13, .NET 10, WPF/XAML, `System.Diagnostics.Process`, `System.Text.Json`, and the existing dependency-free check runner.

## Global constraints

- Run only against a verified `CondaLauncher`; never use a shell or concatenate user text into a command line.
- Creation uses `conda create --prefix <absolute destination> python[=<version>] --yes --json` and rejects an existing destination immediately before launch.
- Deletion uses `conda env remove --prefix <absolute prefix> --yes --json` and protects base, known installation roots and their ancestors, drive/share roots, active prefix, and non-Conda directories.
- The default parent is the first writable `envs_dirs` candidate; if none exists, require a custom parent.
- The app's Conda child processes set `CONDA_REPORT_ERRORS=false` without changing the user's Conda configuration.
- Both success and failure refresh the environment list. No real user environment is created or deleted by automated checks.

## File map

- `src/CondaManager.Core/EnvironmentCreationPlanner.cs`: name/version/path validation and default writable parent selection.
- `src/CondaManager.Core/ManagedPrefixStore.cs`: normalized custom-prefix persistence under `%LOCALAPPDATA%/CondaManager`.
- `src/CondaManager.Core/CondaEnvironmentOperator.cs`: checked create/delete commands and postconditions.
- `src/CondaManager.Core/ProcessRunner.cs`: per-launcher environment overrides.
- `src/CondaManager.Core/CondaEnvironmentCatalog.cs`: merge still-valid stored prefixes.
- `src/CondaManager.Core/EnvironmentBrowserViewModel.cs`: refresh and select a requested full prefix.
- `src/CondaManager.App/CreateEnvironmentDialog.xaml(.cs)`: input, default location, destination preview, and validation.
- `src/CondaManager.App/MainWindow.xaml(.cs)`: actions, confirmation, status/cancellation, and browser refresh.
- `tests/CondaManager.Checks/CreationChecks.cs`, `ManagedPrefixChecks.cs`, `OperationChecks.cs`, and existing module checks: behavior verification.
- `README.md`: stage 3 usage and safety notes.

### Task 1: Creation plan and custom-prefix store

**Interfaces:** `EnvironmentCreationPlanner.ChooseDefaultParent(CondaInfo, IWritableDirectoryProbe)` returns `string?`; `EnvironmentCreationPlanner.Build(string name,string parent,string? pythonVersion)` returns `EnvironmentCreationPlan(Name,ParentDirectory,Prefix,PythonVersion)`. `IManagedPrefixStore.Load/Add/Remove` works with normalized absolute prefixes.

- [ ] Write failing checks for the first writable `envs_dirs` choice, no writable choice, Chinese names and Unicode paths, rejected spaces/separators/reserved names, `major.minor[.patch]` version validation, and exact `Path.Combine(parent,name)` preview.
- [ ] Write failing checks for store round trip, case-insensitive deduplication, and malformed file tolerance.
- [ ] Implement the planner, a production probe that tests an existing ancestor without creating the target, and atomic JSON persistence. The store must keep in-memory additions available if writing settings fails, while reporting that persistence failure.
- [ ] Run the relevant checks and full solution build.

### Task 2: Checked Conda mutation commands

**Interfaces:** `ICondaEnvironmentOperator.CreateAsync(VerifiedInstallation,EnvironmentCreationPlan,CancellationToken)` and `DeleteAsync(VerifiedInstallation,string prefix,IReadOnlyCollection<string> protectedRoots,string? activePrefix,CancellationToken)`. `CondaLauncher.EnvironmentOverrides` carries `CONDA_REPORT_ERRORS=false` to `ProcessRunner`.

- [ ] Write failing process checks that prove launcher environment overrides reach a child process without modifying the parent environment.
- [ ] Write failing operation checks for exact argument boundaries, existing creation target refusal, successful creation marker, nonzero/error JSON, cancellation, base/root/ancestor/active protection, missing `conda-meta/history`, successful deletion disappearance, and uncertain postcondition.
- [ ] Implement `CondaEnvironmentOperator` with bounded create/delete timeouts, exit-code and concise JSON-error handling, and filesystem postcondition checks. Do not expose raw traceback or command JSON.
- [ ] Run the relevant checks and full solution build.

### Task 3: Catalog refresh and persistence integration

**Interfaces:** `CondaEnvironmentCatalog(IProcessRunner,IManagedPrefixStore?)` merges stored prefixes that still have `conda-meta/history`; `EnvironmentBrowserViewModel.RefreshAsync(string preferredPrefix,CancellationToken)` requests a specific selection after refresh.

- [ ] Write failing catalog checks for merging a custom prefix absent from Conda JSON, deduplicating a registered prefix, and skipping a stale path. Write a browser check that refresh selects a newly created prefix without querying base packages first.
- [ ] Implement the catalog merge and requested-prefix refresh, preserving existing cancellation and generation behavior.
- [ ] Run relevant checks and full solution build.

### Task 4: WPF create/delete workflow and delivery

- [ ] Add the create dialog with name, optional Python version, editable/browsable parent, validation text, and absolute destination preview; reject an existing target before returning the plan.
- [ ] Add Create/Delete actions in the main window. Delete confirmation states that all files under the full prefix are removed. Disable switching and repeat actions during operations; show an explicit progress message and cancellation button. Block close until an operation finishes or cancellation cleanup completes.
- [ ] On successful create, store the prefix, refresh and select it. On successful delete, remove it from the store and refresh. On failure/cancellation, refresh and show the exact target and outcome. Keep store errors distinct from Conda results.
- [ ] Update README, run all checks/build, render a WPF smoke window without invoking real Conda operations, publish self-contained `win-x64`, review the diff, and commit.

## Self-review

- [x] Every phase 3 requirement in the design maps to a task.
- [x] All paths and API names referenced in later tasks are defined above.
- [x] Automated checks never mutate an existing user environment.
