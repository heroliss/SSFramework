#requires -Version 5.1
<#
.SYNOPSIS
Previews SSFramework package sources and optional Unity MCP installation.
.DESCRIPTION
Runs outside Unity. The default MCP mode prints manual installation steps.
Manifest mode adds only the selected MCP dependency. -Apply writes one atomic
manifest replacement with a byte-for-byte backup. No downloads or client edits.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ProjectPath,
    [ValidateSet('None', 'AnkleBreaker', 'Coplay')]
    [string] $UnityMcp = 'None',
    [ValidateSet('Manual', 'Manifest')]
    [string] $McpInstallMode = 'Manual',
    [switch] $SkipOpenUPM,
    [switch] $Apply,
    [switch] $Interactive,
    [switch] $PassThru
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-JsonObject($Value) {
    return $null -ne $Value -and $Value -is [Management.Automation.PSCustomObject]
}
function Read-JsonBytes([byte[]] $Bytes) {
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes).TrimStart([char]0xFEFF)
    return ConvertFrom-Json -InputObject $text
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
        ServerVersion = '2.35.6'; Requirements = 'Node.js >= 18 (including npm/npx); Git'
        RequiredCommands = @('node', 'npx', 'git')
        ServerCommand = 'npx.cmd --yes --package=anklebreaker-unity-mcp@2.35.6 unity-mcp'
        ServerEnvironment = @{ UNITY_MCP_COMPACT_TOOLS = '1' }
        EditorMenu = 'Window > MCP Dashboard'
        License = 'AnkleBreaker Open License v1.0: attribution and redistribution conditions apply.'
        LicenseUrl = 'https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/blob/v2.39.5/LICENSE'
        DocsUrl = 'https://github.com/AnkleBreaker-Studio/unity-mcp-server/blob/v2.35.6/README.md'
    },
    [pscustomobject]@{
        Id = 'Coplay'; PackageId = 'com.coplaydev.unity-mcp'; PluginVersion = '10.2.0'
        GitUrl = 'https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#v10.2.0'
        ServerVersion = '10.2.0'; Requirements = 'Python >= 3.10; uv/uvx; Git'
        RequiredCommands = @('uv', 'uvx', 'git')
        ServerCommand = 'uvx --from mcpforunityserver==10.2.0 mcp-for-unity --transport stdio'
        ServerEnvironment = @{}
        EditorMenu = 'Window > MCP for Unity'
        License = 'MIT: retain the copyright and license notice when redistributing.'
        LicenseUrl = 'https://github.com/CoplayDev/unity-mcp/blob/v10.2.0/LICENSE'
        DocsUrl = 'https://github.com/CoplayDev/unity-mcp/blob/v10.2.0/website/docs/getting-started/install.md'
    }
)

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

if ($Interactive -and -not $PSBoundParameters.ContainsKey('UnityMcp')) {
    Write-Host '可选 Unity MCP：0 跳过（保留已有包）；1 AnkleBreaker；2 Coplay'
    switch ((Read-Host '选择 [0/1/2]，直接回车跳过').Trim()) {
        '' { $UnityMcp = 'None' }; '0' { $UnityMcp = 'None' }
        '1' { $UnityMcp = 'AnkleBreaker' }; '2' { $UnityMcp = 'Coplay' }
        default { throw 'Invalid MCP selection. Use 0, 1 or 2; nothing was changed.' }
    }
}
if ($UnityMcp -ne 'None' -and $Interactive -and -not $PSBoundParameters.ContainsKey('McpInstallMode')) {
    Write-Host 'MCP 安装方式：1 手动安装指引；2 将所选包加入工程清单'
    switch ((Read-Host '选择 [1/2]，直接回车使用手动安装').Trim()) {
        '' { $McpInstallMode = 'Manual' }; '1' { $McpInstallMode = 'Manual' }
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
if ($null -ne $selectedMcp) {
    $lockPath = Join-Path $projectRoot 'Packages/packages-lock.json'
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        $packageLock = Read-JsonBytes ([IO.File]::ReadAllBytes($lockPath))
        if (-not (Test-JsonObject $packageLock)) { throw 'packages-lock.json must contain an object.' }
        if ($packageLock.PSObject.Properties['dependencies']) {
            if (-not (Test-JsonObject $packageLock.dependencies)) { throw 'packages-lock.json dependencies must be an object.' }
            foreach ($dependency in $packageLock.dependencies.PSObject.Properties) {
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
$addedScopes = @()
if (-not $SkipOpenUPM) { $addedScopes = Merge-OpenUPM $manifest }
$changed = $addedScopes.Count -gt 0 -or $addedPackage
$versionLine = Get-Content -LiteralPath $versionPath | Where-Object { $_ -match '^m_EditorVersion:' } | Select-Object -First 1
$plan = [pscustomobject]@{
    ProjectPath = $projectRoot; UnityVersion = $versionLine
    ConfigureOpenUPM = -not $SkipOpenUPM; AddedScopes = @($addedScopes)
    UnityMcp = $UnityMcp; McpInstallMode = $McpInstallMode; Mcp = $selectedMcp
    DetectedMcp = @($installedMcp); AddedMcpPackage = $addedPackage
    ManifestChanged = $changed; Applied = $false; BackupPath = $null
}
Write-Host "Project: $projectRoot"
Write-Host "$versionLine"
if (-not $SkipOpenUPM) {
    Write-Host 'Registry: https://package.openupm.com'
    if ($versionLine -notmatch '6000\.3\.') {
        Write-Warning 'The full SSFramework package currently targets Unity 6.3 LTS. Check compatibility before installation.'
    }
    foreach ($scope in $addedScopes) { Write-Host "  Add scope: $scope" }
}
if ($null -ne $selectedMcp) {
    Write-Host "MCP: $UnityMcp | Unity plugin $($selectedMcp.PluginVersion) | server $($selectedMcp.ServerVersion)"
    Write-Host "Requirements: $($selectedMcp.Requirements)"
    foreach ($commandName in $selectedMcp.RequiredCommands) {
        $available = $null -ne (Get-Command $commandName -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)
        $status = if ($available) { 'found on PATH; check its version' } else { 'not found on PATH; install it before connecting' }
        Write-Host "  ${commandName}: $status"
    }
    foreach ($detected in $installedMcp) { Write-Host "  Existing MCP: $($detected.PackageId) [$($detected.Source)]" }
    Write-Host "License: $($selectedMcp.License)"
    Write-Host $selectedMcp.LicenseUrl
    if ($addedPackage) { Write-Host "  Add dependency: $($selectedMcp.PackageId) = $($selectedMcp.GitUrl)" }
    Write-Host "MCP package mode: $McpInstallMode"
    Write-Host '手动接入 / 后续连接步骤：'
    if ($McpInstallMode -eq 'Manual') { Write-Host '  1. 在 Unity Package Manager 添加下方 Git URL；若已安装 MCP，先核对现有来源。' }
    else { Write-Host '  1. 应用清单后打开 Unity，等待 Package Manager 完成解析与编译。' }
    Write-Host "     $($selectedMcp.GitUrl)"
    Write-Host "  2. 打开 $($selectedMcp.EditorMenu)，检查插件状态与所选客户端的连接配置。"
    Write-Host "  3. 使用配套服务端版本 $($selectedMcp.ServerVersion)，按提供方说明配置客户端。"
    Write-Host "     Windows 服务端命令（stdio；本工具不会执行）：$($selectedMcp.ServerCommand)"
    foreach ($key in $selectedMcp.ServerEnvironment.Keys) { Write-Host "     服务端环境变量：$key=$($selectedMcp.ServerEnvironment[$key])" }
    Write-Host "     $($selectedMcp.DocsUrl)"
    Write-Host '  4. 在 AI 客户端读取工程路径、场景及 Console，确认目标，再验证编译后的重连与测试结果。'
    Write-Host '服务端命令首次运行可能下载第三方代码。客户端配置格式与启动方式以提供方和宿主说明为准。'
    Write-Host '仅检测了声明、锁文件和 embedded 包中的已知 MCP；Assets 导入的插件需在 Unity 中核对。'
    Write-Host '本工具不安装本机运行环境、不启动服务端，也不修改 AI 客户端配置。'
}
if ($changed) {
    Write-Host '待修改：Packages/manifest.json（上述 Scope 与所选 MCP；JSON 排版可能变化）。'
    Write-Host '原清单备份：UserSettings/SSFrameworkSetup/manifest-<unique-id>.json'
    Write-Host '其他依赖版本、packages-lock.json 和 ProjectSettings 保持原状。'
    if ($Interactive -and -not $Apply -and -not $WhatIfPreference) {
        $Apply = (Read-Host '请先关闭该 Unity 工程。是否应用以上配置？[y/N]').Trim() -ieq 'y'
    }
    if ($Apply -and $PSCmdlet.ShouldProcess($manifestPath, 'Apply package source/MCP plan with an atomic manifest backup')) {
        $unityLockPath = Join-Path $projectRoot 'Temp/UnityLockfile'
        if (Test-Path -LiteralPath $unityLockPath) {
            try {
                $lockProbe = [IO.File]::Open($unityLockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                $lockProbe.Dispose()
            } catch { throw 'The Unity project is open or its lock is inaccessible. Close the Editor and retry.' }
        }
        $backupDirectory = Join-Path $projectRoot 'UserSettings/SSFrameworkSetup'
        $operationId = [Guid]::NewGuid().ToString('N')
        $backupPath = Join-Path $backupDirectory "manifest-$operationId.json"
        $temporaryPath = Join-Path $projectRoot "Packages/ssframework-$operationId.tmp"
        try {
            $currentBytes = [IO.File]::ReadAllBytes($manifestPath)
            if ([Convert]::ToBase64String($currentBytes) -cne [Convert]::ToBase64String($originalBytes)) {
                throw 'manifest.json changed after preview. Run the tool again to review the new plan.'
            }
            $updatedText = ($manifest | ConvertTo-Json -Depth 100) + [Environment]::NewLine
            [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
            [IO.File]::WriteAllText($temporaryPath, $updatedText, [Text.UTF8Encoding]::new($false))
            $currentBytes = [IO.File]::ReadAllBytes($manifestPath)
            if ([Convert]::ToBase64String($currentBytes) -cne [Convert]::ToBase64String($originalBytes)) {
                throw 'manifest.json changed after preview. Run the tool again to review the new plan.'
            }
            [IO.File]::Replace($temporaryPath, $manifestPath, $backupPath)
        } finally {
            if ([IO.File]::Exists($temporaryPath)) { [IO.File]::Delete($temporaryPath) }
        }
        $plan.Applied = $true
        $plan.BackupPath = $backupPath
        Write-Host "Configured. Original manifest: $backupPath"
    } else { Write-Host 'Preview only. Run again with -Apply to write the configuration.' }
} else { Write-Host 'Already configured / no manifest changes selected. No files changed.' }
Write-Host 'SSFramework Git URL 仍由使用者在 Unity 中添加；使用已审查的固定 revision。'
Write-Host '清单配置完成不代表包安装、MCP 连接或 Unity 测试已经通过。'
if ($PassThru) { return $plan }
