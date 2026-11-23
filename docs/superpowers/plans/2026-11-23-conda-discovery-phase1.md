# Conda Discovery Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Initialize a Git-backed Windows 11 WPF application that finds existing Conda installations, verifies a working launcher, and parses Conda JSON for later environment management.

**Architecture:** `CondaManager.Core` holds process execution, JSON models and parsing, launcher validation, candidate discovery, and selection state. `CondaManager.App` is a thin WPF shell. `CondaManager.Checks` is a zero-NuGet console test executable because the local SDK has no cached test framework packages.

**Tech Stack:** C# 13, .NET 10, WPF/XAML, `System.Diagnostics.Process`, `System.Text.Json`, `System.ComponentModel.INotifyPropertyChanged`.

## Global Constraints

- Target Windows 11 x64; WPF target framework is `net10.0-windows`.
- Manage existing Conda/Miniconda/Miniforge installations only.
- First phase performs discovery, verification, and JSON parsing; no environment creation or deletion.
- Conda process arguments use `ProcessStartInfo.ArgumentList`; never interpolate user input into a shell command.
- Validate with `info --json` before displaying an installation as usable.
- Per-launcher validation timeout is 30 seconds; user cancellation ends the discovery run.
- Unknown JSON fields and empty arrays are accepted; missing required fields, nulls, wrong types, relative paths, and malformed JSON are errors.
- Keep GUI responsive during scanning; show scan progress, no-results, and per-candidate failure states.
- Self-contained publication targets `win-x64` after build and tests pass.

---

## File map

- `.gitignore`: excludes build output, IDE files, and workspace-local .NET caches.
- `scripts/verify.ps1`: runs offline restore, build, and checks with one MSBuild node to avoid this sandbox's parallel worker failure.
- `CondaManager.slnx`: solution containing the three projects.
- `src/CondaManager.Core/CondaManager.Core.csproj`: dependency-free business logic.
- `src/CondaManager.Core/CondaJson.cs`: strict parsers for `info`, `env list`, and `list` JSON.
- `src/CondaManager.Core/CondaModels.cs`: typed records and validation errors.
- `src/CondaManager.Core/ProcessRunner.cs`: async process execution and cancellation.
- `src/CondaManager.Core/CondaDiscovery.cs`: candidate roots, launcher verification, installation deduplication.
- `src/CondaManager.Core/SelectionSettings.cs`: chosen root persistence with injectable settings path.
- `src/CondaManager.Core/MainViewModel.cs`: WPF-independent selection and discovery state.
- `src/CondaManager.App/*`: WPF app entry point, window, and folder-dialog glue.
- `tests/CondaManager.Checks/*`: dependency-free executable checks, one file per module.
- `README.md`: build, run, and read-only verification instructions.

### Task 1: Repository and JSON contracts

**Files:** Create `.gitignore`, `CondaManager.slnx`, the three `.csproj` files, a minimal `App.xaml`/`MainWindow.xaml` scaffold, `CondaModels.cs`, `CondaJson.cs`, `tests/CondaManager.Checks/Program.cs`, and `tests/CondaManager.Checks/JsonChecks.cs`.

**Interfaces:**
- `CondaInfo(string RootPrefix, string CondaVersion, IReadOnlyList<string> EnvsDirs)`.
- `CondaPackage(string Name, string Version, string? Build, string? Channel)`.
- `CondaJson.ParseInfo(string json)`, `ParseEnvironmentPrefixes(string json)`, `ParsePackages(string json)`.
- `CondaDataException` for invalid or malformed Conda JSON.

- [x] **Step 1: Add projects and a test runner.** Use SDK-style projects without package references. `Core` and `Checks` target `net10.0`; `App` targets `net10.0-windows` with `<UseWPF>true</UseWPF>` and `<OutputType>WinExe</OutputType>`. `Checks` references `Core`; `App` references `Core`. Include a bare `App.xaml` and `MainWindow.xaml` so the solution builds before Task 4. Add a console runner whose assertion failures set nonzero exit status.
- [x] **Step 2: Write failing JSON checks.** Cover a minimal valid `info` object (`root_prefix`, `conda_version`, `envs_dirs`), empty `envs` and package arrays, package `name`/`version`, unknown fields, and errors for missing fields, null/wrong types, relative paths, and invalid JSON. The first `dotnet run --project tests/CondaManager.Checks` must fail because `CondaJson` is absent.
- [x] **Step 3: Implement only the parsing contract.** Use `JsonDocument`; check property `ValueKind` before reading. Windows paths must be rooted and normalized with `Path.GetFullPath` plus `Path.TrimEndingDirectorySeparator`. Never infer an environment's Python version from `info.python_version`.
- [x] **Step 4: Run checks and build.** Run `pwsh -File scripts/verify.ps1`. It executes offline restore, solution build, and the console checks with one MSBuild node because this sandbox fails during parallel solution builds. Expected: zero check failures and build errors.
- [x] **Step 5: Commit** the tested slice with `feat: add Conda JSON contracts`.

### Task 2: Process runner and launcher verification

**Files:** Create `ProcessRunner.cs`, `CondaDiscovery.cs`, `tests/CondaManager.Checks/ProcessChecks.cs`, and `tests/CondaManager.Checks/LauncherChecks.cs`.

**Interfaces:**
- `CondaLauncher(string ExecutablePath, IReadOnlyList<string> LeadingArguments)`.
- `ProcessResult(int ExitCode, string Stdout, string Stderr)`.
- `IProcessRunner.RunAsync(CondaLauncher launcher, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)`.
- `CondaLauncherValidator.ValidateAsync(string installationRoot, CancellationToken cancellationToken)` returns a verified installation or a diagnostic failure.

- [x] **Step 1: Write failing process and validation checks.** The checks launch the test executable itself in `--echo-args` mode to prove paths with spaces and Chinese stay one argument. A fake `IProcessRunner` drives a failed `Scripts/conda.exe` followed by a successful `<root>/python.exe -m conda`, a root mismatch, malformed JSON, timeout, and user cancellation. Verify that only the matching root is accepted.
- [x] **Step 2: Verify failure.** `dotnet run --project tests/CondaManager.Checks --no-restore` must fail at the missing runner and validator contract.
- [x] **Step 3: Implement runner and validator.** Set `UseShellExecute=false`, redirect stdout/stderr, set `CreateNoWindow=true`, and fill `ArgumentList` from fixed leading arguments plus command arguments. Read both streams concurrently, wait asynchronously, kill the process tree on timeout or cancellation, and preserve stderr/exit status. Per root, try `Scripts/conda.exe` then its own `python.exe -m conda`; do not invoke `.bat` or `.cmd` with user input.
- [x] **Step 4: Run checks and build** with `pwsh -File scripts/verify.ps1`. Expected: zero failures/errors.
- [x] **Step 5: Commit** the tested slice with `feat: validate Conda launchers`.

### Task 3: Candidate discovery and selection

**Files:** Extend `CondaDiscovery.cs`; create `SelectionSettings.cs`, `MainViewModel.cs`, `tests/CondaManager.Checks/DiscoveryChecks.cs`, and `tests/CondaManager.Checks/SelectionChecks.cs`.

**Interfaces:**
- `CondaCandidateFinder.FindRoots(savedRoot, condaExe, pathValue, userProfile, programData)` returns ordered normalized root paths.
- `CondaDiscoveryService.DiscoverAsync(roots, progress, cancellationToken)` returns verified installations plus per-root failures.
- `SelectionSettings.Load()` and `Save(string root)` use an injected settings-file path.
- `MainViewModel.ScanAsync()` and `AddManualRootAsync(string root)` update installation collection, selected installation, status, and diagnostics.

- [x] **Step 1: Write failing discovery checks.** Assert stable order (saved root, `CONDA_EXE`, PATH, conventional roots), case-insensitive/trailing-slash deduplication, same-root launcher deduplication, distinct-root preservation, invalid saved selection fallback, valid saved selection preference, manual root validation, and cancellation.
- [x] **Step 2: Verify failure.** Run console checks and confirm missing discovery/selection behavior is the cause.
- [x] **Step 3: Implement discovery and settings.** A candidate is only verified after `info --json` returns exit code 0 with required typed fields and a matching normalized root. Persist only the selected installation root to `%LOCALAPPDATA%/CondaManager/settings.json`. If the settings file is missing or invalid, continue scanning; if writing it fails, keep the in-memory selection and report the settings error without crashing.
- [x] **Step 4: Run checks and build** with `pwsh -File scripts/verify.ps1`. Expected: zero failures/errors.
- [x] **Step 5: Commit** the tested slice with `feat: discover Conda installations`.

### Task 4: Minimal WPF application and delivery

**Files:** Create `src/CondaManager.App/App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`, and `README.md`; update project resources if needed.

**Interfaces:** The window binds to `MainViewModel`; code-behind only opens `Microsoft.Win32.OpenFolderDialog`, passes the chosen root to `AddManualRootAsync`, and starts scan on load.

- [x] **Step 1: Create the WPF shell.** Show scan state, installation selector, root path, Conda version, `envs_dirs`, refresh action, manual folder chooser, no-results message, and per-candidate diagnostics in Chinese. Start discovery asynchronously on window load. Disable concurrent scan actions while one runs.
- [x] **Step 2: Build and run checks.** Restore and build the solution; run the console checks. Expected: zero errors and check failures.
- [x] **Step 3: Run a read-only launcher smoke test.** On this machine, validate `D:/Software/ANACONDA` using the verified fallback launcher. Do not create, remove, or list packages in the installed base environment. Report any environmental restriction accurately.
- [x] **Step 4: Document usage and publish.** `README.md` explains local build/run, launcher detection, first-phase scope, and tests. Publish with `dotnet publish src/CondaManager.App/CondaManager.App.csproj -c Release -r win-x64 --self-contained true --source . -p:NuGetAudit=false -m:1 -p:UseSharedCompilation=false`; inspect the produced executable/folder.
- [x] **Step 5: Final verification and commit.** Run the complete check executable, solution build, and `git status --short`; commit with `feat: add Conda discovery desktop shell` after verification.

## Self-review checklist

- [x] All first-phase requirements map to Tasks 1–4.
- [x] No environment creation/deletion occurs in code or smoke tests.
- [x] Model and method names agree across tasks.
- [x] The final build, checks, and publish results are recorded from fresh commands.

## Verification record (2026-11-23)

- `pwsh -NoProfile -File scripts/verify.ps1`: solution build passed with zero warnings and errors; 35/35 checks passed.
- `dotnet publish src/CondaManager.App/CondaManager.App.csproj -c Release -r win-x64 --self-contained true --source . -p:NuGetAudit=false -m:1 -p:UseSharedCompilation=false`: exited successfully and produced the Windows x64 publish folder.
- `dotnet run --project tests/CondaManager.Checks/CondaManager.Checks.csproj --no-restore -p:UseSharedCompilation=false -- --probe-root D:\Software\ANACONDA`: verified Conda 25.5.1 through that installation's `python.exe -m conda` fallback.
- The published WPF executable started with a main window titled “Conda 环境管理器” and exited after a normal close request.
- Final independent review of `main..d2a167d` found no confirmed actionable issues. The reviewer did not run a real Conda command.
