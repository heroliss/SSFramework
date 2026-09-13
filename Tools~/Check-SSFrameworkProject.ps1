#requires -Version 5.1
<#
.SYNOPSIS
Read-only inspection of SSFramework consumer project files. Does not run Unity.
#>
[CmdletBinding()]
param([string]$ProjectPath, [switch]$Interactive, [switch]$Json, [switch]$PassThru, [switch]$FailOnError)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Interactive -and [string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = (Read-Host 'Unity 工程根目录（留空取消）').Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($ProjectPath)) { return }
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) { throw 'Provide -ProjectPath.' }
$root = (Resolve-Path -LiteralPath $ProjectPath).ProviderPath
$checks = [Collections.Generic.List[object]]::new()
$snapshots = @{}
function Add-Check([string]$Id, [string]$Status, [string]$Title, [string]$Evidence, [string]$Next) {
    $checks.Add([pscustomobject]@{Id=$Id;Status=$Status;Title=$Title;Evidence=$Evidence;Next=$Next})
}
function Property($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -ne $property) { return ,$property.Value }
    return $null
}
function Read-Text([string]$Path) {
    if (-not $snapshots.ContainsKey($Path)) {
        $exists = [IO.File]::Exists($Path)
        # Preserve zero-byte files: PowerShell otherwise unwraps an empty byte array to null.
        $bytes = if ($exists) { ,([IO.File]::ReadAllBytes($Path)) } else { $null }
        $snapshots[$Path] = [pscustomobject]@{Exists=$exists;Bytes=$bytes}
    }
    $snapshot = $snapshots[$Path]
    if (-not $snapshot.Exists) { return $null }
    return [Text.UTF8Encoding]::new($false,$true).GetString($snapshot.Bytes).TrimStart([char]0xFEFF)
}
function Read-Object([string]$Relative) {
    try {
        $text = Read-Text (Join-Path $root $Relative)
        if ($null -eq $text) { Add-Check $Relative 'Warning' $Relative '文件尚不存在。' '先让 Unity 完成保存或 UPM 解析，再检查。'; return $null }
        $value = ConvertFrom-Json -InputObject $text
        if ($value -isnot [Management.Automation.PSCustomObject]) { throw 'Expected a JSON object.' }
        return $value
    } catch {
        Add-Check $Relative 'Error' $Relative '无法读取有效的 JSON 对象。' '检查文件是否损坏或正在写入；不要用手改锁文件来伪造安装完成。'
        return $null
    }
}
function Get-AssetAsmdefs {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push((Join-Path $root 'Assets'))
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop()) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            elseif ($item.Extension -eq '.asmdef') { $item }
        }
    }
}
function Check-Language([string]$Path, [string]$Label) {
    try {
        $text = Read-Text $Path
        if ($null -eq $text) { Add-Check ('csharp:'+ $Label) 'Warning' $Label '没有生效的响应文件；Unity 6.3 默认 C# 9。' '运行接入工具的编译配置，或在相应响应文件中加入 -langversion:10.0。'; return }
        $active = [regex]::Replace($text,'(?m)^[ \t]*#[^\r\n]*','')
        $matches = [regex]::Matches($active,'(?im)^[ \t]*[-/]langversion[ \t]*:[ \t]*(?<value>[^\s#]+)[ \t]*(?:#[^\r\n]*)?\r?$')
        $mentions = [regex]::Matches($active,'(?i)[-/]langversion\s*:')
        if ($matches.Count -ne 1 -or $mentions.Count -ne 1) { Add-Check ('csharp:'+ $Label) 'Warning' $Label '缺少唯一、可识别的语言版本选项。' '每个响应文件只保留一个独立的 -langversion:10.0；保留其他编译参数。'; return }
        $version = $matches[0].Groups['value'].Value.Trim('"')
        if ($version -in @('10','10.0')) { Add-Check ('csharp:'+ $Label) 'Pass' $Label '响应文件声明 C# 10.0。' '' }
        else { Add-Check ('csharp:'+ $Label) 'Warning' $Label ('声明为 '+$version+'；框架接入基线为 10.0。') '核对业务 record struct 与日志插值处理器是否按预期编译；不自动降级自定义版本。' }
    } catch { Add-Check ('csharp:'+ $Label) 'Error' $Label '响应文件无法读取为 UTF-8 文本。' '修复文件编码或权限后重新检查。' }
}

foreach ($directory in @('Assets','Packages','ProjectSettings')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $directory) -PathType Container)) { throw "Not a Unity project root: missing $directory." }
}
$versionText = Read-Text (Join-Path $root 'ProjectSettings/ProjectVersion.txt')
$versionMatch = [regex]::Match([string]$versionText,'(?m)^m_EditorVersion:\s*(\S+)')
if ($versionMatch.Success -and $versionMatch.Groups[1].Value -match '^6000\.3\.') { Add-Check 'unity' 'Pass' 'Unity 版本' $versionMatch.Groups[1].Value '' }
else { Add-Check 'unity' 'Warning' 'Unity 版本' '未检测到已验证的 Unity 6.3 基线。' '用 Unity Hub 核对工程版本，其他版本先进行兼容性验证。' }

$manifest = Read-Object 'Packages/manifest.json'
$lock = Read-Object 'Packages/packages-lock.json'
$declared = Property $manifest 'dependencies'
$resolved = Property $lock 'dependencies'
if ($null -ne $manifest -and $declared -isnot [Management.Automation.PSCustomObject]) { Add-Check 'manifest-dependencies' 'Error' '工程依赖声明' 'dependencies 不是 JSON 对象。' '修复 manifest.json 的结构。' }
if ($null -ne $lock -and $resolved -isnot [Management.Automation.PSCustomObject]) { Add-Check 'lock-dependencies' 'Error' 'UPM 锁文件' 'dependencies 不是 JSON 对象。' '通过 Unity Package Manager 重新解析依赖。' }
$frameworkId = 'com.liss.ssframework'
$choice = Property $declared $frameworkId
$framework = Property $resolved $frameworkId
if ($null -eq $framework) {
    $message = if ($null -ne $choice) { '已声明 Framework，但锁文件尚无解析结果。' } else { '清单与锁文件中均未找到 Framework。' }
    Add-Check 'framework' 'Warning' 'Framework 安装记录' $message '按接入指南配置包源并安装 Framework，在 Unity 完成解析；embedded / 本地包也需核对实际来源。'
} else {
    $lockedVersion = Property $framework 'version'
    $source = Property $framework 'source'
    if ([string]::IsNullOrWhiteSpace([string]$source) -or [string]::IsNullOrWhiteSpace([string]$lockedVersion)) { Add-Check 'framework-choice' 'Warning' 'Framework 来源' '锁记录缺少来源或版本。' '通过 Package Manager 完成解析后再检查。' }
    elseif ($null -ne $choice -and $choice -isnot [string]) { Add-Check 'framework-choice' 'Error' 'Framework 来源' '工程清单中的包版本不是字符串。' '在 Package Manager 重新选择来源。' }
    elseif ($source -eq 'git' -and $null -ne $choice -and $choice -cne $lockedVersion) { Add-Check 'framework-choice' 'Warning' 'Framework 来源' 'Git 清单选择与锁文件记录不同，可能尚未完成升级。' '等待 Package Manager 解析，核对来源及实际提交。' }
    elseif ($source -eq 'git' -and [string](Property $framework 'hash') -notmatch '^[0-9a-f]{40}$') { Add-Check 'framework-choice' 'Warning' 'Framework 来源' 'Git 锁记录缺少完整提交 SHA。' '在 Unity 中重新解析，保留真实锁定结果。' }
    else { Add-Check 'framework-choice' 'Pass' 'Framework 锁定记录' ('已记录来源 '+[string]$source+'；这不证明包下载或脚本编译完成。') '' }

    # Traverse the declared dependency closure; resolved versions may legitimately be higher.
    $queue = [Collections.Generic.Queue[string]]::new()
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $queue.Enqueue($frameworkId)
    $missing = [Collections.Generic.List[string]]::new()
    while ($queue.Count -gt 0) {
        $id = $queue.Dequeue()
        if (-not $visited.Add($id)) { continue }
        $entry = Property $resolved $id
        if ($null -eq $entry -or [string]::IsNullOrWhiteSpace([string](Property $entry 'version'))) { $missing.Add($id); continue }
        $dependencies = Property $entry 'dependencies'
        if ($null -ne $dependencies -and $dependencies -isnot [Management.Automation.PSCustomObject]) { $missing.Add($id+' (invalid dependencies)'); continue }
        if ($null -ne $dependencies) { foreach ($dependency in $dependencies.PSObject.Properties) { $queue.Enqueue($dependency.Name) } }
    }
    if ($missing.Count) { Add-Check 'dependency-locks' 'Warning' '依赖锁定记录' ('缺少有效记录：'+($missing -join '、')) '先处理 Package Manager 的解析错误，等待依赖完整安装。' }
    else { Add-Check 'dependency-locks' 'Pass' '依赖锁定记录' ('Framework 及其 '+($visited.Count-1)+' 项依赖均有版本记录；未检查缓存或编译结果。') '' }

    $registries = Property $manifest 'scopedRegistries'
    $routes = [Collections.Generic.List[object]]::new()
    $invalidRegistry = $null -ne $registries -and $registries -isnot [Array]
    foreach ($registry in @($registries)) {
        if ($null -eq $registry) { continue }
        $url = Property $registry 'url'
        $scopes = Property $registry 'scopes'
        if ($url -isnot [string] -or $scopes -isnot [Array]) { $invalidRegistry=$true; continue }
        foreach ($scope in $scopes) {
            if ($scope -isnot [string] -or [string]::IsNullOrWhiteSpace($scope)) { $invalidRegistry=$true; continue }
            $routes.Add([pscustomobject]@{Scope=$scope;Url=$url.TrimEnd('/')})
        }
    }
    if ($invalidRegistry) { Add-Check 'registry-format' 'Error' 'Scoped Registry 格式' '包源列表或 Scope 格式无效。' '在 Project Settings 的 Package Manager 中修正包源配置。' }
    $wrongRoutes = [Collections.Generic.List[string]]::new()
    foreach ($id in $visited) {
        $entry = Property $resolved $id
        $expectedUrl = [string](Property $entry 'url')
        if ((Property $entry 'source') -ne 'registry') { continue }
        if ([string]::IsNullOrWhiteSpace($expectedUrl)) { $wrongRoutes.Add($id); continue }
        $matching = @($routes | Where-Object { $id -ceq $_.Scope -or $id.StartsWith($_.Scope+'.',[StringComparison]::Ordinal) } | Sort-Object { $_.Scope.Length } -Descending)
        $selected = @()
        if ($matching.Count) { $selected = @($matching | Where-Object { $_.Scope.Length -eq $matching[0].Scope.Length } | Select-Object -ExpandProperty Url -Unique) }
        elseif ($id.StartsWith('com.unity.',[StringComparison]::Ordinal)) { $selected=@('https://packages.unity.com') }
        if ($selected.Count -ne 1 -or $selected[0] -ine $expectedUrl.TrimEnd('/')) { $wrongRoutes.Add($id) }
    }
    if ($wrongRoutes.Count) { Add-Check 'registry-routing' 'Warning' '依赖包源路由' ('当前 Scope 无法唯一匹配已锁定包源：'+($wrongRoutes -join '、')) '核对 OpenUPM 与更具体 Scope 的归属；旧锁文件不会替代当前包源配置。' }
    elseif (-not $invalidRegistry) { Add-Check 'registry-routing' 'Pass' '依赖包源路由' '已锁定 Registry 依赖的来源与当前 Scope 一致；未进行联网检查。' '' }
}

Check-Language (Join-Path $root 'Assets/csc.rsp') 'Assets/csc.rsp（业务默认）'
$businessAssemblies = 0
$guidAssemblies = 0
foreach ($asmdefFile in Get-AssetAsmdefs) {
    try {
        $asmdef = ConvertFrom-Json -InputObject (Read-Text $asmdefFile.FullName)
        $references = Property $asmdef 'references'
        if (@($references | Where-Object { $_ -is [string] -and $_ -like 'GUID:*' }).Count) { $guidAssemblies++ }
        if (@($references | Where-Object { $_ -is [string] -and ($_ -eq 'Game.Framework' -or $_ -like 'Game.Framework.*') }).Count -eq 0) { continue }
        $businessAssemblies++
        $localResponse = Join-Path $asmdefFile.DirectoryName 'csc.rsp'
        # A local response file shadows Assets/csc.rsp, even when it contains no language option.
        if (Test-Path -LiteralPath $localResponse) { Check-Language $localResponse ([string](Property $asmdef 'name')+' 的局部 csc.rsp') }
    } catch { Add-Check ('asmdef:'+ $asmdefFile.Name) 'Warning' '业务程序集声明' ('无法解析 '+$asmdefFile.Name) '在 Unity 核对 asmdef 的 JSON 与引用。' }
}
if ($businessAssemblies -eq 0) { Add-Check 'assemblies' 'Info' '业务程序集' '尚未发现按名称显式引用 Framework 的业务 asmdef。' '开发业务前按接入指南建立 asmdef 并引用所用模块；不会自动生成目录或程序集。' }
else { Add-Check 'assemblies' 'Info' '业务程序集' ('发现 '+$businessAssemblies+' 个显式引用 Framework 的 asmdef。') '这里只识别声明；实际程序集编译与引用有效性需在 Unity 核对。' }
if ($guidAssemblies) { Add-Check 'guid-references' 'Pending' 'GUID 程序集引用' ('另有 '+$guidAssemblies+' 个 asmdef 使用 GUID 引用，文件自检未解析其目标。') '在 Unity Inspector 中核对引用目标及对应局部 csc.rsp。' }

if ($null -ne (Property $resolved 'com.code-philosophy.hybridclr')) {
    $hybridText = Read-Text (Join-Path $root 'ProjectSettings/HybridCLRSettings.asset')
    $enabled = [regex]::Match([string]$hybridText,'(?m)^  enable:\s*([01])\s*$')
    if ($enabled.Success -and $enabled.Groups[1].Value -eq '0') { Add-Check 'hybridclr' 'Pass' 'HybridCLR 构建选择' '项目配置已关闭 Enable，采用普通 Player 构建。' '' }
    elseif ($enabled.Success) { Add-Check 'hybridclr' 'Warning' 'HybridCLR 构建选择' '项目已开启 Enable。' '普通构建请在 Unity 关闭 Enable；使用代码热更新则完成 Installer、程序集配置与 Generate 并单独验收。' }
    else { Add-Check 'hybridclr' 'Warning' 'HybridCLR 构建选择' '未找到可识别的项目 Enable 设置；不能据此判断热更新已关闭。' '在 Unity 的 HybridCLR Settings 显式选择；普通 Player 构建应关闭 Enable。' }
}
$buildText = Read-Text (Join-Path $root 'ProjectSettings/EditorBuildSettings.asset')
$scenes = [regex]::Matches([string]$buildText,'(?m)^  - enabled: 1\r?\n    path: (.+)\r?$')
Add-Check 'scenes' 'Pending' '场景与 Build Profile' ('全局场景列表中识别到 '+$scenes.Count+' 个启用场景；自定义 Build Profile 可能覆盖此列表。') '在 Unity 核对实际 Profile、场景引用和保存状态，运行最小场景并构建目标 Player。'
foreach ($file in @('.gitignore','.gitattributes','AGENTS.md')) {
    $present = Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf
    $evidence = if ($present) { '文件存在；未判断已有规则是否适合本项目。' } else { '可选文件尚不存在，不阻止 Framework 安装。' }
    Add-Check ('project:'+ $file) 'Info' $file $evidence '按需要在接入工具中选择仅创建缺失文件；已有规则自行维护。'
}
Add-Check 'unity-verification' 'Pending' 'Unity 编译与运行验收' '本工具只读项目文件，未运行 Unity、测试、网络请求或构建。' '在 Unity 检查 Console、签名及包解析；验证场景输入、字体、生命周期和目标 Player。'

$stable = $true
foreach ($path in @($snapshots.Keys)) {
    $snapshot = $snapshots[$path]
    try {
        if ([IO.File]::Exists($path) -ne $snapshot.Exists -or ($snapshot.Exists -and [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($snapshot.Bytes))) { $stable=$false; break }
    } catch { $stable=$false; break }
}
if (-not $stable) { Add-Check 'snapshot' 'Warning' '文件检查快照' '检查期间文件发生变化，本轮结果不可作为稳定结论。' '等待 Unity / UPM 完成操作后重新检查。' }
$errorCount = @($checks | Where-Object Status -eq 'Error').Count
$warningCount = @($checks | Where-Object Status -eq 'Warning').Count
$status = if (-not $stable) { 'Retry' } elseif ($errorCount) { 'Blocked' } elseif ($warningCount) { 'Review' } else { 'FilesChecked' }
$report = [pscustomobject]@{ProjectPath=$root;Status=$status;SnapshotStable=$stable;ErrorCount=$errorCount;WarningCount=$warningCount;RequiresUnityVerification=$true;Checks=@($checks.ToArray())}
if ($Json) { $report | ConvertTo-Json -Depth 8 }
else {
    Write-Host ''
    Write-Host 'SSFramework 安装后配置自检（只读）' -ForegroundColor Cyan
    Write-Host "工程：$root"
    $labels = @{Pass='通过';Warning='需处理';Error='错误';Info='说明';Pending='待验证'}
    foreach ($check in $checks) {
        $color = if ($check.Status -eq 'Error') { 'Red' } elseif ($check.Status -eq 'Warning') { 'Yellow' } elseif ($check.Status -eq 'Pass') { 'Green' } else { 'Gray' }
        Write-Host ("[{0}] {1}：{2}" -f $labels[$check.Status],$check.Title,$check.Evidence) -ForegroundColor $color
        if ($check.Next) { Write-Host ('       下一步：'+$check.Next) }
    }
    Write-Host ''
    Write-Host "文件检查：$errorCount 项错误，$warningCount 项需处理；Unity 编译、运行与构建仍待验证。"
    if ($PassThru) { $report }
}
if ($FailOnError -and ($errorCount -gt 0 -or -not $stable)) { throw 'Project file inspection failed. Review the reported checks.' }
