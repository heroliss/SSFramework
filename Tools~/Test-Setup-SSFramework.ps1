#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ConsumerProject,
    [string] $OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'SSFrameworkSetupTests')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'Setup-SSFramework.ps1'
$runRoot = Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))
$script:passed = 0
$emptyManifest = '{"dependencies":{"com.unity.inputsystem":"1.20.0"},"testables":["com.example.tests"],"custom":{"nested":[{"keep":true}]}}'
$ankleId = 'com.anklebreaker.unity-mcp'
$ankleUrl = 'https://github.com/AnkleBreaker-Studio/unity-mcp-plugin.git#v2.39.5'
$coplayId = 'com.coplaydev.unity-mcp'
$coplayUrl = 'https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.2.0'
function Assert($Condition, [string] $Message) { if (-not $Condition) { throw "Assertion failed: $Message" } }
function Read-Manifest([string] $Root) { return [IO.File]::ReadAllText((Join-Path $Root 'Packages/manifest.json')) | ConvertFrom-Json }
function File-Bytes([string] $Path) { return [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) }
function Manifest-Bytes([string] $Root) { return File-Bytes (Join-Path $Root 'Packages/manifest.json') }
function Write-Json([string] $Path, [string] $Json) { [IO.File]::WriteAllText($Path, $Json, [Text.UTF8Encoding]::new($false)) }
function New-Project([string] $Name, [string] $Json = $emptyManifest) {
    $root = Join-Path $runRoot $Name
    foreach ($directory in @('Assets', 'Packages', 'ProjectSettings')) { [IO.Directory]::CreateDirectory((Join-Path $root $directory)) | Out-Null }
    Write-Json (Join-Path $root 'Packages/manifest.json') $Json
    Write-Json (Join-Path $root 'Packages/packages-lock.json') '{"dependencies":{"com.unity.inputsystem":{"version":"1.20.0","source":"registry"}}}'
    Write-Json (Join-Path $root 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 6000.3.23f1'
    return $root
}
function Invoke-Case([string] $Name, [scriptblock] $Body) { & $Body; $script:passed++; Write-Host "PASS $Name" }
function Assert-Rejected([string] $Root, [string] $Message, [hashtable] $Options = @{}) {
    $before = Manifest-Bytes $Root
    $failure = ''
    try { & $tool -ProjectPath $Root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Apply @Options 6>$null } catch { $failure = $_.Exception.Message }
    Assert ($failure -like "*$Message*") "Expected '$Message'; got '$failure'."
    Assert ((Manifest-Bytes $Root) -ceq $before) 'Rejected operation changed manifest.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $Root 'UserSettings'))) 'Rejected operation created backup directories.'
}
Invoke-Case 'Preview and WhatIf never write MCP dependencies, scopes or backups' {
    foreach ($whatIf in @($false, $true)) {
        $root = New-Project "preview-$whatIf"
        $before = Manifest-Bytes $root
        $result = & $tool -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Apply:$whatIf -WhatIf:$whatIf -PassThru 6>$null
        Assert ($result.ManifestChanged -and -not $result.Applied) 'Preview result is wrong.'
        Assert ((Manifest-Bytes $root) -ceq $before) 'Preview changed manifest.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Preview created backups.'
    }
}
Invoke-Case 'Manual mode applies only package sources and returns pinned instructions' {
    $root = New-Project 'manual'
    $before = (Read-Manifest $root).dependencies | ConvertTo-Json -Compress
    $result = & $tool -ProjectPath $root -UnityMcp AnkleBreaker -Apply -PassThru 6>$null
    Assert ($result.Applied -and -not $result.AddedMcpPackage) 'Manual mode did not apply sources only.'
    Assert (((Read-Manifest $root).dependencies | ConvertTo-Json -Compress) -ceq $before) 'Manual mode installed MCP.'
    Assert ($result.Mcp.GitUrl -ceq $ankleUrl -and $result.Mcp.ServerVersion -ceq '2.35.6') 'Wrong pinned AnkleBreaker pair.'
    Assert ($result.Mcp.ServerEnvironment.UNITY_MCP_COMPACT_TOOLS -ceq '1') 'Compact tools hint missing.'
}
Invoke-Case 'Each provider merges one dependency; exact backup and repeat no-op' {
    foreach ($provider in @('AnkleBreaker', 'Coplay')) {
        $root = New-Project ("space [" + [char]0x6708 + "] $provider")
        $before = Manifest-Bytes $root
        $lockBefore = File-Bytes (Join-Path $root 'Packages/packages-lock.json')
        $versionBefore = File-Bytes (Join-Path $root 'ProjectSettings/ProjectVersion.txt')
        $result = & $tool -ProjectPath $root -UnityMcp $provider -McpInstallMode Manifest -Apply -PassThru 6>$null
        $manifest = Read-Manifest $root
        $expectedId = if ($provider -eq 'AnkleBreaker') { $ankleId } else { $coplayId }
        $expectedUrl = if ($provider -eq 'AnkleBreaker') { $ankleUrl } else { $coplayUrl }
        Assert ($result.Applied -and $result.AddedMcpPackage) 'Apply did not add MCP.'
        Assert ($manifest.dependencies.PSObject.Properties[$expectedId].Value -ceq $expectedUrl) 'Wrong package source.'
        Assert (@($manifest.dependencies.PSObject.Properties).Count -eq 2) 'Unselected dependencies added.'
        Assert ($manifest.dependencies.'com.unity.inputsystem' -ceq '1.20.0') 'Input dependency changed.'
        Assert ($manifest.custom.nested[0].keep -and $manifest.testables[0] -ceq 'com.example.tests') 'Other fields changed.'
        Assert ((File-Bytes $result.BackupPath) -ceq $before) 'Backup differs from original bytes.'
        Assert ((File-Bytes (Join-Path $root 'Packages/packages-lock.json')) -ceq $lockBefore) 'Lock was rewritten.'
        Assert ((File-Bytes (Join-Path $root 'ProjectSettings/ProjectVersion.txt')) -ceq $versionBefore) 'Settings changed.'
        $after = Manifest-Bytes $root
        $repeated = & $tool -ProjectPath $root -UnityMcp $provider -McpInstallMode Manifest -Apply -PassThru 6>$null
        Assert (-not $repeated.ManifestChanged -and -not $repeated.Applied) 'Repeat is not a no-op.'
        Assert ((Manifest-Bytes $root) -ceq $after) 'Repeat changed bytes.'
        Assert (@(Get-ChildItem -LiteralPath (Join-Path $root 'UserSettings/SSFrameworkSetup') -File).Count -eq 1) 'Repeat created a second backup.'
    }
}
Invoke-Case 'None preserves installed MCP; SkipOpenUPM supports independent MCP setup' {
    $root = New-Project 'skip' ('{"dependencies":{"' + $ankleId + '":"' + $ankleUrl + '"}}')
    $before = Manifest-Bytes $root
    $result = & $tool -ProjectPath $root -UnityMcp None -SkipOpenUPM -Apply -PassThru 6>$null
    Assert (-not $result.ManifestChanged -and (Manifest-Bytes $root) -ceq $before) 'None removed or rewrote an existing MCP.'
    $root = New-Project 'independent'
    $result = & $tool -ProjectPath $root -UnityMcp Coplay -McpInstallMode Manifest -SkipOpenUPM -Apply -PassThru 6>$null
    Assert ($result.Applied -and $result.AddedScopes.Count -eq 0) 'Independent setup changed sources.'
    Assert ($null -eq (Read-Manifest $root).PSObject.Properties['scopedRegistries']) 'Independent setup added a registry.'
}
Invoke-Case 'Different pinned version or registry source rejects the entire plan' {
    foreach ($version in @('1.0.0', 'https://github.com/AnkleBreaker-Studio/unity-mcp-plugin.git#main')) {
        $root = New-Project ([Guid]::NewGuid().ToString('N')) ('{"dependencies":{"' + $ankleId + '":"' + $version + '"}}')
        Assert-Rejected $root 'different version or source'
    }
}
Invoke-Case 'Other provider in manifest or lock rejects the entire plan' {
    $root = New-Project 'other-direct' ('{"dependencies":{"' + $coplayId + '":"' + $coplayUrl + '"}}')
    Assert-Rejected $root 'Another Unity MCP provider'
    $root = New-Project 'other-transitive'
    Write-Json (Join-Path $root 'Packages/packages-lock.json') ('{"dependencies":{"' + $coplayId + '":{"version":"10.2.0"}}}')
    Assert-Rejected $root 'Another Unity MCP provider'
}
Invoke-Case 'Embedded package identity is detected independently of directory name' {
    foreach ($id in @($ankleId, $coplayId)) {
        $root = New-Project ([Guid]::NewGuid().ToString('N'))
        $embedded = Join-Path $root 'Packages/arbitrary-folder'
        [IO.Directory]::CreateDirectory($embedded) | Out-Null
        Write-Json (Join-Path $embedded 'package.json') ('{"name":"' + $id + '","version":"1.0.0"}')
        $message = if ($id -eq $ankleId) { 'selected MCP is embedded' } else { 'Another Unity MCP provider' }
        Assert-Rejected $root $message
    }
}
Invoke-Case 'Same provider owned by another dependency is not silently repinned' {
    $root = New-Project 'same-transitive'
    Write-Json (Join-Path $root 'Packages/packages-lock.json') ('{"dependencies":{"' + $ankleId + '":{"version":"2.39.5"}}}')
    Assert-Rejected $root 'without a direct manifest entry'
}
Invoke-Case 'Manual instructions preserve other MCP installations' {
    $root = New-Project 'manual-other' ('{"dependencies":{"' + $coplayId + '":"' + $coplayUrl + '"}}')
    $before = Manifest-Bytes $root
    $result = & $tool -ProjectPath $root -UnityMcp AnkleBreaker -SkipOpenUPM -Apply -PassThru 6>$null
    Assert ((Manifest-Bytes $root) -ceq $before) 'Manual instructions changed installed providers.'
    Assert ($result.DetectedMcp.Count -eq 1) 'Existing provider was not reported.'
}
Invoke-Case 'Source conflict cannot partially install MCP' {
    $root = New-Project 'source-conflict' '{"dependencies":{},"scopedRegistries":[{"name":"Internal","url":"https://example.invalid","scopes":["org.nuget.r3"]}]}'
    Assert-Rejected $root 'source conflict'
}
Invoke-Case 'Live Unity lock rejects MCP writes while preview remains available' {
    $root = New-Project 'unity-lock'
    [IO.Directory]::CreateDirectory((Join-Path $root 'Temp')) | Out-Null
    $lockPath = Join-Path $root 'Temp/UnityLockfile'
    $handle = [IO.File]::Open($lockPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Assert-Rejected $root 'Close the Editor' @{ SkipOpenUPM = $true }
        & $tool -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest 6>$null
    } finally { $handle.Dispose() }
    & $tool -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Apply 6>$null
    Assert (Test-Path -LiteralPath $lockPath) 'Lock file was deleted.'
}
Invoke-Case 'UTF-8 and BOM preserve non-ASCII data and exact original bytes' {
    foreach ($bom in @($false, $true)) {
        $root = New-Project "unicode-$bom"
        $path = Join-Path $root 'Packages/manifest.json'
        $word = [string][char]0x6708 + [char]0x7403
        [IO.File]::WriteAllText($path, ('{"dependencies":{},"name":"' + $word + '"}'), [Text.UTF8Encoding]::new($bom))
        $before = Manifest-Bytes $root
        $result = & $tool -ProjectPath $root -UnityMcp Coplay -McpInstallMode Manifest -Apply -PassThru 6>$null
        Assert ((Read-Manifest $root).name -ceq $word) 'Unicode changed.'
        Assert ((File-Bytes $result.BackupPath) -ceq $before) 'BOM backup differs.'
    }
}
Invoke-Case 'Manifest edited after preview is preserved and application is rejected' {
    $root = New-Project 'changed-after-preview'
    $racePath = Join-Path $root 'Packages/manifest.json'
    function Read-Host { param($Prompt) Write-Json $racePath '{"dependencies":{},"userEdit":true}'; return 'y' }
    $failure = ''
    try { & $tool -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Interactive 6>$null } catch { $failure = $_.Exception.Message }
    Assert ($failure -like '*changed after preview*') "Concurrent edit was not rejected: $failure"
    Assert ((Read-Manifest $root).userEdit) 'User edit was overwritten.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Rejected edit created backups.'
}
Invoke-Case 'Interactive decline leaves no manifest changes' {
    $root = New-Project 'decline'
    $before = Manifest-Bytes $root
    function Read-Host { param($Prompt) return 'n' }
    $result = & $tool -ProjectPath $root -UnityMcp Coplay -McpInstallMode Manifest -Interactive -PassThru 6>$null
    Assert (-not $result.Applied -and (Manifest-Bytes $root) -ceq $before) 'Decline applied changes.'
}
Invoke-Case 'Malformed lock fails before any write' {
    $root = New-Project 'bad-lock'
    Write-Json (Join-Path $root 'Packages/packages-lock.json') '{"dependencies":[]}'
    Assert-Rejected $root 'dependencies must be an object'
}
Invoke-Case 'Interactive choices default to manual mode and do not apply implicitly' {
    foreach ($providerChoice in @('', '1', '2')) {
        $root = New-Project ([Guid]::NewGuid().ToString('N'))
        $before = Manifest-Bytes $root
        $answers = [Collections.Generic.Queue[string]]::new()
        $answers.Enqueue($providerChoice)
        if ($providerChoice -ne '') { $answers.Enqueue('') }
        $answers.Enqueue('n')
        function Read-Host { param($Prompt) return $answers.Dequeue() }
        $result = & $tool -ProjectPath $root -Interactive -PassThru 6>$null
        $expected = switch ($providerChoice) { '' { 'None' }; '1' { 'AnkleBreaker' }; '2' { 'Coplay' } }
        Assert ($result.UnityMcp -ceq $expected -and $result.McpInstallMode -ceq 'Manual') 'Interactive selection/default is wrong.'
        Assert (-not $result.Applied -and (Manifest-Bytes $root) -ceq $before) 'Interactive defaults changed manifest.'
        Assert ($answers.Count -eq 0) 'Unexpected prompt count.'
    }
}
Invoke-Case 'Interactive cancellation and invalid selections never write files' {
    function Read-Host { param($Prompt) return '' }
    $result = & $tool -Interactive -PassThru 6>$null
    Assert ($null -eq $result) 'Empty project path did not cancel.'
    foreach ($badMode in @($false, $true)) {
        $root = New-Project ([Guid]::NewGuid().ToString('N'))
        $before = Manifest-Bytes $root
        $answers = [Collections.Generic.Queue[string]]::new()
        if ($badMode) { $answers.Enqueue('1') }
        $answers.Enqueue('invalid')
        function Read-Host { param($Prompt) return $answers.Dequeue() }
        $failure = ''
        try { & $tool -ProjectPath $root -Interactive 6>$null } catch { $failure = $_.Exception.Message }
        Assert ($failure -like '*Invalid*selection*' -or $failure -like '*Invalid install mode*') 'Invalid selection was not rejected.'
        Assert ((Manifest-Bytes $root) -ceq $before) 'Invalid selection changed manifest.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Invalid selection created backups.'
    }
}
if ($ConsumerProject) {
    Invoke-Case 'Real consumer: read-only preview and apply both choices to exact manifest copies' {
        $before = Manifest-Bytes $ConsumerProject
        $consumerLock = Join-Path $ConsumerProject 'Packages/packages-lock.json'
        $lockBefore = File-Bytes $consumerLock
        $consumerVersion = Join-Path $ConsumerProject 'ProjectSettings/ProjectVersion.txt'
        $versionBefore = File-Bytes $consumerVersion
        foreach ($provider in @('AnkleBreaker', 'Coplay')) {
            $preview = & $tool -ProjectPath $ConsumerProject -UnityMcp $provider -McpInstallMode Manifest -PassThru 6>$null
            Assert (-not $preview.Applied) 'Consumer preview applied changes.'
            $root = New-Project "consumer-$provider"
            foreach ($relative in @('Packages/manifest.json', 'Packages/packages-lock.json', 'ProjectSettings/ProjectVersion.txt')) {
                [IO.File]::WriteAllBytes((Join-Path $root $relative), [IO.File]::ReadAllBytes((Join-Path $ConsumerProject $relative)))
            }
            $result = & $tool -ProjectPath $root -UnityMcp $provider -McpInstallMode Manifest -Apply -PassThru 6>$null
            Assert ($result.Applied -and $result.AddedMcpPackage) 'Consumer copy not configured.'
            $updated = Read-Manifest $root
            foreach ($dependency in (Read-Manifest $ConsumerProject).dependencies.PSObject.Properties) {
                Assert ($updated.dependencies.PSObject.Properties[$dependency.Name].Value -ceq $dependency.Value) 'Consumer dependency changed.'
            }
            Assert ((File-Bytes $result.BackupPath) -ceq $before) 'Consumer backup differs.'
        }
        Assert ((Manifest-Bytes $ConsumerProject) -ceq $before) 'Actual consumer manifest changed.'
        Assert ((File-Bytes $consumerLock) -ceq $lockBefore) 'Actual consumer lock changed.'
        Assert ((File-Bytes $consumerVersion) -ceq $versionBefore) 'Actual consumer version changed.'
    }
}
Write-Host "$script:passed test groups passed. Fixtures: $runRoot"
