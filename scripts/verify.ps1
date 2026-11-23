$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

dotnet restore (Join-Path $repoRoot 'CondaManager.slnx') --source $repoRoot -p:NuGetAudit=false -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build (Join-Path $repoRoot 'CondaManager.slnx') --no-restore -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project (Join-Path $repoRoot 'tests/CondaManager.Checks/CondaManager.Checks.csproj') --no-restore -p:UseSharedCompilation=false
exit $LASTEXITCODE
