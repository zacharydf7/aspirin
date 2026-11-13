# Functions shared by install.ps1 and rollback.ps1. Written for Windows PowerShell 5.1.

$ErrorActionPreference = 'Stop'

# Windows PowerShell started from PowerShell 7 inherits a module path that hides its own modules, and built-in
# commands such as Get-FileHash go missing. Putting its own module folder first brings them back.
if ($PSVersionTable.PSEdition -ne 'Core') {
    $env:PSModulePath = "$PSHOME\Modules;$env:PSModulePath"
}

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
    if ($manifest.schemaVersion -ne 2) {
        throw "This script supports package schema version 2, but the package is version '$($manifest.schemaVersion)'."
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

# Makes Destination an exact copy of Source, apart from the files named in Exclude, which are neither copied nor
# removed. robocopy retries files that are still locked by a stopping service.
function Copy-DirectoryMirror {
    param([string]$Source, [string]$Destination, [string[]]$Exclude = @())

    $excludeArguments = @()
    if ($Exclude.Count -gt 0) {
        $excludeArguments = @('/XF') + $Exclude
    }

    & robocopy.exe $Source.TrimEnd('\') $Destination.TrimEnd('\') /MIR /R:5 /W:3 /NFL /NDL /NJH /NJS /NP @excludeArguments | Out-Null
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

# Says where an account's password is checked: its domain for DOMAIN\name, this computer's domain for a UPN
# (name@suffix), and this machine for a local account (.\name, <computer>\name or a bare name). A UPN goes to this
# computer's domain because its suffix needn't be a domain name, and the domain finds the account across the forest.
# ContextName is $null for this computer's domain.
function Get-AccountContext {
    param([string]$Account)

    if ($Account.Contains('@')) {
        return [pscustomobject]@{ ContextType = 'Domain'; ContextName = $null; UserName = $Account }
    }

    if ($Account.Contains('\')) {
        $domain, $userName = $Account.Split('\', 2)
        if ($domain -ne '.' -and $domain -ne $env:COMPUTERNAME) {
            return [pscustomobject]@{ ContextType = 'Domain'; ContextName = $domain; UserName = $userName }
        }
        $Account = $userName
    }

    return [pscustomobject]@{ ContextType = 'Machine'; ContextName = $env:COMPUTERNAME; UserName = $Account }
}

# Fails unless the credential's password is right, checked where Get-AccountContext says. Logon rights aren't
# checked, only the password.
function Assert-AccountPassword {
    param([pscredential]$Credential)

    Add-Type -AssemblyName System.DirectoryServices.AccountManagement
    $account = $Credential.UserName
    $target = Get-AccountContext $account

    try {
        if ($null -eq $target.ContextName) {
            $context = New-Object System.DirectoryServices.AccountManagement.PrincipalContext($target.ContextType)
        }
        else {
            $context = New-Object System.DirectoryServices.AccountManagement.PrincipalContext($target.ContextType, $target.ContextName)
        }
        try {
            # Without options, a domain check can try a second kind of bind after the first fails, so one wrong
            # password could count twice toward locking the account out.
            $valid = $context.ValidateCredentials($target.UserName, $Credential.GetNetworkCredential().Password,
                [System.DirectoryServices.AccountManagement.ContextOptions]::Negotiate)
        }
        finally {
            $context.Dispose()
        }
    }
    catch {
        $reason = $_.Exception.Message
        if ($_.Exception.InnerException) {
            $reason = $_.Exception.InnerException.Message
        }
        throw "The password for '$account' couldn't be checked: $reason"
    }

    if (-not $valid) {
        throw "The password for '$account' is wrong, or the account doesn't exist, is locked out or is disabled."
    }
}

# Fails unless Windows lets the account log on the way a service (Service) or an application pool (Batch) does.
# Trying the logon checks the right as Windows will when the service or pool starts: granted to the account or one of
# its groups, and not taken away by a policy that denies it. The attempt shows in the Security event log as a logon.
function Assert-AccountLogon {
    param([pscredential]$Credential, [ValidateSet('Service', 'Batch')][string]$LogonType)

    if (-not ('Aspirin.Logon' -as [type])) {
        Add-Type -Namespace Aspirin -Name Logon -MemberDefinition @'
[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);

[DllImport("kernel32.dll")]
private static extern bool CloseHandle(IntPtr handle);

// Returns 0 when the logon works, or the Windows error code.
public static int Try(string user, string domain, string password, int logonType)
{
    IntPtr token;
    if (LogonUser(user, domain, password, logonType, 0, out token))
    {
        CloseHandle(token);
        return 0;
    }
    return Marshal.GetLastWin32Error();
}
'@
    }

    $account = $Credential.UserName
    $target = Get-AccountContext $account
    $domain = $target.ContextName
    if ($target.ContextType -eq 'Machine') {
        $domain = '.'
    }

    $logonTypes = @{ Batch = 4; Service = 5 }
    $rights = @{ Batch = 'Log on as a batch job'; Service = 'Log on as a service' }
    $logonTypeNotGranted = 1385  # ERROR_LOGON_TYPE_NOT_GRANTED
    $code = [Aspirin.Logon]::Try($target.UserName, $domain, $Credential.GetNetworkCredential().Password, $logonTypes[$LogonType])
    if ($code -eq $logonTypeNotGranted) {
        throw "The account '$account' lacks the '$($rights[$LogonType])' right, or a policy denies it. Ask an administrator to grant it, then run the installer again."
    }
    if ($code -ne 0) {
        throw "Windows wouldn't let '$account' log on to check its '$($rights[$LogonType])' right: $((New-Object ComponentModel.Win32Exception($code)).Message)"
    }
}

# Returns the SIDs that hold a user right, such as SeBatchLogonRight, under security policy, both local and from the
# domain.
function Get-UserRightHolders {
    param([string]$Right)

    $path = [IO.Path]::GetTempFileName()
    try {
        # /mergedpolicy includes rights that domain Group Policy sets, not only local ones.
        & secedit.exe /export /mergedpolicy /areas USER_RIGHTS /cfg $path /quiet | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Reading the user rights from security policy failed (secedit exit code $LASTEXITCODE)."
        }
        # Get-Content reads the file in whatever encoding secedit wrote, which it marks with a byte order mark.
        $lines = @(Get-Content -LiteralPath $path)
        if ($lines -notcontains '[Privilege Rights]') {
            throw "secedit exported the user rights in a form the installer doesn't recognize:`n$(($lines | Select-Object -First 10) -join "`n")"
        }
        $line = $lines | Where-Object { $_ -match "^$Right\s*=" } | Select-Object -First 1
        if (-not $line) {
            return @()
        }

        # A holder is written as *SID, or as a name when secedit couldn't write its SID.
        return @(($line -split '=', 2)[1].Split(',') | ForEach-Object {
            $holder = $_.Trim()
            if ($holder.StartsWith('*')) {
                $holder.Substring(1)
            }
            else {
                try {
                    ([Security.Principal.NTAccount]$holder).Translate([Security.Principal.SecurityIdentifier]).Value
                }
                catch {
                    $holder
                }
            }
        })
    }
    finally {
        Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
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
function Get-Backups {
    param([string]$BackupPath)

    if (-not (Test-Path -LiteralPath $BackupPath)) {
        return @()
    }

    return @(Get-ChildItem -LiteralPath $BackupPath -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'backup.json') } |
        Sort-Object Name -Descending)
}

# Returns the id of the most recent install run that backed up any resource in the package, or nothing.
function Get-LastRunId {
    param($Manifest)

    $newest = $Manifest.resources |
        ForEach-Object { @(Get-Backups $_.backupPath)[0] } |
        Where-Object { $_ } |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($newest) {
        return $newest.Name
    }
}
