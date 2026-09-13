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
    try { & $tool -FrameworkInstallMode Manual -ProjectPath $Root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Apply @Options 6>$null } catch { $failure = $_.Exception.Message }
    Assert ($failure -like "*$Message*") "Expected '$Message'; got '$failure'."
    Assert ((Manifest-Bytes $Root) -ceq $before) 'Rejected operation changed manifest.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $Root 'UserSettings'))) 'Rejected operation created backup directories.'
}
Invoke-Case 'Preview and WhatIf never write MCP dependencies, scopes or backups' {
    foreach ($whatIf in @($false, $true)) {
        $root = New-Project "preview-$whatIf"
        $before = Manifest-Bytes $root
        $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Apply:$whatIf -WhatIf:$whatIf -PassThru 6>$null
        Assert ($result.ManifestChanged -and -not $result.Applied) 'Preview result is wrong.'
        Assert ((Manifest-Bytes $root) -ceq $before) 'Preview changed manifest.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Preview created backups.'
    }
}
Invoke-Case 'Manual mode applies only package sources and returns pinned instructions' {
    $root = New-Project 'manual'
    $before = (Read-Manifest $root).dependencies | ConvertTo-Json -Compress
    $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp AnkleBreaker -Apply -PassThru 6>$null
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
        $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp $provider -McpInstallMode Manifest -Apply -PassThru 6>$null
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
        $repeated = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp $provider -McpInstallMode Manifest -Apply -PassThru 6>$null
        Assert (-not $repeated.ManifestChanged -and -not $repeated.Applied) 'Repeat is not a no-op.'
        Assert ((Manifest-Bytes $root) -ceq $after) 'Repeat changed bytes.'
        Assert (@(Get-ChildItem -LiteralPath (Join-Path $root 'UserSettings/SSFrameworkSetup') -File).Count -eq 1) 'Repeat created a second backup.'
    }
}
Invoke-Case 'None preserves installed MCP; SkipOpenUPM supports independent MCP setup' {
    $root = New-Project 'skip' ('{"dependencies":{"' + $ankleId + '":"' + $ankleUrl + '"}}')
    $before = Manifest-Bytes $root
    $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp None -SkipOpenUPM -Apply -PassThru 6>$null
    Assert (-not $result.ManifestChanged -and (Manifest-Bytes $root) -ceq $before) 'None removed or rewrote an existing MCP.'
    $root = New-Project 'independent'
    $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp Coplay -McpInstallMode Manifest -SkipOpenUPM -Apply -PassThru 6>$null
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
    $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp AnkleBreaker -SkipOpenUPM -Apply -PassThru 6>$null
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
        & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest 6>$null
    } finally { $handle.Dispose() }
    & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Apply 6>$null
    Assert (Test-Path -LiteralPath $lockPath) 'Lock file was deleted.'
}
Invoke-Case 'UTF-8 and BOM preserve non-ASCII data and exact original bytes' {
    foreach ($bom in @($false, $true)) {
        $root = New-Project "unicode-$bom"
        $path = Join-Path $root 'Packages/manifest.json'
        $word = [string][char]0x6708 + [char]0x7403
        [IO.File]::WriteAllText($path, ('{"dependencies":{},"name":"' + $word + '"}'), [Text.UTF8Encoding]::new($bom))
        $before = Manifest-Bytes $root
        $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp Coplay -McpInstallMode Manifest -Apply -PassThru 6>$null
        Assert ((Read-Manifest $root).name -ceq $word) 'Unicode changed.'
        Assert ((File-Bytes $result.BackupPath) -ceq $before) 'BOM backup differs.'
    }
}
Invoke-Case 'Manifest edited after preview is preserved and application is rejected' {
    $root = New-Project 'changed-after-preview'
    $racePath = Join-Path $root 'Packages/manifest.json'
    function Read-Host { param($Prompt) Write-Json $racePath '{"dependencies":{},"userEdit":true}'; return 'y' }
    $failure = ''
    try { & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Interactive 6>$null } catch { $failure = $_.Exception.Message }
    Assert ($failure -like '*changed after preview*') "Concurrent edit was not rejected: $failure"
    Assert ((Read-Manifest $root).userEdit) 'User edit was overwritten.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Rejected edit created backups.'
}
Invoke-Case 'Interactive decline leaves no manifest changes' {
    $root = New-Project 'decline'
    $before = Manifest-Bytes $root
    function Read-Host { param($Prompt) return 'n' }
    $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp Coplay -McpInstallMode Manifest -Interactive -PassThru 6>$null
    Assert (-not $result.Applied -and (Manifest-Bytes $root) -ceq $before) 'Decline applied changes.'
}
Invoke-Case 'Malformed lock fails before any write' {
    $root = New-Project 'bad-lock'
    Write-Json (Join-Path $root 'Packages/packages-lock.json') '{"dependencies":[]}'
    Assert-Rejected $root 'dependencies must be an object'
}
Invoke-Case 'Interactive MCP defaults recommend manifest mode and do not apply implicitly' {
    foreach ($providerChoice in @('', '1', '2')) {
        $root = New-Project ([Guid]::NewGuid().ToString('N'))
        $before = Manifest-Bytes $root
        $answers = [Collections.Generic.Queue[string]]::new()
        $answers.Enqueue($providerChoice)
        if ($providerChoice -ne '') { $answers.Enqueue('') }
        $answers.Enqueue('n')
        function Read-Host { param($Prompt) return $answers.Dequeue() }
        $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -Interactive -PassThru 6>$null
        $expected = switch ($providerChoice) { '' { 'None' }; '1' { 'AnkleBreaker' }; '2' { 'Coplay' } }
        Assert ($result.UnityMcp -ceq $expected -and $result.McpInstallMode -ceq $(if ($providerChoice -eq '') { 'Manual' } else { 'Manifest' })) 'Interactive selection/default is wrong.'
        Assert (-not $result.Applied -and (Manifest-Bytes $root) -ceq $before) 'Interactive defaults changed manifest.'
        Assert ($answers.Count -eq 0) 'Unexpected prompt count.'
    }
}
Invoke-Case 'Interactive cancellation and invalid selections never write files' {
    function Read-Host { param($Prompt) return '' }
    $result = & $tool -FrameworkInstallMode Manual -Interactive -PassThru 6>$null
    Assert ($null -eq $result) 'Empty project path did not cancel.'
    foreach ($badMode in @($false, $true)) {
        $root = New-Project ([Guid]::NewGuid().ToString('N'))
        $before = Manifest-Bytes $root
        $answers = [Collections.Generic.Queue[string]]::new()
        if ($badMode) { $answers.Enqueue('1') }
        $answers.Enqueue('invalid')
        function Read-Host { param($Prompt) return $answers.Dequeue() }
        $failure = ''
        try { & $tool -FrameworkInstallMode Manual -ProjectPath $root -Interactive 6>$null } catch { $failure = $_.Exception.Message }
        Assert ($failure -like '*Invalid*selection*' -or $failure -like '*Invalid install mode*') 'Invalid selection was not rejected.'
        Assert ((Manifest-Bytes $root) -ceq $before) 'Invalid selection changed manifest.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Invalid selection created backups.'
    }
}
Invoke-Case 'Preview, confirmation, result and next steps are displayed in the correct order' {
    $root = New-Project 'display-order'
    $messages = [Collections.Generic.List[string]]::new()
    $confirmation = [pscustomobject]@{ Index = -1 }
    function Write-Host { param($Object, $ForegroundColor) $messages.Add([string]$Object) }
    function Read-Host { param($Prompt) $confirmation.Index = $messages.Count; return 'y' }
    $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Interactive -PassThru
    $beforeConfirmation = ($messages | Select-Object -First $confirmation.Index) -join "`n"
    $afterConfirmation = ($messages | Select-Object -Skip $confirmation.Index) -join "`n"
    Assert ($result.Applied) 'Confirmed application did not run.'
    Assert ($beforeConfirmation.Contains('[2/4]') -and -not $beforeConfirmation.Contains('[4/4]')) 'Next steps appeared before confirmation.'
    Assert ($afterConfirmation.IndexOf('[3/4]') -lt $afterConfirmation.IndexOf('[4/4]')) 'Result must precede next steps.'
    Assert (-not (($messages -join "`n").Contains('npx.cmd --yes'))) 'Detailed server command leaked into compact output.'
}
Invoke-Case 'Details are opt-in and existing framework/MCP are not offered for duplicate installation' {
    $root = New-Project 'display-existing' ('{"dependencies":{"com.liss.ssframework":"file:../framework","' + $ankleId + '":"' + $ankleUrl + '"}}')
    foreach ($details in @($false, $true)) {
        $messages = [Collections.Generic.List[string]]::new()
        function Write-Host { param($Object, $ForegroundColor) $messages.Add([string]$Object) }
        $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp AnkleBreaker -SkipOpenUPM -Details:$details -PassThru
        $text = $messages -join "`n"
        Assert ($result.FrameworkDeclared -and -not $result.ManifestChanged) 'Existing declarations were not recognized.'
        Assert (-not $text.Contains('Add package from git URL')) 'Existing framework offered for reinstallation.'
        Assert ($text.Contains($ankleUrl) -eq $details) 'MCP URL should only be included in optional source details for an existing package.'
        Assert ($text.Contains('npx.cmd --yes') -eq $details) 'Details flag does not control connection commands.'
    }
}
$setupTool = $tool
Invoke-Case 'Automatic framework and MCP are applied together with one exact backup' {
    $root = New-Project 'framework-auto'
    $before = Manifest-Bytes $root
    $result = & $setupTool -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Apply -PassThru 6>$null
    $manifest = Read-Manifest $root
    Assert ($result.Applied -and $result.AddedFrameworkPackage -and $result.AddedMcpPackage) 'Combined automatic installation failed.'
    Assert ($manifest.dependencies.'com.liss.ssframework' -ceq $result.FrameworkGitUrl) 'Framework did not use the reviewed revision.'
    Assert ($manifest.dependencies.$ankleId -ceq $ankleUrl) 'Selected MCP was not added.'
    Assert ((File-Bytes $result.BackupPath) -ceq $before) 'Combined backup is not exact.'
    $after = Manifest-Bytes $root
    $repeat = & $setupTool -ProjectPath $root -UnityMcp AnkleBreaker -McpInstallMode Manifest -Apply -PassThru 6>$null
    Assert (-not $repeat.Applied -and (Manifest-Bytes $root) -ceq $after) 'Repeat rewrote installed packages.'
    Assert (@(Get-ChildItem -LiteralPath (Join-Path $root 'UserSettings/SSFrameworkSetup') -File).Count -eq 1) 'Repeat created a duplicate backup.'
}
Invoke-Case 'Existing framework version and embedded framework are preserved' {
    $root = New-Project 'framework-existing' '{"dependencies":{"com.liss.ssframework":"file:../custom-framework"}}'
    $result = & $setupTool -ProjectPath $root -Apply -PassThru 6>$null
    Assert (-not $result.AddedFrameworkPackage -and (Read-Manifest $root).dependencies.'com.liss.ssframework' -ceq 'file:../custom-framework') 'Existing framework was repinned.'
    $root = New-Project 'framework-embedded'
    $embedded = Join-Path $root 'Packages/arbitrary-framework'
    [IO.Directory]::CreateDirectory($embedded) | Out-Null
    Write-Json (Join-Path $embedded 'package.json') '{"name":"com.liss.ssframework","version":"0.1.1"}'
    $result = & $setupTool -ProjectPath $root -Apply -PassThru 6>$null
    Assert ($result.FrameworkPresent -and -not $result.AddedFrameworkPackage) 'Embedded framework was not recognized.'
    Assert ($null -eq (Read-Manifest $root).dependencies.PSObject.Properties['com.liss.ssframework']) 'Embedded framework was duplicated in manifest.'
}
Invoke-Case 'Automatic framework requires compatible Unity and configured sources' {
    foreach ($incompatible in @($true, $false)) {
        $root = New-Project ([Guid]::NewGuid().ToString('N'))
        if ($incompatible) { Write-Json (Join-Path $root 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 6000.6.0f1' }
        $before = Manifest-Bytes $root
        $failure = ''
        try { & $setupTool -ProjectPath $root -SkipOpenUPM -Apply 6>$null } catch { $failure = $_.Exception.Message }
        Assert ($failure -match 'Unity 6.3|OpenUPM scopes') 'Missing compatibility/source guard.'
        Assert ((Manifest-Bytes $root) -ceq $before) 'Rejected framework setup changed manifest.'
    }
}
Invoke-Case 'Recommended framework choice is automatic; apply remains an explicit choice' {
    $root = New-Project 'framework-default'
    $answers = [Collections.Generic.Queue[string]]::new()
    foreach ($answer in @('', '', 'n')) { $answers.Enqueue($answer) }
    function Read-Host { param($Prompt) return $answers.Dequeue() }
    $result = & $setupTool -ProjectPath $root -Interactive -PassThru 6>$null
    Assert ($result.FrameworkInstallMode -eq 'Manifest' -and $result.AddedFrameworkPackage) 'Recommended framework default is wrong.'
    Assert (-not $result.Applied -and $answers.Count -eq 0) 'Default selection silently applied or used unexpected prompts.'
}
Invoke-Case 'Network preflight retries once and permits successful metadata checks' {
    $root = New-Project 'network-retry'
    $attempts = @{}
    function Invoke-WebRequest {
        param($Uri, [switch]$UseBasicParsing, $TimeoutSec)
        if (-not $attempts.ContainsKey($Uri)) { $attempts[$Uri] = 0 }
        $attempts[$Uri]++
        if ($Uri -match 'package.openupm.com' -and $attempts[$Uri] -eq 1) { throw 'Simulated connection reset.' }
        $name = if ($Uri -match 'package.openupm.com') { 'com.cysharp.r3' } else { 'com.liss.ssframework' }
        return [pscustomobject]@{ StatusCode = 200; Content = ('{"name":"' + $name + '"}') }
    }
    $result = & $setupTool -ProjectPath $root -CheckNetwork -Apply -PassThru 6>$null
    Assert ($result.Applied -and -not $result.NetworkBlocked) 'Recovered network request blocked apply.'
    Assert ($result.NetworkChecks.Count -eq 2 -and $result.NetworkChecks[0].Attempts -eq 2) 'Expected bounded retry and both required hosts.'
}
Invoke-Case 'Failed network or wrong metadata preserves manifest and creates no backup' {
    foreach ($badMetadata in @($false, $true)) {
        $root = New-Project ([Guid]::NewGuid().ToString('N'))
        $before = Manifest-Bytes $root
        function Invoke-WebRequest {
            param($Uri, [switch]$UseBasicParsing, $TimeoutSec)
            if ($badMetadata) { return [pscustomobject]@{ StatusCode = 200; Content = '{"name":"wrong-package"}' } }
            throw 'Simulated network failure.'
        }
        $result = & $setupTool -ProjectPath $root -CheckNetwork -Apply -PassThru 6>$null
        Assert ($result.NetworkBlocked -and -not $result.Applied) 'Failed preflight applied changes.'
        Assert ((Manifest-Bytes $root) -ceq $before) 'Failed preflight changed manifest.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root 'UserSettings'))) 'Failed preflight created backups.'
    }
}
if ($ConsumerProject) {
    Invoke-Case 'Real consumer: exact copies preserve installed providers and reject incompatible choices' {
        $before = Manifest-Bytes $ConsumerProject
        $consumerLock = Join-Path $ConsumerProject 'Packages/packages-lock.json'
        $lockBefore = File-Bytes $consumerLock
        $consumerVersion = Join-Path $ConsumerProject 'ProjectSettings/ProjectVersion.txt'
        $versionBefore = File-Bytes $consumerVersion
        foreach ($provider in @('AnkleBreaker', 'Coplay')) {
            $preview = & $tool -FrameworkInstallMode Manual -ProjectPath $ConsumerProject -UnityMcp $provider -McpInstallMode Manual -PassThru 6>$null
            Assert (-not $preview.Applied) 'Consumer preview applied changes.'
            $root = New-Project "consumer-$provider"
            foreach ($relative in @('Packages/manifest.json', 'Packages/packages-lock.json', 'ProjectSettings/ProjectVersion.txt')) {
                [IO.File]::WriteAllBytes((Join-Path $root $relative), [IO.File]::ReadAllBytes((Join-Path $ConsumerProject $relative)))
            }
            $existing = (Read-Manifest $root).dependencies.PSObject.Properties[$preview.Mcp.PackageId]
            $hasConflict = @($preview.DetectedMcp | Where-Object { $_.PackageId -cne $preview.Mcp.PackageId -or $_.Source -eq 'embedded' }).Count -gt 0
            $hasConflict = $hasConflict -or ($null -ne $existing -and $existing.Value -cne $preview.Mcp.GitUrl)
            $hasConflict = $hasConflict -or ($null -eq $existing -and @($preview.DetectedMcp | Where-Object { $_.Source -eq 'lock' }).Count -gt 0)
            if ($hasConflict) {
                $failure = ''
                try { & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp $provider -McpInstallMode Manifest -Apply 6>$null } catch { $failure = $_.Exception.Message }
                Assert ($failure -like '*MCP*') 'Expected consumer provider/source conflict.'
                Assert ((Manifest-Bytes $root) -ceq $before) 'Conflict changed the exact consumer copy.'
                continue
            }
            $result = & $tool -FrameworkInstallMode Manual -ProjectPath $root -UnityMcp $provider -McpInstallMode Manifest -Apply -PassThru 6>$null
            Assert ($result.Applied -eq $result.ManifestChanged) 'Consumer copy plan and application disagree.'
            Assert ($result.AddedMcpPackage -eq ($null -eq $existing)) 'Consumer MCP declaration handling is wrong.'
            $updated = Read-Manifest $root
            foreach ($dependency in (Read-Manifest $ConsumerProject).dependencies.PSObject.Properties) {
                Assert ($updated.dependencies.PSObject.Properties[$dependency.Name].Value -ceq $dependency.Value) 'Consumer dependency changed.'
            }
            if ($result.Applied) { Assert ((File-Bytes $result.BackupPath) -ceq $before) 'Consumer backup differs.' }
            else { Assert ((Manifest-Bytes $root) -ceq $before) 'No-op changed the consumer copy.' }
        }
        Assert ((Manifest-Bytes $ConsumerProject) -ceq $before) 'Actual consumer manifest changed.'
        Assert ((File-Bytes $consumerLock) -ceq $lockBefore) 'Actual consumer lock changed.'
        Assert ((File-Bytes $consumerVersion) -ceq $versionBefore) 'Actual consumer version changed.'
        $root = New-Project 'consumer-framework'
        foreach ($relative in @('Packages/manifest.json', 'Packages/packages-lock.json', 'ProjectSettings/ProjectVersion.txt')) {
            [IO.File]::WriteAllBytes((Join-Path $root $relative), [IO.File]::ReadAllBytes((Join-Path $ConsumerProject $relative)))
        }
        $frameResult = & $setupTool -ProjectPath $root -Apply -PassThru 6>$null
        Assert ($frameResult.FrameworkPresent -or $null -ne (Read-Manifest $root).dependencies.PSObject.Properties['com.liss.ssframework']) 'Consumer copy has no framework after automatic setup.'
        foreach ($dependency in (Read-Manifest $ConsumerProject).dependencies.PSObject.Properties) {
            Assert ((Read-Manifest $root).dependencies.PSObject.Properties[$dependency.Name].Value -ceq $dependency.Value) 'Framework setup changed existing consumer dependency.'
        }
        Assert ((Manifest-Bytes $ConsumerProject) -ceq $before) 'Framework copy validation changed real consumer.'
    }
}
Invoke-Case 'Compiler preview, explicit skip and repeat preserve existing project state' {
    $root = New-Project 'compiler-new'
    $rsp = Join-Path $root 'Assets/csc.rsp'
    & $tool -ProjectPath $root -WhatIf -Apply 6>$null
    Assert (-not (Test-Path -LiteralPath $rsp)) 'WhatIf created compiler configuration.'
    & $tool -ProjectPath $root -SkipCompilerConfiguration -Apply 6>$null
    Assert (-not (Test-Path -LiteralPath $rsp)) 'Explicit skip created compiler configuration.'
    $manifestBefore = Manifest-Bytes $root
    $result = & $tool -ProjectPath $root -Apply -PassThru 6>$null
    Assert ($result.Applied -and $result.CompilerChanged -and -not $result.ManifestChanged) 'Compiler-only application is wrong.'
    Assert ((Manifest-Bytes $root) -ceq $manifestBefore) 'Compiler-only application changed manifest.'
    Assert ([IO.File]::ReadAllText($rsp).Trim() -ceq '-langversion:10.0') 'Default language is wrong.'
    $repeat = & $tool -ProjectPath $root -Apply -PassThru 6>$null
    Assert (-not $repeat.Applied -and -not $repeat.CompilerChanged) 'Compiler repeat is not a no-op.'
}
Invoke-Case 'Compiler merge preserves BOM, flags and local asmdef options' {
    $root = New-Project 'compiler-merge'
    $rsp = Join-Path $root 'Assets/csc.rsp'
    [IO.File]::WriteAllText($rsp, "-define:KEEP_ME`r`n-langversion:9.0`r`n-nowarn:0649`r`n", [Text.UTF8Encoding]::new($true))
    $before = File-Bytes $rsp
    $local = Join-Path $root 'Assets/Game'
    [IO.Directory]::CreateDirectory($local) | Out-Null
    Write-Json (Join-Path $local 'Game.asmdef') '{"name":"Game"}'
    Write-Json (Join-Path $local 'csc.rsp') '-unsafe'
    $result = & $tool -ProjectPath $root -Apply -PassThru 6>$null
    Assert ($result.CompilerFiles.Count -eq 2) 'Local response file was missed.'
    Assert ([IO.File]::ReadAllText($rsp) -ceq "-define:KEEP_ME`r`n-langversion:10.0`r`n-nowarn:0649`r`n") 'Compiler merge changed unrelated flags.'
    Assert ([IO.File]::ReadAllBytes($rsp)[0] -eq 239) 'Compiler merge lost UTF-8 BOM.'
    $rootFile = @($result.CompilerFiles | Where-Object { $_.Path -eq $rsp })[0]
    Assert ((File-Bytes $rootFile.BackupPath) -ceq $before) 'Compiler backup is not exact.'
    Assert ([IO.File]::ReadAllText((Join-Path $local 'csc.rsp')) -match '-unsafe\r?\n-langversion:10.0') 'Local flags or language setting missing.'
}
Invoke-Case 'Conflicting compiler options reject before any manifest or compiler write' {
    $root = New-Project 'compiler-conflict'
    $rsp = Join-Path $root 'Assets/csc.rsp'
    Write-Json $rsp "-langversion:9.0`n-langversion:10.0"
    $manifestBefore = Manifest-Bytes $root
    $rspBefore = File-Bytes $rsp
    $failure = ''
    try { & $tool -ProjectPath $root -Apply 6>$null } catch { $failure = $_.Exception.Message }
    Assert ($failure -like '*unambiguous*') 'Ambiguous language was not rejected.'
    Assert ((Manifest-Bytes $root) -ceq $manifestBefore -and (File-Bytes $rsp) -ceq $rspBefore) 'Rejected compiler plan modified files.'
}
Invoke-Case 'A failed manifest replacement rolls back new and existing compiler files' {
    foreach ($existing in @($false, $true)) {
        $root = New-Project "compiler-rollback-$existing"
        $rsp = Join-Path $root 'Assets/csc.rsp'
        if ($existing) { Write-Json $rsp "-langversion:9.0`n-define:KEEP" }
        $rspBefore = if ($existing) { File-Bytes $rsp } else { '' }
        $manifestBefore = Manifest-Bytes $root
        $handle = [IO.File]::Open((Join-Path $root 'Packages/manifest.json'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $failure = ''
        try { & $tool -ProjectPath $root -Apply 6>$null } catch { $failure = $_.Exception.Message }
        finally { $handle.Dispose() }
        Assert ($failure.Length -gt 0) 'Locked manifest should reject replacement.'
        Assert ((Manifest-Bytes $root) -ceq $manifestBefore) 'Failed replacement changed manifest.'
        if ($existing) { Assert ((File-Bytes $rsp) -ceq $rspBefore) 'Rollback did not restore original compiler bytes.' }
        else { Assert (-not (Test-Path -LiteralPath $rsp)) 'Rollback left newly created compiler configuration.' }
    }
}
Write-Host "$script:passed test groups passed. Fixtures: $runRoot"
