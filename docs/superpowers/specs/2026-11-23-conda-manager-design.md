# Windows 11 Conda Manager Design

## Goal

Build a Windows 11 desktop application that manages existing Conda, Miniconda,
and Miniforge installations. The first development phase establishes a working
WPF application, discovers installations, verifies a usable Conda launcher,
and parses the JSON data needed by later environment operations.

## Product scope

- The user selects one verified Conda installation when several are available.
- The main view will list known environments and show each environment's full
  path, Python version, and installed package names and versions.
- The create form will show an absolute destination before starting. Its default
  parent is the first writable directory in the selected installation's
  `envs_dirs`; changing the parent changes only that creation. The destination
  is the selected parent joined with the entered environment name. Conda is
  invoked with `--prefix` so the displayed destination is the actual target.
- Deletion will require confirmation showing the full path. The application's
  known Conda roots are protected, and deletion uses Conda's own command with
  the full prefix.
- Package installation, updates, import/export, and installing Conda itself are
  outside the initial product scope.

## Technology and boundaries

- Windows 11 x64, C#, WPF/XAML, and .NET 10 LTS.
- The application is published self-contained for `win-x64` and runs outside
  every managed environment.
- A WPF presentation layer owns interaction state. A service layer discovers
  installations and coordinates operations. A Conda adapter owns process
  invocation and JSON parsing. Conda remains the source of environment facts;
  local settings under `%LOCALAPPDATA%/CondaManager` retain the chosen
  installation root and any custom prefixes the application creates when
  Conda does not register them.
- Every process uses an absolute executable path and a separate argument list.
  No user-supplied text is interpolated into a shell command. Candidate
  launchers are verified with `info --json`; an installation's own
  `python.exe -m conda` is supported when `conda.exe` cannot start.
- Each environment is identified by its normalized absolute prefix, not by its
  display name. The selected Conda installation determines the CLI context;
  it does not claim exclusive ownership of every listed environment.

## Data flow

1. Discover candidate installation roots in a stable order: saved root,
   `CONDA_EXE`, PATH entries, then a bounded set of conventional locations.
   Manual selection accepts an installation root directory. Normalize full
   paths, trim trailing separators, and compare without case to deduplicate.
   For each root, try `Scripts/conda.exe`, then that root's
   `python.exe -m conda`. Run `info --json` with a 30-second timeout per
   launcher. A start failure, timeout, nonzero exit, malformed JSON, or
   mismatched reported root permits the next launcher from that same root;
   user cancellation stops the scan. Deduplicate verified installations by
   normalized `root_prefix`, retaining the first working launcher.
2. Read `conda info --json` for installation metadata and `envs_dirs`; read
   `conda env list --json` separately for known environment prefixes. This
   avoids depending on `envs_dirs` appearing in the environment-list response.
3. On selecting an environment, read `conda list --prefix <path> --json`.
   Determine its Python version from the package named `python`; show
   “未安装 Python” when that package is absent.
4. Later create and delete actions run asynchronously. A successful process
   result triggers a fresh Conda query; a failed result displays diagnostic
   information and also refreshes state before declaring the outcome.

## First-phase deliverable

- Git repository and a buildable WPF solution with a focused core library and
  automated tests.
- A discovery flow that finds candidates, validates actual launchers, deduplicates
  installations by normalized root prefix, and supports manually choosing an
  installation root directory. The UI shows scan progress, a distinct
  no-results state, and per-candidate failures. The user can switch among
  verified installations. A saved root is selected when still valid; an
  invalid saved root is ignored and the first verified installation is selected.
- Typed parsing for Conda installation information, environment paths, and
  package records. `info` requires absolute `root_prefix`, nonempty
  `conda_version`, and an `envs_dirs` array of absolute paths; `env list`
  requires an `envs` array of absolute paths; each `list` package requires
  nonempty `name` and `version`. Empty arrays and unknown fields are accepted.
  Missing fields, wrong types, null or relative paths, malformed JSON, failed
  starts, nonzero exit codes, and timeouts are surfaced as distinct errors
  without crashing the UI.
- A minimal Chinese UI showing discovery progress and verified installations,
  the chosen installation's root path/version/default environment directories,
  plus an error or manual-selection state. Environment management controls
  follow in subsequent phases.

## Safety and verification

- Discovery must never launch a candidate merely because a file exists; it
  must validate the candidate's output. A failed `conda.exe` candidate may
  fall back to that same installation's `python.exe -m conda` before the
  installation is marked unusable.
- Process results include exit code, stdout, and stderr. JSON parse failures
  report context but do not expose unrelated installation configuration.
- A `.gitignore` excludes .NET build output and local tool caches. The design
  and each completed implementation slice are committed to Git.
- Tests use fixed JSON fixtures and fake process results for discovery, parsing,
  fallback, deduplication, malformed output, timeout/cancellation, invalid
  saved selection, and paths containing spaces or Chinese characters.
  A read-only real Conda smoke test can be run manually against a usable
  installation; it is optional for CI.
- The first phase does not create or remove real Conda environments.
