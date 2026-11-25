# Package Search and App-Scoped Channels Design (Phase 5)

## Scope and decisions

Add a search of packages available from Conda channels and an ordered channel
list managed by this application. The current text box continues to filter
packages already installed in the selected environment; repository search is
a separate window. A search result can prefill the existing installation form
with package name and version. The selected Conda installation supplies the
verified launcher. The base environment remains read-only for package changes.

The channel list is scoped to a verified installation root and stored under
`%LOCALAPPDATA%\CondaManager`. It never writes `.condarc` or calls a mutating
`conda config` command. With no app override, search and package operations
inherit Conda's effective channels. With a nonempty custom list, search and
package install/update/remove use the same ordered `-c <channel>` arguments
and `--override-channels`. Environment creation keeps its existing Conda
configuration. An in-flight preview and apply use one immutable channel
snapshot, so editing the list cannot silently change the second command.

Three approaches were considered. Conda CLI search uses the verified launcher
and works with named and custom channels. A package-site API would not cover
arbitrary channels. Parsing repodata independently would duplicate Conda's
index and authentication behavior. Use `conda search --json` through the
existing process abstraction.

## Search flow

The main window adds `搜索可用包` and `管理渠道` actions near package controls.
Search opens a window with an explicit query box, Search/Cancel buttons,
loading/error/empty states, and a results table. The query is 3–64 ASCII
letters, digits, `_`, `-`, or `.`, starting with a letter or digit. The app
builds the single MatchSpec `*<query>*` and runs `conda search <spec> --json`
with the current channel snapshot and `--skip-flexible-search`. No query is
sent while typing. Search is cancellable and a late result from an older
query, installation, or channel snapshot cannot replace the current results.

Conda search JSON maps package names to arrays of package records. The parser
reads only name, version, build, subdir, and a safe display label for channel.
Package fields must be printable bounded strings. A simple channel name is
displayed directly; a URL is displayed as its host only, never its path,
userinfo, query, or fragment. Other channel values display `来源未知`.
It ignores raw URL and unrelated fields, refuses stdout longer than 8 MiB,
limits displayed rows to 300, and reports when results are truncated. Missing,
malformed or overlarge JSON is an error. `PackagesNotFoundError` and
`PackagesNotFoundInChannelsError` in Conda's `exception_name` become an empty state;
other nonzero exits show a short generic error without raw stdout/stderr,
channel URLs, tokens, or traceback. The search window keeps its last valid
results until the next explicit search, and shows which channel mode was used.

Selecting a result enables `安装此版本` only when a non-base environment is
currently selected. It opens the existing install form prefilled with name and
version; the user can still edit them. The selected build is informational and
is not pinned. The form explains that Conda may select a different build or
channel, and the existing dry-run confirmation shows the actual planned
transaction before any change. Closing search or declining confirmation does
not invoke package installation.

## Channel management

The channel window offers `沿用 Conda 当前渠道` or `使用本应用自定义渠道`.
Custom mode requires at least one entry. Users can add, remove, and move
entries up or down; list order is priority order. A simple channel name must
match `[A-Za-z0-9][A-Za-z0-9._-]{0,127}` (including `defaults` or
`conda-forge`). It also accepts an HTTPS URL without userinfo,
query, fragment, whitespace, or a Conda `/t/<token>/` path. HTTP, `file://`,
local paths, and credential-bearing URLs are rejected. Duplicates are removed
after normalization using case-insensitive comparison. URL scheme and host
are lowercased, trailing slashes are removed, and the path is preserved.
The UI explains that names resolve through the user's
existing Conda channel alias and warns that mixing incompatible channels can
lead to dependency conflicts. The exact ordered list is visible before Save.

The app persists a normalized root-to-profile map in an atomic JSON file.
Malformed settings fall back to inherited mode with a visible diagnostic;
write failures are reported without claiming persistence. Removing the last
custom channel switches to inherited mode rather than passing an invalid
`--override-channels` command. Profiles are loaded when an installation is
selected; changing installations loads its own profile. A channel change
invalidates visible remote-search results. The list can be inspected and
edited without a network request. A read-only `conda config --show channels
--json` query displays the inherited channel names when it succeeds. It uses
the same safe label rules as search results; failure shows a generic
diagnostic and does not block use of the inherited mode.

## Core and integration boundaries

`ChannelPolicy` validates and normalizes user entries. `ChannelProfileStore`
persists per-root profiles. `CondaPackageSearch` owns query validation, CLI
arguments, timeouts, JSON parsing, safe channel labels, and no-match handling.
`AvailablePackageSearchViewModel` owns search state, cancellation, and stale
result rejection. WPF owns dialogs and selection. The existing
`PackageOperationRequest` gains an immutable channel snapshot; its command
builder appends the same channel flags during preview and apply, including
update/remove solver operations. All child processes retain
`CONDA_REPORT_ERRORS=false`. No shell is used.

Tests use fake runners and temporary app settings paths. They cover channel
validation/order/persistence, search arguments and JSON cases, no-match,
credentials in raw output, cancellation/stale results, inherited versus custom
flags, and equal preview/apply channels. An offscreen WPF smoke loads both
windows and the prefilled installation path. Automated checks do not mutate a
real Conda environment or `.condarc`. Full checks/build and self-contained
`win-x64` publish must pass.

Conda documents [search](https://docs.conda.io/projects/conda/en/stable/commands/search.html),
[channel override behavior](https://docs.conda.io/projects/conda/en/stable/user-guide/tasks/manage-channels.html),
and [config scopes](https://docs.conda.io/projects/conda/en/stable/commands/config.html).
