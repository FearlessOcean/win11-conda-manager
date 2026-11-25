$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'tests/CondaManager.WpfSmoke/CondaManager.WpfSmoke.csproj'
$work = Join-Path $repoRoot '.dotnet-temp/wpf-smoke'
$artifacts = Join-Path $work 'artifacts'
$variables = @('DOTNET_CLI_HOME', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'TEMP', 'TMP')
$previous = @{}
foreach ($name in $variables) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $env:DOTNET_CLI_HOME = (New-Item -ItemType Directory -Force (Join-Path $work 'cli')).FullName
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:TEMP = (New-Item -ItemType Directory -Force (Join-Path $work 'tmp')).FullName
    $env:TMP = $env:TEMP
    dotnet build $project --artifacts-path $artifacts --source $repoRoot -p:NuGetAudit=false -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet (Join-Path $artifacts 'bin/CondaManager.WpfSmoke/debug/CondaManager.WpfSmoke.dll')
    exit $LASTEXITCODE
}
finally {
    foreach ($name in $variables) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
