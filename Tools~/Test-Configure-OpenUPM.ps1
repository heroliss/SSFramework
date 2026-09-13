#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ConsumerProject,
    [string] $PackageManifestPath,
    [string] $OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'SSFrameworkSetupTests')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $PackageManifestPath) { $PackageManifestPath = Join-Path $PSScriptRoot '../package.json' }
$tool = Join-Path $PSScriptRoot 'Configure-OpenUPM.ps1'
$runRoot = Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))
$script:passed = 0
$emptyManifest = '{"dependencies":{"com.unity.inputsystem":"1.20.0"},"testables":["com.example.tests"],"custom":{"nested":[{"keep":true}]}}'

function Assert($Condition, [string] $Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}
function Read-Manifest([string] $Root) {
    return [IO.File]::ReadAllText((Join-Path $Root 'Packages/manifest.json')) | ConvertFrom-Json
}
function Manifest-Bytes([string] $Root) {
    return [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $Root 'Packages/manifest.json')))
}
function New-Project([string] $Name, [string] $Json = $emptyManifest) {
    $root = Join-Path $runRoot $Name
    foreach ($directory in @('Assets', 'Packages', 'ProjectSettings')) {
        [IO.Directory]::CreateDirectory((Join-Path $root $directory)) | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $root 'Packages/manifest.json'), $Json, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $root 'ProjectSettings/ProjectVersion.txt'), 'm_EditorVersion: 6000.3.23f1')
    [IO.File]::WriteAllText((Join-Path $root 'Packages/packages-lock.json'), '{"sentinel":"unchanged"}')
    return $root
}
function Invoke-Case([string] $Name, [scriptblock] $Body) {
    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}
function Assert-Rejected([string] $Name, [string] $Json, [string] $ExpectedMessage) {
    $root = New-Project $Name $Json
    $before = Manifest-Bytes $root
    $failure = ''
    try { & $tool -ProjectPath $root -Apply 6>$null } catch { $failure = $_.Exception.Message }
    Assert ($failure -like "*$ExpectedMessage*") "Expected rejection '$ExpectedMessage'; got '$failure'."
    Assert ((Manifest-Bytes $root) -ceq $before) 'Rejected operation changed manifest.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Rejected operation created backup directories.'
}

Invoke-Case 'Preview never writes files' {
    $root = New-Project 'preview'
    $before = Manifest-Bytes $root
    & $tool -ProjectPath $root 6>$null
    Assert ((Manifest-Bytes $root) -ceq $before) 'Preview changed manifest.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Preview created backup directories.'
}
Invoke-Case 'WhatIf never writes files' {
    $root = New-Project 'what-if'
    $before = Manifest-Bytes $root
    & $tool -ProjectPath $root -Apply -WhatIf 6>$null
    Assert ((Manifest-Bytes $root) -ceq $before) 'WhatIf changed manifest.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'WhatIf created directories.'
}
Invoke-Case 'Apply preserves dependencies, other keys, lock and exact original backup' {
    $root = New-Project ('space [' + [char]0x6708 + '] project')
    $before = Manifest-Bytes $root
    & $tool -ProjectPath $root -Apply 6>$null
    $result = Read-Manifest $root
    Assert ($result.scopedRegistries.Count -eq 1) 'Expected one registry.'
    Assert ($result.scopedRegistries[0].scopes.Count -eq 6) 'Expected six scopes.'
    Assert ($result.dependencies.'com.unity.inputsystem' -eq '1.20.0') 'Dependency changed.'
    Assert ($result.testables[0] -eq 'com.example.tests') 'testables changed.'
    Assert ($result.custom.nested[0].keep) 'Custom nested data changed.'
    Assert ((Get-Content -LiteralPath (Join-Path $root 'Packages/packages-lock.json') -Raw) -ceq '{"sentinel":"unchanged"}') 'Lock changed.'
    $backups = @(Get-ChildItem -LiteralPath (Join-Path $root 'UserSettings/SSFrameworkSetup') -File)
    Assert ($backups.Count -eq 1) 'Expected exactly one backup.'
    Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($backups[0].FullName)) -ceq $before) 'Backup differs from original bytes.'
    $configured = Manifest-Bytes $root
    & $tool -ProjectPath $root -Apply 6>$null
    Assert ((Manifest-Bytes $root) -ceq $configured) 'Repeat application changed bytes.'
    Assert (@(Get-ChildItem -LiteralPath $backups[0].DirectoryName -File).Count -eq 1) 'Repeat application created a backup.'
}
Invoke-Case 'Empty registry array is supported' {
    $root = New-Project 'empty-array' '{"dependencies":{},"scopedRegistries":[]}'
    & $tool -ProjectPath $root -Apply 6>$null
    Assert ((Read-Manifest $root).scopedRegistries.Count -eq 1) 'Empty array not populated.'
}
Invoke-Case 'UTF-8 with or without BOM preserves non-ASCII content' {
    foreach ($bom in @($false, $true)) {
        $name = [string][char]0x6708 + [char]0x7403
        $root = New-Project "unicode-$bom" ('{"dependencies":{},"customName":"' + $name + '"}')
        $path = Join-Path $root 'Packages/manifest.json'
        $json = [IO.File]::ReadAllText($path)
        [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($bom))
        $before = Manifest-Bytes $root
        & $tool -ProjectPath $root -Apply 6>$null
        Assert ((Read-Manifest $root).customName -ceq $name) 'Non-ASCII text was corrupted.'
        $backup = Get-ChildItem -LiteralPath (Join-Path $root 'UserSettings/SSFrameworkSetup') -File
        Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($backup.FullName)) -ceq $before) 'BOM backup was changed.'
    }
}
Invoke-Case 'Existing four scopes and unrelated registry are preserved' {
    $json = '{"dependencies":{},"scopedRegistries":[{"name":"Internal","url":"https://example.invalid","scopes":["com.company"],"extra":true},{"name":"Our OpenUPM","url":"https://package.openupm.com/","scopes":["com.cysharp.unitask","com.cysharp.r3","com.code-philosophy.luban","com.tuyoogame.yooasset"]}]}'
    $root = New-Project 'upgrade' $json
    & $tool -ProjectPath $root -Apply 6>$null
    $registries = (Read-Manifest $root).scopedRegistries
    Assert ($registries.Count -eq 2) 'Registry duplicated.'
    Assert ($registries[0].extra -and $registries[0].scopes[0] -ceq 'com.company') 'Unrelated registry changed.'
    Assert ($registries[1].scopes.Count -eq 6 -and $registries[1].name -ceq 'Our OpenUPM') 'Upgrade or custom name lost.'
}
Invoke-Case 'Existing covering scopes remain sufficient without duplicate entries' {
    $root = New-Project 'covering' '{"dependencies":{},"scopedRegistries":[{"name":"OpenUPM","url":"https://package.openupm.com","scopes":["com.cysharp","com.code-philosophy","com.tuyoogame","org.nuget"]}]}'
    $before = Manifest-Bytes $root
    & $tool -ProjectPath $root -Apply 6>$null
    Assert ((Manifest-Bytes $root) -ceq $before) 'Already sufficient scopes changed.'
}
Invoke-Case 'Broader unrelated routes allow exact OpenUPM overrides' {
    $root = New-Project 'broad-other' '{"dependencies":{},"scopedRegistries":[{"name":"Internal","url":"https://example.invalid","scopes":["com","org"]}]}'
    & $tool -ProjectPath $root -Apply 6>$null
    Assert ((Read-Manifest $root).scopedRegistries.Count -eq 2) 'Broader unrelated registry was not preserved.'
}
Invoke-Case 'Reject competing exact package source' {
    Assert-Rejected 'exact-conflict' '{"dependencies":{},"scopedRegistries":[{"name":"Internal","url":"https://example.invalid","scopes":["com.cysharp.r3"]}]}' 'source conflict'
}
Invoke-Case 'Reject more specific NuGet source' {
    Assert-Rejected 'nuget-conflict' '{"dependencies":{},"scopedRegistries":[{"name":"Internal","url":"https://example.invalid","scopes":["org.nuget.r3"]}]}' 'source conflict'
}
Invoke-Case 'Reject duplicate OpenUPM registries' {
    Assert-Rejected 'duplicate-registry' '{"dependencies":{},"scopedRegistries":[{"name":"One","url":"https://package.openupm.com","scopes":[]},{"name":"Two","url":"https://package.openupm.com/","scopes":[]}]}' 'Multiple OpenUPM'
}
Invoke-Case 'Reject misleading OpenUPM URL' {
    Assert-Rejected 'url-conflict' '{"dependencies":{},"scopedRegistries":[{"name":"OpenUPM","url":"https://example.invalid","scopes":[]}]}' 'different URL'
}
Invoke-Case 'Reject malformed registry structure' {
    Assert-Rejected 'bad-array' '{"dependencies":{},"scopedRegistries":{"name":"OpenUPM"}}' 'must be an array'
    Assert-Rejected 'bad-scopes' '{"dependencies":{},"scopedRegistries":[{"name":"OpenUPM","url":"https://package.openupm.com","scopes":"org.nuget"}]}' 'scopes must be an array'
    Assert-Rejected 'null-registry' '{"dependencies":{},"scopedRegistries":[null]}' 'Invalid scoped registry'
    Assert-Rejected 'bad-dependencies' '{"dependencies":[]}' 'dependencies object'
}
Invoke-Case 'Open Unity lock prevents writes; stale unlocked file permits apply' {
    $root = New-Project 'unity-lock'
    $before = Manifest-Bytes $root
    [IO.Directory]::CreateDirectory((Join-Path $root 'Temp')) | Out-Null
    $lockPath = Join-Path $root 'Temp/UnityLockfile'
    $handle = [IO.File]::Open($lockPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $failure = ''
        try { & $tool -ProjectPath $root -Apply 6>$null } catch { $failure = $_.Exception.Message }
        Assert ($failure -like '*Close the Editor*') 'Live lock did not reject write.'
        Assert ((Manifest-Bytes $root) -ceq $before) 'Live lock rejection changed manifest.'
        & $tool -ProjectPath $root 6>$null
    } finally { $handle.Dispose() }
    & $tool -ProjectPath $root -Apply 6>$null
    Assert ((Read-Manifest $root).scopedRegistries.Count -eq 1) 'Stale unlocked lock blocked apply.'
    Assert (Test-Path -LiteralPath $lockPath) 'Lock file was deleted.'
}
Invoke-Case 'Invalid JSON fails without modifying original' {
    Assert-Rejected 'invalid-json' '{"dependencies":' ''
}
Invoke-Case 'All declared non-Unity package dependencies are covered' {
    $root = New-Project 'dependency-coverage'
    & $tool -ProjectPath $root -Apply 6>$null
    $scopes = (Read-Manifest $root).scopedRegistries[0].scopes
    $package = [IO.File]::ReadAllText($PackageManifestPath) | ConvertFrom-Json
    foreach ($name in $package.dependencies.PSObject.Properties.Name) {
        if ($name.StartsWith('com.unity.')) { continue }
        Assert (@($scopes | Where-Object { $name -ceq $_ -or $name.StartsWith($_ + '.', [StringComparison]::Ordinal) }).Count -gt 0) "Uncovered dependency: $name"
    }
}
if ($ConsumerProject) {
    Invoke-Case 'Real consumer manifest: read-only preview plus apply on byte-for-byte copy' {
        $before = Manifest-Bytes $ConsumerProject
        & $tool -ProjectPath $ConsumerProject 6>$null
        Assert ((Manifest-Bytes $ConsumerProject) -ceq $before) 'Consumer preview changed original.'
        $root = New-Project 'consumer-copy'
        [IO.File]::WriteAllBytes((Join-Path $root 'Packages/manifest.json'), [Convert]::FromBase64String($before))
        & $tool -ProjectPath $root -Apply 6>$null
        $original = Read-Manifest $ConsumerProject
        $updated = Read-Manifest $root
        Assert (($original.dependencies | ConvertTo-Json -Depth 100 -Compress) -ceq ($updated.dependencies | ConvertTo-Json -Depth 100 -Compress)) 'Consumer dependencies changed.'
        Assert ($updated.scopedRegistries.Count -gt 0) 'Consumer copy not configured.'
        Assert ((Manifest-Bytes $ConsumerProject) -ceq $before) 'Consumer original changed after copy test.'
    }
}
Write-Host "$script:passed test groups passed. Fixtures: $runRoot"
