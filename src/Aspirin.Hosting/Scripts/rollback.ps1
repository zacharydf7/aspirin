<#
.SYNOPSIS
    Undoes the most recent run of install.ps1.

.DESCRIPTION
    install.ps1 takes a backup of each service and site before changing it. This script finds the most recent install
    run and restores the backup it took of each one. For a service that is the files, binary path, display name,
    description, start mode and environment. For an IIS site it is the files, the site and its application pool as
    IIS had them, and which certificate each of its HTTPS bindings used. A service, site or pool that did not exist
    before that run is removed. A service or site the run did not reach is left alone.

    A service's account is not restored, because its password is not kept. If the install changed it, set it back
    in the Services console. An application pool's account is restored with the rest of the pool.

.PARAMETER Force
    Restores a backup even though accounts other than administrators could have changed it. Without this, the
    script stops, because a changed backup could make a service or site run something else.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'iis.ps1')

function Restore-WindowsService {
    param($Resource, [string]$RunId)

    $name = $Resource.windowsService.serviceName
    Write-Host "[$($Resource.name)] Windows service '$name'"

    $backup = Join-Path $Resource.backupPath $RunId
    if (-not (Test-Path -LiteralPath (Join-Path $backup 'backup.json'))) {
        Write-Step 'The install did not reach this service, so it is left as it is.'
        return
    }

    $record = Get-Content -LiteralPath (Join-Path $backup 'backup.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $existing = Get-ServiceInstance $name
    if ($existing) {
        Write-Step 'Stopping the service'
        Stop-ServiceAndWait $name
    }

    Restore-Files $Resource $backup

    if (-not $record.serviceExisted) {
        if ($existing) {
            Write-Step 'Removing the service, which did not exist before the install'
            & sc.exe delete $name | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Removing the service '$name' failed (sc.exe exit code $LASTEXITCODE)."
            }
        }
        return
    }

    if (-not $existing) {
        throw "The service '$name' no longer exists, so its configuration can't be restored. Its files were restored."
    }

    Invoke-ServiceChange $name @{ PathName = $record.binaryPath; DisplayName = $record.displayName }
    if ($null -ne $record.description) {
        Set-Service -Name $name -Description $record.description
    }

    $startMode = @{ Auto = 'auto'; Manual = 'demand'; Disabled = 'disabled' }[$record.startMode]
    if ($record.startMode -eq 'Auto' -and $record.delayedAutoStart) {
        $startMode = 'delayed-auto'
    }
    Set-ServiceStartMode $name $startMode
    Set-ServiceEnvironment $name @($record.environment)

    if ($existing.StartName -ne $record.account) {
        Write-Warning "The service ran as '$($record.account)' before the install and now runs as '$($existing.StartName)'. Change it back in the Services console if needed."

        # The install took the earlier account's read access to the files away. Giving it back means that setting the
        # account back is all that's needed.
        if ($record.account -and $record.account -ne 'LocalSystem' -and (Test-Path -LiteralPath $Resource.installPath)) {
            Restore-DirectoryRead $Resource.installPath $record.account
        }
    }

    if ($record.wasRunning) {
        Write-Step 'Starting the service'
        Start-ServiceAndVerify $name
    }
}

# Gives an earlier account its read access again. When that fails, for example because the account was deleted
# since the install, it warns instead of stopping, so the rest of the rollback still runs.
function Restore-DirectoryRead {
    param([string]$Path, [string]$Account)

    try {
        Grant-DirectoryRead $Path $Account
    }
    catch {
        Write-Warning "Couldn't give '$Account' read access to '$Path' again: $($_.Exception.Message)"
    }
}

function Restore-Files {
    param($Resource, [string]$Backup)

    $backedUpFiles = Join-Path $Backup 'app'
    if (Test-Path -LiteralPath $backedUpFiles) {
        Write-Step 'Restoring the files'
        Copy-DirectoryMirror $backedUpFiles $Resource.installPath
    }
    elseif (Test-Path -LiteralPath $Resource.installPath) {
        Remove-Item -LiteralPath $Resource.installPath -Recurse -Force
    }
}

function Restore-IisSite {
    param($Resource, [string]$RunId)

    $definition = $Resource.iisSite
    $siteName = $definition.siteName
    $poolName = $definition.appPoolName
    Write-Host "[$($Resource.name)] IIS site '$siteName'"

    $backup = Join-Path $Resource.backupPath $RunId
    if (-not (Test-Path -LiteralPath (Join-Path $backup 'backup.json'))) {
        Write-Step 'The install did not reach this site, so it is left as it is.'
        return
    }

    $record = Get-Content -LiteralPath (Join-Path $backup 'backup.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (Get-IisSite $siteName) {
        Write-Step 'Stopping the site'
        if ((Get-IisState 'site' $siteName) -eq 'Started') {
            Invoke-AppCmd stop site $siteName | Out-Null
        }
    }
    if (Get-IisAppPool $poolName) {
        if ((Get-IisState 'apppool' $poolName) -eq 'Started') {
            Invoke-AppCmd stop apppool $poolName | Out-Null
        }
        Wait-AppPoolIdle $poolName
    }

    # The backup never has the "updating" page, so the mirror copy removes it.
    Restore-Files $Resource $backup

    if (Get-IisSite $siteName) {
        Invoke-AppCmd delete site $siteName | Out-Null
    }
    $currentFileAccount = $null
    $currentPool = Get-IisAppPool $poolName
    if ($currentPool) {
        $currentFileAccount = Get-ExistingPoolFileAccount $currentPool
        Invoke-AppCmd delete apppool $poolName | Out-Null
    }

    if ($record.poolExisted) {
        Write-Step "Restoring the application pool '$poolName'"
        Import-IisObject 'apppool' (Join-Path $backup 'apppool.xml')

        # The install moved the files' read access from the pool's earlier account to its new one, so it moves back.
        $restoredFileAccount = Get-ExistingPoolFileAccount (Get-IisAppPool $poolName)
        if ($restoredFileAccount -ne $currentFileAccount -and (Test-Path -LiteralPath $Resource.installPath)) {
            if ($currentFileAccount) {
                Revoke-DirectoryRead $Resource.installPath $currentFileAccount
            }
            if ($restoredFileAccount) {
                Restore-DirectoryRead $Resource.installPath $restoredFileAccount
            }
        }
    }
    else {
        Write-Step "Removed the application pool '$poolName', which did not exist before the install"
    }

    foreach ($entry in @($record.certificateBindings)) {
        $endpoint = [pscustomobject]@{ Kind = $entry.kind; Endpoint = $entry.endpoint }
        if ($entry.existed) {
            Set-SslCertificateBinding $endpoint $entry.certificateHash $entry.store $entry.appId
        }
        else {
            Remove-SslCertificateBinding $endpoint
        }
    }

    if (-not $record.siteExisted) {
        Write-Step 'Removed the site, which did not exist before the install'
        return
    }

    Write-Step 'Restoring the site'
    Import-IisObject 'site' (Join-Path $backup 'site.xml')
    if ($record.siteWasStarted) {
        Write-Step 'Starting the site'
        if ($record.poolExisted) {
            Start-IisObject 'apppool' $poolName
        }
        Start-IisObject 'site' $siteName
    }
}

try {
    $manifest = Read-PackageManifest $PSScriptRoot
    if (-not (Test-IsAdministrator)) {
        throw 'Run this script in an elevated PowerShell session (Run as administrator).'
    }

    $runId = Get-LastRunId $manifest
    if (-not $runId) {
        throw 'No backup was found for any resource in this package, so there is nothing to roll back.'
    }

    # The backup says which program the service or site runs, so a backup someone other than an administrator could
    # edit is restored only with -Force.
    $unprotected = @($manifest.resources |
        ForEach-Object { Join-Path $_.backupPath $runId } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'backup.json') } |
        ForEach-Object { Get-UnprotectedFolders $_ } |
        Select-Object -Unique)
    if ($unprotected.Count -gt 0 -and -not $Force) {
        throw ("Accounts other than administrators own or can change these folders on the path to the backups:`n    " + ($unprotected -join "`n    ") +
            "`nCheck backup.json in each resource's '$runId' backup folder, then run rollback.ps1 -Force.")
    }

    # Resources are rolled back in the reverse of the order they were installed in.
    Write-Host "Undoing the install run $runId."
    $resources = @($manifest.resources)
    [array]::Reverse($resources)
    foreach ($resource in $resources) {
        if ($resource.kind -eq 'iisSite') {
            Restore-IisSite $resource $runId
        }
        else {
            Restore-WindowsService $resource $runId
        }
    }

    if (@($manifest.resources | Where-Object { $_.kind -eq 'iisSite' }).Count -gt 0) {
        Write-Host "The install also backed up the whole IIS configuration as 'aspirin-$runId'. If IIS still isn't right, an administrator can restore it, which also undoes every other IIS change made since: & '$script:AppCmd' restore backup aspirin-$runId"
    }

    Write-Host 'Rollback complete.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
