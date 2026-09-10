#requires -Version 5.1
<#
.SYNOPSIS
Previews or configures SSFramework's OpenUPM scopes in a Unity project.
.DESCRIPTION
Standalone bootstrap: no Unity, Git, Node, network or installed package required.
By default this script only previews. -Apply writes with an atomic backup;
-Interactive asks for a project path and offers to apply the displayed plan.
Close the target Unity Editor before applying. Package installation remains in UPM.
.EXAMPLE
& ./Configure-OpenUPM.ps1 -ProjectPath 'D:/Games/MyGame'
.EXAMPLE
& ./Configure-OpenUPM.ps1 -ProjectPath 'D:/Games/MyGame' -Apply
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ProjectPath,
    [switch] $Apply,
    [switch] $Interactive
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-JsonObject($Value) {
    return $null -ne $Value -and $Value -is [System.Management.Automation.PSCustomObject]
}

function Test-ScopeCoverage([string] $Scope, [string] $Package) {
    return $Package -ceq $Scope -or $Package.StartsWith($Scope + '.', [StringComparison]::Ordinal)
}

if ([string]::IsNullOrWhiteSpace($ProjectPath) -and $Interactive) {
    $ProjectPath = (Read-Host 'Unity project folder (paste path; empty cancels)').Trim().Trim('"')
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    throw 'Provide -ProjectPath pointing to the Unity project root.'
}

$projectRoot = (Resolve-Path -LiteralPath $ProjectPath).ProviderPath
foreach ($relative in @('Assets', 'Packages', 'ProjectSettings')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Container)) {
        throw "Not a Unity project root: missing $relative."
    }
}
$manifestPath = Join-Path $projectRoot 'Packages/manifest.json'
$versionPath = Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
    throw 'Missing ProjectSettings/ProjectVersion.txt.'
}
$originalBytes = [IO.File]::ReadAllBytes($manifestPath)
$originalText = [IO.File]::ReadAllText($manifestPath)
$manifest = ConvertFrom-Json -InputObject $originalText
if (-not (Test-JsonObject $manifest) -or
    -not $manifest.PSObject.Properties['dependencies'] -or
    -not (Test-JsonObject $manifest.dependencies)) {
    throw 'manifest.json must contain a dependencies object.'
}

$registryUrl = 'https://package.openupm.com'
$requiredScopes = @(
    'com.cysharp.unitask',
    'com.cysharp.r3',
    'com.code-philosophy.luban',
    'com.tuyoogame.yooasset',
    'com.code-philosophy.hybridclr',
    'org.nuget'
)
$registries = @()
if ($manifest.PSObject.Properties['scopedRegistries']) {
    if ($manifest.scopedRegistries -isnot [Array]) {
        throw 'scopedRegistries must be an array.'
    }
    $registries = @($manifest.scopedRegistries)
}

$openUpm = $null
foreach ($registry in $registries) {
    if (-not (Test-JsonObject $registry)) { throw 'Invalid scoped registry object.' }
    foreach ($key in @('name', 'url', 'scopes')) {
        if (-not $registry.PSObject.Properties[$key]) { throw "Registry is missing $key." }
    }
    if ($registry.name -isnot [string] -or [string]::IsNullOrWhiteSpace($registry.name) -or
        $registry.url -isnot [string] -or $registry.scopes -isnot [Array]) {
        throw 'Registry name/url must be strings and scopes must be an array.'
    }
    foreach ($scope in $registry.scopes) {
        if ($scope -isnot [string] -or $scope -cnotmatch '^[a-z0-9_-]+(\.[a-z0-9_-]+)*$') {
            throw "Invalid scope in registry '$($registry.name)'. Resolve it in Package Manager first."
        }
    }

    $isOpenUpm = $registry.url.TrimEnd('/') -ieq $registryUrl
    if ($isOpenUpm) {
        if ($null -ne $openUpm) { throw 'Multiple OpenUPM registries found; consolidate them first.' }
        $openUpm = $registry
        continue
    }
    if ($registry.name -ieq 'OpenUPM') {
        throw "A registry named OpenUPM uses a different URL. Review its source in Package Manager first."
    }
    foreach ($scope in $registry.scopes) {
        # More specific scopes in another registry could intercept NuGet dependencies.
        # Broader scopes such as 'com' are safe to override with our exact package scopes.
        if ($requiredScopes -ccontains $scope -or $scope.StartsWith('org.nuget.', [StringComparison]::Ordinal)) {
            throw "Scope '$scope' belongs to registry '$($registry.name)'. Resolve this source conflict first."
        }
    }
}

$createRegistry = $null -eq $openUpm
if ($createRegistry) {
    $openUpm = [pscustomobject]@{ name = 'OpenUPM'; url = $registryUrl; scopes = @() }
}
$addedScopes = @($requiredScopes | Where-Object {
    $required = $_
    @($openUpm.scopes | Where-Object { Test-ScopeCoverage $_ $required }).Count -eq 0
})

$versionLine = Get-Content -LiteralPath $versionPath | Where-Object { $_ -match '^m_EditorVersion:' } | Select-Object -First 1
Write-Host "Project: $projectRoot"
Write-Host "$versionLine"
Write-Host "Registry: $registryUrl"
if ($versionLine -notmatch '6000\.3\.') {
    Write-Warning 'The full SSFramework package currently targets Unity 6.3 LTS. Check compatibility before installation.'
}
if ($addedScopes.Count -eq 0) {
    Write-Host 'Already configured. No files changed.'
    return
}
if ($createRegistry) { Write-Host 'Create OpenUPM registry.' }
foreach ($scope in $addedScopes) { Write-Host "  Add scope: $scope" }
Write-Host 'Only Packages/manifest.json scopedRegistries will change (JSON formatting may change).'
Write-Host 'Backup: UserSettings/SSFrameworkSetup/manifest-<unique-id>.json'
Write-Host 'Package versions, dependencies and packages-lock.json are preserved.'

if ($Interactive -and -not $Apply -and -not $WhatIfPreference) {
    $Apply = (Read-Host 'Close this project in Unity. Apply this configuration? [y/N]') -ieq 'y'
}
if (-not $Apply) {
    Write-Host 'Preview only. Run again with -Apply to write the configuration.'
    return
}
if (-not $PSCmdlet.ShouldProcess($manifestPath, 'Merge OpenUPM scopes and back up original manifest')) { return }

# Unity owns this lock while the project is open. Do not remove even a stale lock.
$unityLockPath = Join-Path $projectRoot 'Temp/UnityLockfile'
if (Test-Path -LiteralPath $unityLockPath) {
    try {
        $lockProbe = [IO.File]::Open($unityLockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $lockProbe.Dispose()
    } catch {
        throw 'The Unity project is open or its lock is inaccessible. Close the Editor and retry.'
    }
}

$openUpm.scopes = @($openUpm.scopes) + $addedScopes
if ($createRegistry) { $registries += $openUpm }
if ($manifest.PSObject.Properties['scopedRegistries']) {
    $manifest.scopedRegistries = @($registries)
} else {
    $manifest | Add-Member -MemberType NoteProperty -Name scopedRegistries -Value @($registries)
}
$updatedText = ($manifest | ConvertTo-Json -Depth 100) + [Environment]::NewLine
$backupDirectory = Join-Path $projectRoot 'UserSettings/SSFrameworkSetup'
$operationId = [Guid]::NewGuid().ToString('N')
$backupPath = Join-Path $backupDirectory "manifest-$operationId.json"
$temporaryPath = Join-Path $projectRoot "Packages/ssframework-$operationId.tmp"

try {
    [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
    [IO.File]::WriteAllText($temporaryPath, $updatedText, [Text.UTF8Encoding]::new($false))
    # Refuse to replace edits made after preview; File.Replace backs up the original bytes.
    $currentBytes = [IO.File]::ReadAllBytes($manifestPath)
    if ([Convert]::ToBase64String($currentBytes) -cne [Convert]::ToBase64String($originalBytes)) {
        throw 'manifest.json changed after preview. Run the tool again to review the new plan.'
    }
    [IO.File]::Replace($temporaryPath, $manifestPath, $backupPath)
} finally {
    if ([IO.File]::Exists($temporaryPath)) { [IO.File]::Delete($temporaryPath) }
}
Write-Host "Configured. Original manifest: $backupPath"
Write-Host 'Reopen Unity, then add the reviewed SSFramework Git URL in Package Manager.'
