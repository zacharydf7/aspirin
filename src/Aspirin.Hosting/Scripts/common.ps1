# Functions shared by install.ps1 and rollback.ps1. Written for Windows PowerShell 5.1.

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "    $Message"
}

function Read-PackageManifest {
    param([string]$PackageRoot)

    $path = Join-Path $PackageRoot 'package.json'
    if (-not (Test-Path -LiteralPath $path)) {
        throw "package.json was not found in '$PackageRoot'."
    }

    $manifest = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) {
        throw "This script supports package schema version 1, but the package is version '$($manifest.schemaVersion)'."
    }

    return $manifest
}

# Fails unless the package holds exactly the files listed in checksums.sha256, unchanged.
function Assert-PackageIntegrity {
    param([string]$PackageRoot, [string[]]$IgnoredFiles = @())

    $checksumPath = Join-Path $PackageRoot 'checksums.sha256'
    if (-not (Test-Path -LiteralPath $checksumPath)) {
        throw 'checksums.sha256 is missing from the package.'
    }

    $expected = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath -Encoding UTF8) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
            throw "checksums.sha256 has a line that is not a checksum: '$line'."
        }
        $expected[$Matches[2]] = $Matches[1]
    }

    $root = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\')
    $problems = @()
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $relativePath = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($relativePath -eq 'checksums.sha256' -or $IgnoredFiles -contains $file.FullName) {
            continue
        }

        if (-not $expected.ContainsKey($relativePath)) {
            $problems += "not part of the package: $relativePath"
            continue
        }

        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $expected[$relativePath]) {
            $problems += "changed since the package was built: $relativePath"
        }
        $expected.Remove($relativePath)
    }

    foreach ($relativePath in $expected.Keys) {
        $problems += "missing: $relativePath"
    }

    if ($problems.Count -gt 0) {
        throw "The package failed its integrity check:`n    " + ($problems -join "`n    ")
    }
}

function Test-IsAdministrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Returns the Win32_Service instance, or nothing when the service doesn't exist.
function Get-ServiceInstance {
    param([string]$Name)
    return Get-CimInstance -ClassName Win32_Service -Filter "Name='$($Name.Replace("'", "\'"))'"
}

function Invoke-ServiceChange {
    param([string]$Name, [hashtable]$Arguments)

    $result = Invoke-CimMethod -InputObject (Get-ServiceInstance $Name) -MethodName Change -Arguments $Arguments
    if ($result.ReturnValue -ne 0) {
        throw "Changing the service '$Name' failed with Win32_Service error code $($result.ReturnValue)."
    }
}

function Stop-ServiceAndWait {
    param([string]$Name)

    $service = Get-Service -Name $Name
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
}

# A service that fails during startup can report Running for a moment, so the status is read again after a pause.
function Start-ServiceAndVerify {
    param([string]$Name)

    Start-Service -Name $Name
    Start-Sleep -Seconds 3
    $status = (Get-Service -Name $Name).Status
    if ($status -ne 'Running') {
        throw "The service '$Name' was started but is now $status. The Application event log usually says why."
    }
}

# StartMode is 'auto', 'delayed-auto', 'demand' or 'disabled', as sc.exe names them.
function Set-ServiceStartMode {
    param([string]$Name, [string]$StartMode)

    & sc.exe config $Name start= $StartMode | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Setting the start mode of '$Name' to '$StartMode' failed (sc.exe exit code $LASTEXITCODE)."
    }
}

# A service's environment variables live in a multi-string registry value under its key.
function Get-ServiceEnvironment {
    param([string]$Name)

    $property = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$Name" -Name Environment -ErrorAction SilentlyContinue
    if ($null -eq $property) {
        return @()
    }

    return @($property.Environment)
}

function Set-ServiceEnvironment {
    param([string]$Name, [string[]]$Entries = @())

    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    if ($Entries.Count -eq 0) {
        Remove-ItemProperty -LiteralPath $key -Name Environment -ErrorAction SilentlyContinue
    }
    else {
        Set-ItemProperty -LiteralPath $key -Name Environment -Value $Entries -Type MultiString
    }
}

# Makes Destination an exact copy of Source. robocopy retries files that are still locked by a stopping service.
function Copy-DirectoryMirror {
    param([string]$Source, [string]$Destination)

    & robocopy.exe $Source.TrimEnd('\') $Destination.TrimEnd('\') /MIR /R:5 /W:3 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "Copying '$Source' to '$Destination' failed (robocopy exit code $LASTEXITCODE)."
    }
}

# Creates a folder that only administrators and SYSTEM can change. A folder made under a drive root would
# otherwise let every signed-in user change it, and so replace the files of a service.
function New-ProtectedDirectory {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Setting the permissions of '$Path' failed (icacls exit code $LASTEXITCODE)."
    }
}

# Lets the account read and run what is in the folder, on top of the permissions the folder already has.
function Grant-DirectoryRead {
    param([string]$Path, [string]$Account)

    # icacls does not accept the ".\user" form that services use for a local account.
    if ($Account.StartsWith('.\')) {
        $Account = $env:COMPUTERNAME + $Account.Substring(1)
    }

    & icacls.exe $Path /grant "${Account}:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Giving '$Account' read access to '$Path' failed (icacls exit code $LASTEXITCODE)."
    }
}

# Returns the accounts, other than administrators and the system, that own the folder or can change what is in it.
# With -AsParent it returns only the accounts that could replace a folder below this one.
function Get-DirectoryWriters {
    param([string]$Path, [switch]$AsParent)

    # SYSTEM, Administrators and TrustedInstaller. CREATOR OWNER (S-1-3-0) is skipped below because it only stands
    # for whoever creates a file later.
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

    # 0x10000000 is GENERIC_ALL and 0x40000000 is GENERIC_WRITE. A rule that only applies to new files can grant
    # these instead of specific rights.
    $write = [int][Security.AccessControl.FileSystemRights]'Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership' -bor 0x10000000
    if (-not $AsParent) {
        # Adding entries to a folder above, which C:\ProgramData lets every user do, doesn't change a folder that is
        # already there. Writing into the folder itself does.
        $write = $write -bor [int][Security.AccessControl.FileSystemRights]'Write' -bor 0x40000000
    }
    $acl = Get-Acl -LiteralPath $Path

    $sids = @($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value)
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and ([int]$rule.FileSystemRights -band $write) -and $rule.IdentityReference.Value -ne 'S-1-3-0') {
            $sids += $rule.IdentityReference.Value
        }
    }

    foreach ($sid in $sids | Where-Object { $trusted -notcontains $_ } | Select-Object -Unique) {
        try {
            ([Security.Principal.SecurityIdentifier]$sid).Translate([Security.Principal.NTAccount]).Value
        }
        catch {
            $sid
        }
    }
}

# Returns the folder and every folder above it, outermost first, leaving out the drive or share root.
function Get-FolderChain {
    param([string]$Path)

    $chain = @()
    $current = $Path.TrimEnd('\')
    while ($current -and [IO.Path]::GetDirectoryName($current)) {
        $chain = @($current) + $chain
        $current = [IO.Path]::GetDirectoryName($current)
    }

    return $chain
}

# Describes each existing folder on the way to Path that an account other than administrators owns or can change.
# Whoever can rename or delete a folder controls everything below it.
function Get-UnprotectedFolders {
    param([string]$Path)

    foreach ($folder in Get-FolderChain $Path) {
        if (Test-Path -LiteralPath $folder) {
            $writers = @(Get-DirectoryWriters $folder -AsParent:($folder -ne $Path.TrimEnd('\')))
            if ($writers.Count -gt 0) {
                "$folder ($($writers -join ', '))"
            }
        }
    }
}

# Backup folders are named after the install run that made them, a UTC timestamp, so sorting by name sorts by age.
# Only a folder with backup.json is complete.
function Get-ServiceBackups {
    param([string]$BackupPath)

    if (-not (Test-Path -LiteralPath $BackupPath)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $BackupPath -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'backup.json') } |
        Sort-Object Name -Descending)
}

# Returns the id of the most recent install run that backed up any service in the package, or nothing.
function Get-LastRunId {
    param($Manifest)

    $newest = $Manifest.resources |
        ForEach-Object { @(Get-ServiceBackups $_.windowsService.backupPath)[0] } |
        Where-Object { $_ } |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($newest) {
        return $newest.Name
    }
}
