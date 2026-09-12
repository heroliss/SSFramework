#requires -Version 5.1
<#
.SYNOPSIS
Previews SSFramework Git installation, package sources and optional Unity MCP.
.DESCRIPTION
Runs outside Unity. Framework Manifest mode is recommended; existing framework
versions are preserved. Manifest mode also configures C# 10 response files.
-Apply backs up existing files and rolls back completed writes on failure.
-CheckNetwork probes metadata; Unity performs package downloads.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ProjectPath,
    [ValidateSet('Manifest', 'Manual', 'Skip')]
    [string] $FrameworkInstallMode = 'Manifest',
    [ValidateSet('None', 'AnkleBreaker', 'Coplay')]
    [string] $UnityMcp = 'None',
    [ValidateSet('Manual', 'Manifest')]
    [string] $McpInstallMode = 'Manual',
    [switch] $SkipOpenUPM,
    [switch] $SkipCompilerConfiguration,
    [switch] $Apply,
    [switch] $Interactive,
    [switch] $Details,
    [switch] $CheckNetwork,
    [switch] $SkipNetworkCheck,
    [switch] $PassThru
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-SetupSection([string] $Title) {
    Write-Host ''
    Write-Host $Title -ForegroundColor Cyan
    Write-Host ('-' * 56) -ForegroundColor DarkGray
}
function Test-JsonObject($Value) {
    return $null -ne $Value -and $Value -is [Management.Automation.PSCustomObject]
}
function Read-JsonBytes([byte[]] $Bytes) {
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes).TrimStart([char]0xFEFF)
    return ConvertFrom-Json -InputObject $text
}
function New-CompilerPlan([string] $Root) {
    $assetRoot = Join-Path $Root 'Assets'
    $paths = @((Join-Path $assetRoot 'csc.rsp'))
    foreach ($asmdef in Get-ChildItem -LiteralPath $assetRoot -Filter '*.asmdef' -Recurse -File) {
        $localPath = Join-Path $asmdef.DirectoryName 'csc.rsp'
        if ([IO.File]::Exists($localPath)) { $paths += $localPath }
    }
    foreach ($path in $paths | Select-Object -Unique) {
        if ((Test-Path -LiteralPath $path) -and -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Compiler response file path is not a file: $path"
        }
        $exists = [IO.File]::Exists($path)
        if ($exists -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Compiler response file is a link; review its target before configuring it: $path"
        }
        [byte[]]$bytes = @()
        if ($exists) { $bytes = [IO.File]::ReadAllBytes($path) }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes).TrimStart([char]0xFEFF)
        $pattern = '(?im)^[ \t]*[-/]langversion[ \t]*:[ \t]*(?<value>[^\s#]+)[ \t]*(?:#[^\r\n]*)?\r?$'
        $languageMatches = [regex]::Matches($text, $pattern)
        $uncommented = [regex]::Replace($text, '(?m)^[ \t]*#[^\r\n]*', '')
        $mentions = [regex]::Matches($uncommented, '(?i)[-/]langversion\s*:')
        if ($languageMatches.Count -gt 1 -or $mentions.Count -ne $languageMatches.Count) {
            throw "Use one unambiguous -langversion option on its own line in $path"
        }
        $previous = if ($languageMatches.Count -eq 1) { $languageMatches[0].Groups['value'].Value.Trim('"') } else { '' }
        $nextText = $text
        $action = '保留已有版本'
        if ($previous -eq '') {
            $separator = if ($text.Length -eq 0 -or $text.EndsWith("`n")) { '' } else { [Environment]::NewLine }
            $nextText = $text + $separator + '-langversion:10.0' + [Environment]::NewLine
            $action = if ($exists) { '补充 C# 10.0' } else { '创建 C# 10.0 配置' }
        } elseif ($previous -match '^\d+(\.\d+)?$' -and [double]::Parse($previous, [Globalization.CultureInfo]::InvariantCulture) -lt 10) {
            $valueGroup = $languageMatches[0].Groups['value']
            $nextText = $text.Remove($valueGroup.Index, $valueGroup.Length).Insert($valueGroup.Index, '10.0')
            $action = "C# $previous → 10.0"
        } elseif ($previous -notin @('10', '10.0')) {
            $action = "保留自定义版本 $previous（本框架仅验证 10.0）"
        }
        $changed = $nextText -cne $text
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191
        $encoding = [Text.UTF8Encoding]::new($bom)
        $updated = [byte[]]($encoding.GetPreamble() + $encoding.GetBytes($nextText))
        [pscustomobject]@{
            Path = $path; Kind = 'compiler'; Exists = $exists; OriginalBytes = [byte[]]$bytes
            UpdatedBytes = $updated; Changed = $changed; Action = $action; BackupPath = $null
        }
    }
}
function Save-SetupFiles([object[]] $Files, [string] $BackupDirectory) {
    foreach ($file in $Files) {
        $exists = [IO.File]::Exists($file.Path)
        if ($exists -ne $file.Exists -or ($exists -and
            [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.Path)) -cne [Convert]::ToBase64String($file.OriginalBytes))) {
            throw "Configuration changed after preview. Run the tool again: $($file.Path)"
        }
    }
    [IO.Directory]::CreateDirectory($BackupDirectory) | Out-Null
    $written = [Collections.Generic.List[object]]::new()
    try {
        foreach ($file in $Files) {
            $id = [Guid]::NewGuid().ToString('N')
            $temporary = $file.Path + ".ssframework-$id.tmp"
            try {
                [IO.File]::WriteAllBytes($temporary, $file.UpdatedBytes)
                if ($file.Exists) {
                    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($file.Path)) -cne [Convert]::ToBase64String($file.OriginalBytes)) {
                        throw "Configuration changed after preview. Run the tool again: $($file.Path)"
                    }
                    $extension = [IO.Path]::GetExtension($file.Path)
                    $file.BackupPath = Join-Path $BackupDirectory "$($file.Kind)-$id$extension"
                    [IO.File]::Replace($temporary, $file.Path, $file.BackupPath)
                } else { [IO.File]::Move($temporary, $file.Path) }
                $written.Add($file)
            } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        }
    } catch {
        $failure = $_
        for ($index = $written.Count - 1; $index -ge 0; $index--) {
            $file = $written[$index]
            if (-not [IO.File]::Exists($file.Path) -or
                [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.Path)) -cne [Convert]::ToBase64String($file.UpdatedBytes)) {
                throw "Configuration write failed and another process changed $($file.Path). Review backups in $BackupDirectory. Original error: $failure"
            }
            if ($file.Exists) { [IO.File]::WriteAllBytes($file.Path, $file.OriginalBytes) }
            else { [IO.File]::Delete($file.Path) }
        }
        throw $failure
    }
}
function Test-SetupEndpoint([string] $Name, [string] $Url, [string] $PackageName) {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 8
            $data = ConvertFrom-Json -InputObject $response.Content
            if ($response.StatusCode -ne 200 -or $data.name -cne $PackageName) { throw 'Unexpected package metadata response.' }
            return [pscustomobject]@{ Name = $Name; Success = $true; Attempts = $attempt }
        } catch {
            if ($attempt -eq 2) { return [pscustomobject]@{ Name = $Name; Success = $false; Attempts = $attempt } }
        }
    }
}
function Merge-OpenUPM($Manifest) {
    $registryUrl = 'https://package.openupm.com'
    $requiredScopes = @(
        'com.cysharp.unitask', 'com.cysharp.r3', 'com.code-philosophy.luban',
        'com.tuyoogame.yooasset', 'com.code-philosophy.hybridclr', 'org.nuget'
    )
    $registries = @()
    if ($Manifest.PSObject.Properties['scopedRegistries']) {
        if ($Manifest.scopedRegistries -isnot [Array]) { throw 'scopedRegistries must be an array.' }
        $registries = @($Manifest.scopedRegistries)
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
        if ($registry.url.TrimEnd('/') -ieq $registryUrl) {
            if ($null -ne $openUpm) { throw 'Multiple OpenUPM registries found; consolidate them first.' }
            $openUpm = $registry
            continue
        }
        if ($registry.name -ieq 'OpenUPM') {
            throw 'A registry named OpenUPM uses a different URL. Review its source in Package Manager first.'
        }
        foreach ($scope in $registry.scopes) {
            if ($requiredScopes -ccontains $scope -or $scope.StartsWith('org.nuget.', [StringComparison]::Ordinal)) {
                throw "Scope '$scope' belongs to registry '$($registry.name)'. Resolve this source conflict first."
            }
        }
    }
    $createRegistry = $null -eq $openUpm
    if ($createRegistry) { $openUpm = [pscustomobject]@{ name = 'OpenUPM'; url = $registryUrl; scopes = @() } }
    $addedScopes = @($requiredScopes | Where-Object {
        $required = $_
        @($openUpm.scopes | Where-Object {
            $required -ceq $_ -or $required.StartsWith($_ + '.', [StringComparison]::Ordinal)
        }).Count -eq 0
    })
    if ($addedScopes.Count -gt 0) {
        $openUpm.scopes = @($openUpm.scopes) + $addedScopes
        if ($createRegistry) { $registries += $openUpm }
        $Manifest | Add-Member -MemberType NoteProperty -Name scopedRegistries -Value @($registries) -Force
    }
    return ,$addedScopes
}

# Official package manifests, npm/PyPI releases and licenses checked 2026-09-10.
# These are installation candidates; Editor/Player acceptance is project-specific.
$mcpProviders = @(
    [pscustomobject]@{
        Id = 'AnkleBreaker'; PackageId = 'com.anklebreaker.unity-mcp'; PluginVersion = '2.39.5'
        GitUrl = 'https://github.com/AnkleBreaker-Studio/unity-mcp-plugin.git#v2.39.5'
        MetadataUrl = 'https://raw.githubusercontent.com/AnkleBreaker-Studio/unity-mcp-plugin/v2.39.5/package.json'
        ServerVersion = '2.35.6'; Requirements = 'Node.js 18 或以上（含 npm/npx）、Git'
        RequiredCommands = @('node', 'npx', 'git')
        ServerCommand = 'npx.cmd --yes --package=anklebreaker-unity-mcp@2.35.6 unity-mcp'
        ServerEnvironment = @{ UNITY_MCP_COMPACT_TOOLS = '1' }
        EditorMenu = 'Window > MCP Dashboard'
        License = 'AnkleBreaker 自定义许可，包含署名与再分发条件。'
        LicenseUrl = 'https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/blob/v2.39.5/LICENSE'
        DocsUrl = 'https://github.com/AnkleBreaker-Studio/unity-mcp-server/blob/v2.35.6/README.md'
    },
    [pscustomobject]@{
        Id = 'Coplay'; PackageId = 'com.coplaydev.unity-mcp'; PluginVersion = '10.2.0'
        GitUrl = 'https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.2.0'
        MetadataUrl = 'https://raw.githubusercontent.com/CoplayDev/unity-mcp/v10.2.0/MCPForUnity/package.json'
        ServerVersion = '10.2.0'; Requirements = 'Python 3.10 或以上、uv/uvx、Git'
        RequiredCommands = @('uv', 'uvx', 'git')
        ServerCommand = 'uvx --from mcpforunityserver==10.2.0 mcp-for-unity --transport stdio'
        ServerEnvironment = @{}
        EditorMenu = 'Window > MCP for Unity'
        License = 'MIT；再分发时保留版权与许可声明。'
        LicenseUrl = 'https://github.com/CoplayDev/unity-mcp/blob/v10.2.0/LICENSE'
        DocsUrl = 'https://github.com/CoplayDev/unity-mcp/blob/v10.2.0/website/docs/getting-started/install.md'
    }
)

# Keep this reviewed installation candidate in sync with consuming-framework.md.
$frameworkGitUrl = 'https://github.com/heroliss/SSFramework.git#57062df9e56b6f562bc4e4a868367370fc657229'
Write-Host ''
Write-Host 'SSFramework 接入助手' -ForegroundColor Cyan
Write-Host '先运行本工具准备安装，再打开 Unity；Unity 会按清单下载框架与依赖。'
if ($Interactive) { Write-SetupSection '[1/4] 选择工程与可选工具' }

if ([string]::IsNullOrWhiteSpace($ProjectPath) -and $Interactive) {
    $ProjectPath = (Read-Host 'Unity 工程根目录（粘贴路径；留空取消）').Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($ProjectPath)) { Write-Host '已取消，没有修改文件。'; return }
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) { throw 'Provide -ProjectPath pointing to the Unity project root.' }
$projectRoot = (Resolve-Path -LiteralPath $ProjectPath).ProviderPath
foreach ($relative in @('Assets', 'Packages', 'ProjectSettings')) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Container)) {
        throw "Not a Unity project root: missing $relative."
    }
}
$versionPath = Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt'
if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw 'Missing ProjectSettings/ProjectVersion.txt.' }
$manifestPath = Join-Path $projectRoot 'Packages/manifest.json'
$originalBytes = [IO.File]::ReadAllBytes($manifestPath)
$manifest = Read-JsonBytes $originalBytes
if (-not (Test-JsonObject $manifest) -or -not $manifest.PSObject.Properties['dependencies'] -or
    -not (Test-JsonObject $manifest.dependencies)) { throw 'manifest.json must contain a dependencies object.' }
$frameworkDeclared = $null -ne $manifest.dependencies.PSObject.Properties['com.liss.ssframework']
$frameworkPresent = $frameworkDeclared
$versionLine = Get-Content -LiteralPath $versionPath | Where-Object { $_ -match '^m_EditorVersion:' } | Select-Object -First 1

if ($Interactive -and -not $PSBoundParameters.ContainsKey('FrameworkInstallMode')) {
    Write-Host ''
    Write-Host 'SSFramework：提供 UI、资源、配置与生命周期等基础能力。'
    Write-Host '  1  自动加入清单（推荐；已有版本会保留）'
    Write-Host '  2  手动安装：只显示 Git 地址'
    Write-Host '  0  跳过框架，仅配置其他选项'
    switch ((Read-Host '选择 [1/2/0]，回车使用推荐项').Trim()) {
        '' { $FrameworkInstallMode = 'Manifest' }; '1' { $FrameworkInstallMode = 'Manifest' }
        '2' { $FrameworkInstallMode = 'Manual' }; '0' { $FrameworkInstallMode = 'Skip' }
        default { throw 'Invalid framework selection. Use 1, 2 or 0; nothing was changed.' }
    }
}

if ($Interactive -and -not $PSBoundParameters.ContainsKey('UnityMcp')) {
    $existingProviders = @($mcpProviders | Where-Object { $null -ne $manifest.dependencies.PSObject.Properties[$_.PackageId] })
    $recommendedMcp = if ($existingProviders.Count -eq 1) { $existingProviders[0].Id } else { 'None' }
    Write-Host ''
    Write-Host 'Unity MCP（可选）'
    Write-Host '让 AI 读取场景、操作编辑器和获取测试结果；游戏运行不需要它。'
    Write-Host '  0  跳过，保留已有包'
    Write-Host '  1  AnkleBreaker'
    Write-Host '  2  Coplay'
    $recommendation = if ($recommendedMcp -eq 'None') { '跳过；需要 AI 编辑器操作时再选择' } else { "保留工程已有的 $recommendedMcp" }
    Write-Host "  推荐：$recommendation"
    switch ((Read-Host '选择 [0/1/2]，回车使用推荐项').Trim()) {
        '' { $UnityMcp = $recommendedMcp }; '0' { $UnityMcp = 'None' }
        '1' { $UnityMcp = 'AnkleBreaker' }; '2' { $UnityMcp = 'Coplay' }
        default { throw 'Invalid MCP selection. Use 0, 1 or 2; nothing was changed.' }
    }
}
if ($UnityMcp -ne 'None' -and $Interactive -and -not $PSBoundParameters.ContainsKey('McpInstallMode')) {
    Write-Host ''
    Write-Host '所选 MCP 的安装方式'
    Write-Host '  1  手动安装：显示 Git 地址和步骤'
    Write-Host '  2  加入清单：Unity 启动后自动下载插件（推荐）'
    switch ((Read-Host '选择 [1/2]，回车使用推荐项').Trim()) {
        '' { $McpInstallMode = 'Manifest' }; '1' { $McpInstallMode = 'Manual' }
        '2' { $McpInstallMode = 'Manifest' }
        default { throw 'Invalid install mode. Use 1 or 2; nothing was changed.' }
    }
}

$selectedMcp = $mcpProviders | Where-Object { $_.Id -eq $UnityMcp } | Select-Object -First 1
$installedMcp = @()
$knownIds = @($mcpProviders.PackageId)
foreach ($dependency in $manifest.dependencies.PSObject.Properties) {
    if ($knownIds -ccontains $dependency.Name) {
        $installedMcp += [pscustomobject]@{ PackageId = $dependency.Name; Source = 'manifest'; Version = $dependency.Value }
    }
}
# Locked transitive packages and embedded packages may not appear in dependencies.
if ($null -ne $selectedMcp -or $FrameworkInstallMode -ne 'Skip') {
    $lockPath = Join-Path $projectRoot 'Packages/packages-lock.json'
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        $packageLock = Read-JsonBytes ([IO.File]::ReadAllBytes($lockPath))
        if (-not (Test-JsonObject $packageLock)) { throw 'packages-lock.json must contain an object.' }
        if ($packageLock.PSObject.Properties['dependencies']) {
            if (-not (Test-JsonObject $packageLock.dependencies)) { throw 'packages-lock.json dependencies must be an object.' }
            foreach ($dependency in $packageLock.dependencies.PSObject.Properties) {
                if ($dependency.Name -ceq 'com.liss.ssframework') { $frameworkPresent = $true }
                if ($knownIds -ccontains $dependency.Name) {
                    $installedMcp += [pscustomobject]@{ PackageId = $dependency.Name; Source = 'lock'; Version = $null }
                }
            }
        }
    }
    foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Packages') -Directory) {
        $embeddedPath = Join-Path $directory.FullName 'package.json'
        if (-not (Test-Path -LiteralPath $embeddedPath -PathType Leaf)) { continue }
        $embedded = Read-JsonBytes ([IO.File]::ReadAllBytes($embeddedPath))
        if ((Test-JsonObject $embedded) -and $embedded.PSObject.Properties['name'] -and $embedded.name -ceq 'com.liss.ssframework') { $frameworkPresent = $true }
        if ((Test-JsonObject $embedded) -and $embedded.PSObject.Properties['name'] -and $knownIds -ccontains $embedded.name) {
            $installedMcp += [pscustomobject]@{ PackageId = $embedded.name; Source = 'embedded'; Version = $null }
        }
    }
}

$addedPackage = $false
if ($null -ne $selectedMcp -and $McpInstallMode -eq 'Manifest') {
    $conflicts = @($installedMcp | Where-Object { $_.PackageId -cne $selectedMcp.PackageId })
    if ($conflicts.Count -gt 0) {
        $conflictNames = ($conflicts | ForEach-Object { "$($_.PackageId) [$($_.Source)]" }) -join ', '
        throw "Another Unity MCP provider is present: $conflictNames. Resolve it in Package Manager first; no provider is removed automatically."
    }
    if (@($installedMcp | Where-Object { $_.Source -eq 'embedded' }).Count -gt 0) {
        throw 'The selected MCP is embedded. Review its source in Package Manager first; no source is replaced automatically.'
    }
    $existing = $manifest.dependencies.PSObject.Properties[$selectedMcp.PackageId]
    if ($null -ne $existing -and $existing.Value -cne $selectedMcp.GitUrl) {
        throw 'The selected MCP already has a different version or source. Review it in Package Manager first; no version is replaced automatically.'
    }
    if ($null -eq $existing) {
        if (@($installedMcp | Where-Object { $_.Source -eq 'lock' }).Count -gt 0) {
            throw 'The selected MCP is already resolved without a direct manifest entry. Review its dependency owner in Package Manager first.'
        }
        $manifest.dependencies | Add-Member -MemberType NoteProperty -Name $selectedMcp.PackageId -Value $selectedMcp.GitUrl
        $addedPackage = $true
    }
}
$addedFramework = $FrameworkInstallMode -eq 'Manifest' -and -not $frameworkPresent
if ($addedFramework) {
    if ($versionLine -notmatch '^m_EditorVersion:\s*6000\.3\.') { throw 'Automatic framework installation currently requires Unity 6.3 LTS. Use Manual mode to evaluate other versions.' }
    if ($SkipOpenUPM) {
        $sourceProbe = Read-JsonBytes $originalBytes
        $missingSourceScopes = Merge-OpenUPM $sourceProbe
        if ($missingSourceScopes.Count -gt 0) {
            throw 'SSFramework requires OpenUPM scopes. Remove -SkipOpenUPM or configure the sources first.'
        }
    }
    $manifest.dependencies | Add-Member -MemberType NoteProperty -Name 'com.liss.ssframework' -Value $frameworkGitUrl
}
$addedScopes = @()
if (-not $SkipOpenUPM) { $addedScopes = Merge-OpenUPM $manifest }
$manifestChanged = $addedScopes.Count -gt 0 -or $addedPackage -or $addedFramework
$compilerFiles = @()
if ($FrameworkInstallMode -eq 'Manifest' -and -not $SkipCompilerConfiguration) { $compilerFiles = @(New-CompilerPlan $projectRoot) }
$compilerChanges = @($compilerFiles | Where-Object Changed)
$changed = $manifestChanged -or $compilerChanges.Count -gt 0
$plan = [pscustomobject]@{
    ProjectPath = $projectRoot; UnityVersion = $versionLine
    ConfigureOpenUPM = -not $SkipOpenUPM; AddedScopes = @($addedScopes)
    UnityMcp = $UnityMcp; McpInstallMode = $McpInstallMode; Mcp = $selectedMcp
    DetectedMcp = @($installedMcp); AddedMcpPackage = $addedPackage
    ManifestChanged = $manifestChanged; Applied = $false; BackupPath = $null
    CompilerChanged = $compilerChanges.Count -gt 0; CompilerFiles = @($compilerFiles | Select-Object Path,Action,Changed,BackupPath)
    FrameworkDeclared = $frameworkDeclared; FrameworkGitUrl = $frameworkGitUrl
    FrameworkPresent = $frameworkPresent; FrameworkInstallMode = $FrameworkInstallMode; AddedFrameworkPackage = $addedFramework
    NetworkChecks = @(); NetworkBlocked = $false
}
$versionDisplay = $versionLine -replace '^m_EditorVersion:\s*', ''
$selectedAlreadyPresent = $null -ne $selectedMcp -and @($installedMcp | Where-Object { $_.PackageId -ceq $selectedMcp.PackageId }).Count -gt 0
Write-SetupSection '[2/4] 变更预览'
Write-Host "  工程：$projectRoot"
Write-Host "  Unity：$versionDisplay"
if ($frameworkPresent) { Write-Host '  SSFramework：已声明、解析或嵌入，保留现有来源与版本。' }
elseif ($addedFramework) { Write-Host '  SSFramework：将固定提交 57062df 加入清单，Unity 启动后自动下载。' }
elseif ($FrameworkInstallMode -eq 'Manual') { Write-Host '  SSFramework：仅提供手动安装地址。' }
else { Write-Host '  SSFramework：本次跳过。' }
if ($addedFramework) { Write-Host '  框架依赖：含 YooAsset 等整包依赖，目前一并安装。' }
if ($SkipOpenUPM) { Write-Host '  包源：本次跳过。' }
elseif ($addedScopes.Count -eq 0) { Write-Host '  包源：OpenUPM 已配置，无需修改。' }
else { Write-Host "  包源：OpenUPM，待补齐 $($addedScopes.Count) 项 Scope。" }
if ($addedScopes.Count -gt 0) { Write-Host '  作用：Scope 告诉 Unity 哪些依赖去 OpenUPM 下载，无需逐个装包。' }
if (-not $SkipOpenUPM -and $versionLine -notmatch '6000\.3\.') {
    Write-Host '  提醒：完整框架当前以 Unity 6.3 LTS 为目标，请先核对兼容性。' -ForegroundColor Yellow
}
if ($null -ne $selectedMcp) {
    Write-Host "  MCP：$UnityMcp，候选插件版本 $($selectedMcp.PluginVersion)。"
    if ($addedPackage) { Write-Host '  操作：将 MCP 加入清单；打开 Unity 后由 UPM 下载。' }
    elseif ($selectedAlreadyPresent) { Write-Host '  操作：保留已有 MCP；本工具不重复添加或升级。' }
    else { Write-Host '  操作：仅提供 MCP 手动安装指引。' }
    Write-Host "  许可：$($selectedMcp.License)"
    $missingCommands = @($selectedMcp.RequiredCommands | Where-Object {
        $null -eq (Get-Command $_ -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
    })
    if ($missingCommands.Count -gt 0) {
        Write-Host "  连接前需准备：$($missingCommands -join '、')（PATH 未找到）。" -ForegroundColor Yellow
    }
    if (@($installedMcp | Where-Object { $_.PackageId -cne $selectedMcp.PackageId }).Count -gt 0) {
        Write-Host '  提醒：工程已有其他 MCP 提供方，请先核对是否需要切换。' -ForegroundColor Yellow
    }
} else { Write-Host '  MCP：本次跳过，已有包保持原状。' }
if ($changed) {
    if ($manifestChanged) { Write-Host '  写入：Packages/manifest.json（合并所选依赖与包源；JSON 排版可能变化）' }
    foreach ($file in $compilerFiles) { Write-Host "  编译配置：$($file.Path)；$($file.Action)" }
    if ($compilerChanges.Count -gt 0) { Write-Host '  作用：让业务代码使用 record struct 与日志插值处理器；保留其他编译参数。' }
    Write-Host '  保护：已有文件逐份备份；写入失败时回滚已完成的修改。'
} else { Write-Host '  写入：无；重复运行不会创建新备份。' }
if (($addedFramework -or $addedPackage) -and $null -eq (Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)) {
    throw 'Git is required for automatic Git package installation. Install Git or select Manual mode.'
}
if ($CheckNetwork -and -not $SkipNetworkCheck -and $manifestChanged) {
    Write-Host '  联网预检：正在检查包元数据（失败会重试一次）……'
    if (-not $SkipOpenUPM) { $plan.NetworkChecks += Test-SetupEndpoint 'OpenUPM' 'https://package.openupm.com/com.cysharp.r3' 'com.cysharp.r3' }
    if ($addedFramework) { $plan.NetworkChecks += Test-SetupEndpoint 'SSFramework / GitHub' 'https://raw.githubusercontent.com/heroliss/SSFramework/57062df9e56b6f562bc4e4a868367370fc657229/package.json' 'com.liss.ssframework' }
    if ($addedPackage) { $plan.NetworkChecks += Test-SetupEndpoint "$UnityMcp / GitHub" $selectedMcp.MetadataUrl $selectedMcp.PackageId }
    foreach ($check in $plan.NetworkChecks) {
        $statusText = if ($check.Success) { '可访问' } else { '检查失败' }
        Write-Host "    $($check.Name)：$statusText"
    }
    $plan.NetworkBlocked = @($plan.NetworkChecks | Where-Object { -not $_.Success }).Count -gt 0
} elseif ($manifestChanged) { Write-Host '  联网预检：未执行；可添加 -CheckNetwork 检查包源。' -ForegroundColor DarkGray }
elseif ($compilerChanges.Count -gt 0) { Write-Host '  联网预检：本次只调整本地编译配置，无需联网。' -ForegroundColor DarkGray }
if ($Details) {
    Write-Host ''
    Write-Host '配置明细' -ForegroundColor DarkCyan
    if (-not $SkipOpenUPM) {
        Write-Host '  Registry：https://package.openupm.com'
        foreach ($scope in $addedScopes) { Write-Host "  新增 Scope：$scope" }
    }
    if ($null -ne $selectedMcp) {
        Write-Host "  候选包：$($selectedMcp.PackageId)"
        Write-Host "  来源：$($selectedMcp.GitUrl)"
        Write-Host "  环境要求：$($selectedMcp.Requirements)"
        foreach ($commandName in $selectedMcp.RequiredCommands) {
            $available = $null -ne (Get-Command $commandName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
            $status = if ($available) { '已找到，版本仍需核对' } else { '未找到' }
            Write-Host "    ${commandName}：$status"
        }
        foreach ($detected in $installedMcp) { Write-Host "  检测到：$($detected.PackageId) [$($detected.Source)]" }
        Write-Host "  许可原文：$($selectedMcp.LicenseUrl)"
    }
    if ($addedFramework) { Write-Host "  框架来源：$frameworkGitUrl" }
}
if ($changed -and -not $plan.NetworkBlocked) {
    Write-Host ''
    if ($Interactive -and -not $Apply -and -not $WhatIfPreference) {
        $Apply = (Read-Host '请先关闭该 Unity 工程。是否应用以上配置？[y/N]').Trim() -ieq 'y'
    }
    if ($Apply -and $PSCmdlet.ShouldProcess($projectRoot, '应用框架、包源、所选 MCP 与预览中的编译配置，备份已有文件')) {
        $unityLockPath = Join-Path $projectRoot 'Temp/UnityLockfile'
        if (Test-Path -LiteralPath $unityLockPath) {
            try {
                $lockProbe = [IO.File]::Open($unityLockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                $lockProbe.Dispose()
            } catch { throw 'The Unity project is open or its lock is inaccessible. Close the Editor and retry.' }
        }
        $backupDirectory = Join-Path $projectRoot 'UserSettings/SSFrameworkSetup'
        $filesToWrite = @($compilerChanges)
        $manifestFile = $null
        if ($manifestChanged) {
            $updatedText = ($manifest | ConvertTo-Json -Depth 100) + [Environment]::NewLine
            $manifestFile = [pscustomobject]@{
                Path = $manifestPath; Kind = 'manifest'; Exists = $true; OriginalBytes = $originalBytes
                UpdatedBytes = [Text.UTF8Encoding]::new($false).GetBytes($updatedText); BackupPath = $null
            }
            $filesToWrite += $manifestFile
        }
        Save-SetupFiles $filesToWrite $backupDirectory
        $plan.Applied = $true
        if ($null -ne $manifestFile) { $plan.BackupPath = $manifestFile.BackupPath }
        $plan.CompilerFiles = @($compilerFiles | Select-Object Path,Action,Changed,BackupPath)
    }
}
Write-SetupSection '[3/4] 执行结果'
if ($plan.NetworkBlocked) {
    Write-Host '  暂停：包源预检失败，清单保持原状。' -ForegroundColor Yellow
    Write-Host '  请检查网络或代理后重试；本工具不会更改系统网络设置。'
} elseif ($plan.Applied) {
    Write-Host '  已完成：预览中的工程配置已保存。' -ForegroundColor Green
    if ($addedScopes.Count -gt 0) { Write-Host "    已补齐 $($addedScopes.Count) 项包源 Scope。" }
    if ($addedFramework) { Write-Host '    已添加 SSFramework Git 依赖。' }
    if ($addedPackage) { Write-Host "    已添加 $UnityMcp Git 依赖。" }
    if ($plan.BackupPath) { Write-Host "  原清单备份：$($plan.BackupPath)" }
    foreach ($file in $plan.CompilerFiles | Where-Object Changed) {
        Write-Host "    已配置：$($file.Path)；$($file.Action)"
        if ($file.BackupPath) { Write-Host "    原文件备份：$($file.BackupPath)" }
    }
} elseif ($changed) { Write-Host '  仅预览：尚未写入任何配置。' -ForegroundColor Yellow }
else { Write-Host '  无需修改：所选配置已存在，或本次只查看指引。' -ForegroundColor Green }

Write-SetupSection '[4/4] 下一步'
if ($FrameworkInstallMode -eq 'Manual' -or $SkipCompilerConfiguration) {
    Write-Host '  手动配置 C#：业务程序集需使用 C# 10；在 Assets/csc.rsp 或相应 asmdef 同目录的 csc.rsp 中设置 -langversion:10.0。'
}
if ($changed -and -not $plan.Applied) {
    Write-Host '  先重新运行并确认应用，或添加 -Apply 参数；配置生效后：'
}
Write-Host '  1. 打开 Unity，等待 Package Manager 完成解析和编译。'
if ($frameworkPresent) {
    Write-Host '  2. SSFramework 已在工程中，在 Package Manager 核对来源和解析结果。'
} elseif ($FrameworkInstallMode -eq 'Manifest') {
    Write-Host '  2. SSFramework 将由 UPM 按清单安装，无需手动添加 Git 地址。'
    Write-Host '     当前固定到待验收提交，完整 Unity 6.3 验收仍需完成。' -ForegroundColor DarkGray
} elseif ($FrameworkInstallMode -eq 'Manual') {
    Write-Host '  2. 在 Package Manager 中选择 Add package from git URL，添加 SSFramework：'
    Write-Host "     $frameworkGitUrl"
    Write-Host '     此地址为当前待验收提交，完整 Unity 6.3 验收仍需完成。' -ForegroundColor DarkGray
} else {
    Write-Host '  2. 本次跳过框架，继续核对已选择的其他配置。'
}
if ($null -ne $selectedMcp) {
    if ($selectedAlreadyPresent) { Write-Host '  3. 所选 MCP 已存在，请在 Package Manager 核对版本和来源。' }
    elseif ($McpInstallMode -eq 'Manifest') { Write-Host '  3. MCP 将由 UPM 按清单安装，无需再次粘贴它的 Git 地址。' }
    else {
        Write-Host '  3. 手动添加 MCP 的 Git 地址：'
        Write-Host "     $($selectedMcp.GitUrl)"
    }
    Write-Host "     打开 $($selectedMcp.EditorMenu) 配置连接。"
    Write-Host "     与候选插件配套的服务端版本：$($selectedMcp.ServerVersion)。"
    if ($Details) {
        Write-Host ''
        Write-Host 'MCP 连接详情（供手动配置，本工具不执行）' -ForegroundColor DarkCyan
        Write-Host "  Windows 服务端命令：$($selectedMcp.ServerCommand)"
        foreach ($key in $selectedMcp.ServerEnvironment.Keys) { Write-Host "  服务端环境变量：$key=$($selectedMcp.ServerEnvironment[$key])" }
        Write-Host "  提供方说明：$($selectedMcp.DocsUrl)"
        Write-Host '  首次启动服务端可能下载代码；配置格式按所用 AI 客户端核对。'
    }
}
Write-Host ''
Write-Host '提示：配置清单与完成安装是两步；是否成功以 Unity 的解析、编译和连接结果为准。' -ForegroundColor DarkGray
Write-Host '查看 Scope、来源与连接命令：运行脚本时添加 -Details。' -ForegroundColor DarkGray
Write-Host '安装说明：https://github.com/heroliss/SSFramework/blob/codex/package-installation/Tools~/README.md' -ForegroundColor DarkGray
if ($PassThru) { return $plan }
if ($plan.NetworkBlocked) { throw 'Network preflight failed; no manifest changes were applied.' }
