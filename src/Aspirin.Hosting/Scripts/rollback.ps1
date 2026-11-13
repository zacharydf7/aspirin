<#
.SYNOPSIS
    Undoes the most recent run of install.ps1.

.DESCRIPTION
    install.ps1 takes a backup of each service before changing it. This script finds the most recent install run
    and restores the backup it took of each service: the files, binary path, display name, description, start mode
    and environment. A service that did not exist before that run is removed. A service the run did not reach is
    left alone.

    The service account is not restored, because its password is not kept. If the install changed the account, set
    it back in the Services console.

.PARAMETER Force
    Restores a backup even though accounts other than administrators could have changed it. Without this, the
    script stops, because a changed backup could make the service run something else.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

. (Join-Path $PSScriptRoot 'common.ps1')

function Restore-WindowsService {
    param($Resource, [string]$RunId)

    $definition = $Resource.windowsService
    $name = $definition.serviceName
    Write-Host "[$($Resource.name)] Windows service '$name'"

    $backup = Join-Path $definition.backupPath $RunId
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

    $backedUpFiles = Join-Path $backup 'app'
    if (Test-Path -LiteralPath $backedUpFiles) {
        Write-Step 'Restoring the files'
        Copy-DirectoryMirror $backedUpFiles $definition.installPath
    }
    elseif (Test-Path -LiteralPath $definition.installPath) {
        Remove-Item -LiteralPath $definition.installPath -Recurse -Force
    }

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
    }

    if ($record.wasRunning) {
        Write-Step 'Starting the service'
        Start-ServiceAndVerify $name
    }
}

try {
    $manifest = Read-PackageManifest $PSScriptRoot
    if (-not (Test-IsAdministrator)) {
        throw 'Run this script in an elevated PowerShell session (Run as administrator).'
    }

    $runId = Get-LastRunId $manifest
    if (-not $runId) {
        throw 'No backup was found for any service in this package, so there is nothing to roll back.'
    }

    # The backup says which program the service runs, so a backup someone other than an administrator could edit
    # is restored only with -Force.
    $unprotected = @($manifest.resources |
        ForEach-Object { Join-Path $_.windowsService.backupPath $runId } |
        Where-Object { Test-Path -LiteralPath (Join-Path $_ 'backup.json') } |
        ForEach-Object { Get-UnprotectedFolders $_ } |
        Select-Object -Unique)
    if ($unprotected.Count -gt 0 -and -not $Force) {
        throw ("Accounts other than administrators own or can change these folders on the path to the backups:`n    " + ($unprotected -join "`n    ") +
            "`nCheck backup.json in each service's '$runId' backup folder, then run rollback.ps1 -Force.")
    }

    # Services are rolled back in the reverse of the order they were installed in.
    Write-Host "Undoing the install run $runId."
    $resources = @($manifest.resources)
    [array]::Reverse($resources)
    foreach ($resource in $resources) {
        Restore-WindowsService $resource $runId
    }

    Write-Host 'Rollback complete.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
