param(
    [string]$FolderPath = 'C:\Bilvask\SCV',
    [string]$ShareName = 'BilvaskSCV',
    [string]$ShareAccess = 'Authenticated Users',
    [string]$NtfsAccess = 'Authenticated Users',
    [switch]$EnableFirewall,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Write-Info([string]$msg) {
    Write-Host "[INFO] $msg"
}

function Ensure-Admin {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Uruchom ten skrypt jako Administrator.'
    }
}

function Ensure-Folder([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        Write-Info "Utworzono folder: $path"
    } else {
        Write-Info "Folder juz istnieje: $path"
    }
}

function Ensure-NtfsRights([string]$path, [string]$identity) {
    $acl = Get-Acl -LiteralPath $path
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $identity,
        'Modify',
        'ContainerInherit,ObjectInherit',
        'None',
        'Allow'
    )

    $exists = $false
    foreach ($r in $acl.Access) {
        if ($r.IdentityReference -like "*$identity" -and $r.FileSystemRights.ToString().Contains('Modify') -and $r.AccessControlType -eq 'Allow') {
            $exists = $true
            break
        }
    }

    if (-not $exists -or $Force) {
        $acl.SetAccessRule($rule)
        Set-Acl -LiteralPath $path -AclObject $acl
        Write-Info "Nadano NTFS Modify dla: $identity"
    } else {
        Write-Info "NTFS Modify juz istnieje dla: $identity"
    }
}

function Ensure-Share([string]$name, [string]$path, [string]$shareAccess) {
    $share = Get-SmbShare -Name $name -ErrorAction SilentlyContinue
    if ($null -eq $share) {
        New-SmbShare -Name $name -Path $path -ChangeAccess $shareAccess -FullAccess 'Administrators','SYSTEM' | Out-Null
        Write-Info "Utworzono udzial SMB: \\$env:COMPUTERNAME\\$name"
    }
    else {
        if ($share.Path -ne $path) {
            throw "Udostepnienie '$name' istnieje, ale wskazuje na inna sciezke: $($share.Path)"
        }
        Write-Info "Udostepnienie SMB juz istnieje: \\$env:COMPUTERNAME\\$name"
        Grant-SmbShareAccess -Name $name -AccountName $shareAccess -AccessRight Change -Force -ErrorAction SilentlyContinue | Out-Null
        Grant-SmbShareAccess -Name $name -AccountName 'Administrators' -AccessRight Full -Force -ErrorAction SilentlyContinue | Out-Null
        Grant-SmbShareAccess -Name $name -AccountName 'SYSTEM' -AccessRight Full -Force -ErrorAction SilentlyContinue | Out-Null
    }
}

function Ensure-Firewall {
    Enable-NetFirewallRule -DisplayGroup 'File and Printer Sharing' | Out-Null
    Write-Info 'Wlaczono reguly zapory dla File and Printer Sharing.'
}

Ensure-Admin
Ensure-Folder -path $FolderPath
Ensure-NtfsRights -path $FolderPath -identity $NtfsAccess
Ensure-Share -name $ShareName -path $FolderPath -shareAccess $ShareAccess
if ($EnableFirewall) { Ensure-Firewall }

$shareLocalPath = "\\localhost\\$ShareName"
$shareHostPath = "\\$env:COMPUTERNAME\\$ShareName"

Write-Host ''
Write-Host '===== PODSUMOWANIE ====='
Write-Host "Folder lokalny : $FolderPath"
Write-Host "UNC lokalny    : $shareLocalPath"
Write-Host "UNC hosta      : $shareHostPath"
Write-Host "Share access   : $ShareAccess"
Write-Host "NTFS access    : $NtfsAccess"
Write-Host ''
Write-Host 'Szybki test lokalny:'
Write-Host ("Test-Path {0} -> {1}" -f $shareLocalPath, (Test-Path -LiteralPath $shareLocalPath))
Write-Host ''
Write-Host 'Na Admin/Worker ustaw Dokumentmappe na:'
Write-Host $shareHostPath
