#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) 'SSFrameworkOnboardingTests'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'Setup-SSFramework.ps1'
$check = Join-Path $PSScriptRoot 'Check-SSFrameworkProject.ps1'
$runRoot = Join-Path $OutputDirectory ([Guid]::NewGuid().ToString('N'))
$passed = 0
$options = @{FrameworkInstallMode='Skip';SkipOpenUPM=$true;UnityMcp='None'}
function Assert($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Text([string]$Path,[string]$Text) { [IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false)) }
function Bytes([string]$Path) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) }
function Inspect([string]$Root) { & $check -ProjectPath $Root -Json | ConvertFrom-Json }
function Has-Check($Report,[string]$Id,[string]$Status) { return @($Report.Checks | Where-Object { $_.Id -eq $Id -and $_.Status -eq $Status }).Count -eq 1 }
function Case([string]$Name,[scriptblock]$Body) { & $Body; $script:passed++; Write-Host ('PASS '+$Name) }
function New-Fixture {
    $root = Join-Path $runRoot ([Guid]::NewGuid().ToString('N'))
    foreach ($folder in @('Assets','Packages','ProjectSettings')) { [IO.Directory]::CreateDirectory((Join-Path $root $folder)) | Out-Null }
    Write-Text (Join-Path $root 'ProjectSettings/ProjectVersion.txt') 'm_EditorVersion: 6000.3.23f1'
    Write-Text (Join-Path $root 'Assets/csc.rsp') '-langversion:10.0'
    Write-Text (Join-Path $root 'Packages/manifest.json') '{"dependencies":{"com.liss.ssframework":"https://github.com/heroliss/SSFramework.git#fixed"},"scopedRegistries":[{"name":"OpenUPM","url":"https://package.openupm.com","scopes":["com.cysharp"]}]}'
    Write-Text (Join-Path $root 'Packages/packages-lock.json') '{"dependencies":{"com.liss.ssframework":{"version":"https://github.com/heroliss/SSFramework.git#fixed","hash":"1234567890123456789012345678901234567890","source":"git","dependencies":{"com.cysharp.unitask":"2.5.11"}},"com.cysharp.unitask":{"version":"2.5.11","source":"registry","url":"https://package.openupm.com","dependencies":{}}}}'
    return $root
}
Case 'Read-only checks accept single-item registry arrays without claiming Unity validation' {
    $root = New-Fixture
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File)
    $before = @{}
    foreach ($file in $files) { $before[$file.FullName]=Bytes $file.FullName }
    $report = Inspect $root
    Assert ($report.Status -eq 'FilesChecked' -and $report.RequiresUnityVerification) 'File checks were incorrectly treated as Unity acceptance.'
    Assert (Has-Check $report 'registry-routing' 'Pass') 'Single Scope array did not roundtrip.'
    Assert (Has-Check $report 'unity-verification' 'Pending') 'Missing explicit Unity verification boundary.'
    foreach ($file in $files) { Assert ((Bytes $file.FullName) -ceq $before[$file.FullName]) 'Read-only check wrote a file.' }
    Assert (@(Get-ChildItem -LiteralPath $root -Recurse -File).Count -eq $files.Count) 'Read-only check created files.'
}
Case 'A missing lock source does not pass the resolution check' {
    $root=New-Fixture
    $path=Join-Path $root 'Packages/packages-lock.json'
    $data=Get-Content -Raw $path | ConvertFrom-Json
    $data.dependencies.'com.liss.ssframework'.PSObject.Properties.Remove('source')
    Write-Text $path ($data | ConvertTo-Json -Depth 10)
    Assert (Has-Check (Inspect $root) 'framework-choice' 'Warning') 'Incomplete lock source was accepted.'
    $data.dependencies.'com.cysharp.unitask'.PSObject.Properties.Remove('url')
    Write-Text $path ($data | ConvertTo-Json -Depth 10)
    Assert (Has-Check (Inspect $root) 'registry-routing' 'Warning') 'Missing registry URL was accepted.'
}
Case 'Changes during inspection invalidate the snapshot' {
    $root=New-Fixture
    $versionPath=Join-Path $root 'ProjectSettings/ProjectVersion.txt'
    function Get-ChildItem {
        param($LiteralPath)
        Write-Text $versionPath 'm_EditorVersion: 6000.3.999f1'
        Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $LiteralPath
    }
    $report=Inspect $root
    Assert ($report.Status -eq 'Retry' -and -not $report.SnapshotStable) 'Concurrent project change was ignored.'
}
Case 'Incomplete dependency closure is visible even when a Framework lock exists' {
    $root = New-Fixture
    $path = Join-Path $root 'Packages/packages-lock.json'
    $data = Get-Content -Raw $path | ConvertFrom-Json
    $data.dependencies.PSObject.Properties.Remove('com.cysharp.unitask')
    Write-Text $path ($data | ConvertTo-Json -Depth 10)
    Assert (Has-Check (Inspect $root) 'dependency-locks' 'Warning') 'Missing transitive dependency was missed.'
}
Case 'A changed Git selection cannot pass using an old lock' {
    $root = New-Fixture
    $path = Join-Path $root 'Packages/manifest.json'
    Write-Text $path ((Get-Content -Raw $path).Replace('#fixed','#new-version'))
    Assert (Has-Check (Inspect $root) 'framework-choice' 'Warning') 'Stale Git resolution was accepted.'
}
Case 'Scope changes are checked against the longest matching namespace' {
    $root = New-Fixture
    $path = Join-Path $root 'Packages/manifest.json'
    $data = Get-Content -Raw $path | ConvertFrom-Json
    $data.scopedRegistries += [pscustomobject]@{name='Another registry';url='https://example.invalid';scopes=@('com.cysharp.unitask')}
    Write-Text $path ($data | ConvertTo-Json -Depth 10)
    Assert (Has-Check (Inspect $root) 'registry-routing' 'Warning') 'More specific conflicting Scope was missed.'
}
Case 'Missing lock and malformed JSON give actionable results' {
    $root = New-Fixture
    [IO.File]::Delete((Join-Path $root 'Packages/packages-lock.json'))
    Assert (Has-Check (Inspect $root) 'framework' 'Warning') 'Missing lock was accepted.'
    Write-Text (Join-Path $root 'Packages/manifest.json') '{broken'
    Assert ((Inspect $root).ErrorCount -gt 0) 'Malformed manifest was accepted.'
}
Case 'A local response file shadows the valid root language setting' {
    $root = New-Fixture
    $folder = Join-Path $root 'Assets/Business'
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    Write-Text (Join-Path $folder 'Business.asmdef') '{"name":"Business","references":["Game.Framework"]}'
    Write-Text (Join-Path $folder 'csc.rsp') '-unsafe'
    $report = Inspect $root
    Assert (@($report.Checks | Where-Object { $_.Id -like 'csharp:Business*' -and $_.Status -eq 'Warning' }).Count -eq 1) 'Local response shadowing was missed.'
}
Case 'Empty responses, C# 9 and conflicting language flags require review' {
    foreach ($text in @('', '-langversion:9.0',"-langversion:10.0`n-langversion:9.0")) {
        $root = New-Fixture
        Write-Text (Join-Path $root 'Assets/csc.rsp') $text
        $report = Inspect $root
        Assert ($report.WarningCount -gt 0) 'Invalid business language setup was accepted.'
        Assert $report.SnapshotStable 'A stable empty file was incorrectly considered a concurrent write.'
    }
}
Case 'GUID references are explicitly left for Unity to resolve' {
    $root = New-Fixture
    Write-Text (Join-Path $root 'Assets/Business.asmdef') '{"name":"Business","references":["GUID:12345678901234567890123456789012ab"]}'
    Assert (Has-Check (Inspect $root) 'guid-references' 'Pending') 'GUID target was guessed.'
}
Case 'HybridCLR absent, enabled and disabled settings have distinct meanings' {
    foreach ($state in @('missing','0','1')) {
        $root = New-Fixture
        $path = Join-Path $root 'Packages/packages-lock.json'
        $data = Get-Content -Raw $path | ConvertFrom-Json
        $data.dependencies | Add-Member -NotePropertyName 'com.code-philosophy.hybridclr' -NotePropertyValue ([pscustomobject]@{version='8.14.1';source='registry';dependencies=[pscustomobject]@{}})
        Write-Text $path ($data | ConvertTo-Json -Depth 10)
        if ($state -ne 'missing') { Write-Text (Join-Path $root 'ProjectSettings/HybridCLRSettings.asset') "MonoBehaviour:`n  enable: $state`n" }
        $expected = if ($state -eq '0') { 'Pass' } else { 'Warning' }
        Assert (Has-Check (Inspect $root) 'hybridclr' $expected) ('Incorrect HybridCLR meaning: '+$state)
    }
}
Case 'Optional project files are skipped by noninteractive default' {
    $root = New-Fixture
    $result = & $setup @options -ProjectPath $root -Apply -PassThru 6>$null
    Assert (-not $result.Applied -and -not $result.ProjectFilesChanged) 'Default invocation wrote optional project rules.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'AGENTS.md'))) 'Default created AI instructions.'
}
Case 'Preview and WhatIf do not initialize project files' {
    foreach ($whatIf in @($false,$true)) {
        $root = New-Fixture
        $result = & $setup @options -ProjectPath $root -GitConfiguration CreateMissing -AiRules CreateMissing -Apply:$whatIf -WhatIf:$whatIf -PassThru 6>$null
        Assert ($result.ProjectFilesChanged -and -not $result.Applied) 'Preview wrote project files.'
        Assert (-not (Test-Path -LiteralPath (Join-Path $root '.gitignore'))) 'Preview created Git rules.'
    }
}
Case 'Explicit initialization creates exactly the selected templates and repeats without writes' {
    $root = New-Fixture
    $result = & $setup @options -ProjectPath $root -GitConfiguration CreateMissing -AiRules CreateMissing -Apply -PassThru 6>$null
    Assert ($result.Applied -and $result.ProjectFiles.Count -eq 3) 'Selected initialization did not run.'
    foreach ($name in @('.gitignore','.gitattributes','AGENTS.md')) {
        $template = switch ($name) { '.gitignore' { 'gitignore.template' }; '.gitattributes' { 'gitattributes.template' }; 'AGENTS.md' { 'AGENTS.md.template' } }
        Assert ((Bytes (Join-Path $root $name)) -ceq (Bytes (Join-Path $PSScriptRoot ('Templates/UnityProject/'+$template)))) 'Created rules differ from the reviewed template.'
    }
    Assert (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) 'Initializer created a Git repository.'
    $repeat = & $setup @options -ProjectPath $root -GitConfiguration CreateMissing -AiRules CreateMissing -Apply -PassThru 6>$null
    Assert (-not $repeat.Applied -and -not $repeat.ProjectFilesChanged) 'Repeated initialization wrote files.'
}
Case 'Existing custom rules stay byte-identical while missing choices are added' {
    $root = New-Fixture
    Write-Text (Join-Path $root '.gitignore') "# user rules`r`n/custom-output/"
    Write-Text (Join-Path $root 'AGENTS.md') '# Existing project instructions'
    $gitBefore=Bytes (Join-Path $root '.gitignore'); $aiBefore=Bytes (Join-Path $root 'AGENTS.md')
    $result = & $setup @options -ProjectPath $root -GitConfiguration CreateMissing -AiRules CreateMissing -Apply -PassThru 6>$null
    Assert ($result.Applied -and @(($result.ProjectFiles | Where-Object Changed)).Count -eq 1) 'Unexpected set of writes.'
    Assert ((Bytes (Join-Path $root '.gitignore')) -ceq $gitBefore -and (Bytes (Join-Path $root 'AGENTS.md')) -ceq $aiBefore) 'Existing rules were changed.'
}
Case 'A missing template distribution fails before writing anything' {
    $root = New-Fixture
    $partial = Join-Path $runRoot ([Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($partial) | Out-Null
    Copy-Item -LiteralPath $setup -Destination $partial
    $failed=$false
    try { & (Join-Path $partial 'Setup-SSFramework.ps1') @options -ProjectPath $root -GitConfiguration CreateMissing -Apply 6>$null } catch { $failed=$_.Exception.Message -like '*Missing template*' }
    Assert $failed 'Missing templates were silently ignored.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root '.gitignore'))) 'Partial initialization happened.'
    & (Join-Path $partial 'Setup-SSFramework.ps1') @options -ProjectPath $root -Apply 6>$null
}
Case 'Directory conflicts reject the whole initialization plan' {
    $root = New-Fixture
    [IO.Directory]::CreateDirectory((Join-Path $root '.gitattributes')) | Out-Null
    $failed=$false
    try { & $setup @options -ProjectPath $root -GitConfiguration CreateMissing -Apply 6>$null } catch { $failed=$_.Exception.Message -like '*destination is a directory*' }
    Assert $failed 'Directory conflict was not detected.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root '.gitignore'))) 'Earlier file was written before conflict validation.'
}
Case 'A user file created after preview is preserved and stops the transaction' {
    $root = New-Fixture
    $racePath = Join-Path $root '.gitignore'
    function Read-Host { param($Prompt) Write-Text $racePath '# User created this during preview'; return 'y' }
    $failed=$false
    try { & $setup @options -ProjectPath $root -GitConfiguration CreateMissing -AiRules CreateMissing -Interactive 6>$null } catch { $failed=$_.Exception.Message -like '*changed after preview*' }
    Assert $failed 'Concurrent file creation was not detected.'
    Assert ((Get-Content -Raw $racePath) -eq '# User created this during preview') 'Concurrent user file was overwritten.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $root 'AGENTS.md'))) 'Transaction partially wrote AI rules.'
}
Case 'A later manifest failure rolls back newly created project files' {
    $root = New-Fixture
    $manifestPath=Join-Path $root 'Packages/manifest.json'
    $before=Bytes $manifestPath
    $handle=[IO.File]::Open($manifestPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $failed=$false
    try { & $setup -ProjectPath $root -GitConfiguration CreateMissing -AiRules CreateMissing -Apply 6>$null } catch { $failed=$true } finally { $handle.Dispose() }
    Assert $failed 'Expected the locked manifest replacement to fail.'
    Assert ((Bytes $manifestPath) -ceq $before) 'Manifest failure changed original bytes.'
    foreach ($file in @('.gitignore','.gitattributes','AGENTS.md')) { Assert (-not (Test-Path -LiteralPath (Join-Path $root $file))) 'Rollback left new project rules behind.' }
}
Case 'Recommended optional choices still require an explicit apply' {
    $root=New-Fixture
    $answers=[Collections.Generic.Queue[string]]::new()
    foreach ($answer in @('','','n')) { $answers.Enqueue($answer) }
    function Read-Host { param($Prompt) return $answers.Dequeue() }
    $result=& $setup @options -ProjectPath $root -Interactive -PassThru 6>$null
    Assert ($result.GitConfiguration -eq 'CreateMissing' -and $result.AiRules -eq 'CreateMissing') 'Missing project files were not the recommended choice.'
    Assert (-not $result.Applied -and $answers.Count -eq 0) 'Recommendation silently applied or changed prompt count.'
}
Write-Host "$passed onboarding groups passed. Fixtures: $runRoot"
