# Conda Package Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users install, update, and remove one Conda package in a selected non-base environment after reviewing a Conda dry-run transaction.

**Architecture:** Extend package metadata to classify Conda versus pip origin. A focused package operator validates requests, previews the solver transaction, and applies only a confirmed nonempty preview through the verified launcher. The WPF shell owns package selection, input and preview dialogs, cancellation, and post-operation refresh.

**Tech Stack:** C# 13, .NET 10, WPF/XAML, `System.Text.Json`, `IProcessRunner`, and the existing dependency-free check runner.

## Global constraints

- Only non-base environments can be mutated; pip and unknown-origin rows are read-only.
- Commands use the selected verified launcher's absolute executable and separate argument list with `--prefix <absolute path>`, `--json`, `--dry-run` for preview, and `--yes` for application.
- Never interpolate a package name or path into a shell command. Never use `--force-remove`, `--all`, or global update.
- The target must have `conda-meta/history`, avoid unsupported aliases/reparse points, and differ from every known Conda installation root by Windows directory identity. An inconclusive identity blocks mutation.
- A successful preview requires valid JSON and shows only package names, versions, and builds from `LINK`/`UNLINK`; do not show raw channel URLs, tokens, tracebacks, or command JSON.
- A no-op preview is displayed without execution. A nonempty preview requires a separate explicit user confirmation; explain that the second solver run may differ.
- `CONDA_REPORT_ERRORS=false` applies to child processes only. Cancellation waits for process cleanup. Uncertain cleanup disables further mutations until restart.
- Automated checks use fake runners and disposable repository paths; they do not mutate a real Conda environment.

## File map and interfaces

- `src/CondaManager.Core/CondaModels.cs`, `CondaJson.cs`: optional `PackageType` metadata and `CondaPackage.Origin` classification (`Conda`, `Pip`, `Unknown`).
- `src/CondaManager.Core/CondaPackageOperator.cs`: `PackageAction` (`Install`, `Update`, `Remove`), `PackageOperationRequest(VerifiedInstallation Installation, string Prefix, PackageAction Action, string PackageName, string? Version, CondaPackage? InstalledPackage, IReadOnlyCollection<string> ProtectedRoots)`, `PackageChange`, `PackagePreview`, and `ICondaPackageOperator.PreviewAsync/ApplyAsync`.
- `src/CondaManager.App/InstallPackageDialog.xaml(.cs)`: package name, optional version, exact spec preview, target path.
- `src/CondaManager.App/PackagePreviewDialog.xaml(.cs)`: full target path, grouped `LINK`/`UNLINK` changes, confirmation warning.
- `src/CondaManager.App/MainWindow.PackageOperations.cs`: package commands and UI operation flow as a partial `MainWindow` class.
- `src/CondaManager.App/MainWindow.xaml(.cs)`: package selection, action buttons, status-property notifications, refresh wiring.
- `tests/CondaManager.Checks/PackageSourceChecks.cs`, `PackageOperationChecks.cs`, and existing parser/browser checks: fake JSON and process behavior.
- `README.md`: scope, warnings, run/verification instructions.

### Task 1: Package origin and request validation

- [x] Add a parser check with Conda, `pypi`, `virtual_python_wheel`, and incomplete metadata records; run it and observe failure because origin is unavailable.
- [x] Add a request-validation check for a non-base target with a Conda marker, safe ASCII name/version, pip/unknown row refusal, base/root/alias/reparse refusal; run it and observe failure before implementing validation.
- [x] Implement `CondaPackage.Origin` and optional `PackageType` parsing; preserve the existing four-field record constructor. Implement request validation in `CondaPackageOperator` with `WindowsPathSafety` and `WindowsDirectoryIdentity`. A target with unknown identity fails closed.
- [x] Run the focused checks and `scripts/verify.ps1`; commit the passing core slice.

### Task 2: Conda preview and apply

**Consumes:** `PackageOperationRequest` from Task 1. **Produces:** `PackagePreview` with `Request`, `ToInstall`, `ToRemove`, `ToFetch`, `HasChanges`; `ICondaPackageOperator.PreviewAsync(PackageOperationRequest,CancellationToken)` and `ApplyAsync(PackageOperationRequest,PackagePreview,CancellationToken)`.

- [x] Add failing fake-runner checks for exact `install`, `update`, and `remove` argument arrays, verified launcher, `CONDA_REPORT_ERRORS=false`, no-op JSON, action groups, prefix mismatch, error JSON sanitization, cancellation, and apply refusal without matching nonempty preview.
- [x] Implement `PreviewAsync` with a bounded solver timeout, `--dry-run --json`, strict success and optional prefix checks, safe `LINK`/`UNLINK`/`FETCH` parsing, and no-op handling when Conda reports success with a message but no actions.
- [x] Implement `ApplyAsync` with target revalidation, `--yes --json`, strict success and optional prefix checks. Keep the preview and apply command request identical except for dry-run/yes flags.
- [x] Run focused and full checks; commit the passing operator slice.

### Task 3: WPF package workflow

**Consumes:** `ICondaPackageOperator`, `PackageOperationRequest`, `PackagePreview`, and `CondaPackage.Origin`.

- [x] Add `InstallPackageDialog` with name/version validation, destination path and exact spec preview. Add a grouped `PackagePreviewDialog` that clearly shows linked/unlinked packages and warns about dependency changes and preview drift.
- [x] Bind a selected package row in `MainWindow`, clear it when filtering/reloading/selection changes, and enable Install/Update/Remove only for an eligible non-base target and known Conda row. Keep pip/unknown rows visible and read-only.
- [x] Implement preview → no-op or confirmation → apply in `MainWindow.PackageOperations.cs`; reuse phase-3 busy/cancel/close behavior, revalidate target and package selection before apply, refresh details after any attempted apply, and preserve the cleanup-failure lockout.
- [x] Extend the ignored offscreen WPF smoke to load the new dialogs and controls. Run the full check suite and build; commit the UI slice.

### Task 4: Delivery

- [x] Update README, verify 0 warnings/errors and all checks passing, run a WPF smoke without real Conda mutations, publish self-contained `win-x64`, review the diff, and commit any final fixes.

Verification: `scripts/verify.ps1` passed 101/101 checks with 0 warnings and 0 errors. The offscreen WPF smoke loaded the main window and package dialogs and exercised a fake package workflow, including confirmation, no-op, selection changes, and refresh. A self-contained `win-x64` publish produced `CondaManager.App.exe`. Independent core and integrated reviews found no remaining Important or Critical issue after the red/green fixes. Automated checks did not alter a real Conda environment.

## Self-review

- [x] Every phase-4 requirement in the design maps to a task.
- [x] Source classification, preview parsing, mutation gating, UI refresh, and failure behavior have explicit verification steps.
- [x] The tests avoid all real Conda package mutations.
