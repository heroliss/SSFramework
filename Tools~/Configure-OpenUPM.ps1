#requires -Version 5.1
<#
.SYNOPSIS
Compatibility entry point: configure only SSFramework's OpenUPM scopes.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $ProjectPath,
    [switch] $Apply,
    [switch] $Interactive
)
& (Join-Path $PSScriptRoot 'Setup-SSFramework.ps1') @PSBoundParameters -UnityMcp None -FrameworkInstallMode Manual
