<#
.SYNOPSIS
    Installs or updates the Windows services in this package.

.DESCRIPTION
    Reads package.json from the folder this script is in. For each service it stops the running service, backs up
    the installed files, copies the new files, creates or updates the service, and starts it. Running the script
    again updates the same service, so it is safe to repeat.

    Nothing is changed until the package has passed its integrity check and every setting has a value.

.PARAMETER SettingsFile
    A JSON file with values for settings the package doesn't carry, such as secrets, or values to override:
    { "<resource name>": { "<setting name>": "<value>" } }

.PARAMETER Credential
    The service account and its password, for a service that runs as a user account. Without it, the script asks
    for the password when a service is created or its account changes.

.PARAMETER KeepBackups
    How many backups to keep for each service.

.PARAMETER WhatIf
    Checks the package and the settings, then lists what would be done without changing anything.
#>
[CmdletBinding()]
param(
    [string]$SettingsFile,
    [pscredential]$Credential,
    [ValidateRange(1, 100)]
    [int]$KeepBackups = 3,
    [switch]$WhatIf
)

. (Join-Path $PSScriptRoot 'common.ps1')

# Returns the environment entries (NAME=value) for each resource, keyed by resource name.
function Resolve-Settings {
    param($Manifest, [string]$SettingsFile)

    $overrides = $null
    if ($SettingsFile) {
        $overrides = Get-Content -LiteralPath $SettingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $resourceNames = @($Manifest.resources | ForEach-Object { $_.name })
        foreach ($property in $overrides.PSObject.Properties) {
            if ($resourceNames -notcontains $property.Name) {
                throw "The settings file names the resource '$($property.Name)', which is not in this package."
            }
        }
    }

    $entries = @{}
    $missing = @()
    foreach ($resource in $Manifest.resources) {
        $values = [ordered]@{}
        foreach ($setting in $resource.settings) {
            $values[$setting.name] = $setting.value
        }

        if ($overrides -and $overrides.PSObject.Properties[$resource.name]) {
            foreach ($property in $overrides.($resource.name).PSObject.Properties) {
                $values[$property.Name] = [string]$property.Value
            }
        }

        $entries[$resource.name] = @()
        foreach ($name in $values.Keys) {
            if ($null -eq $values[$name]) {
                $missing += "$($resource.name): $name"
            }
            else {
                $entries[$resource.name] += "$name=$($values[$name])"
            }
        }
    }

    if ($missing.Count -gt 0) {
        throw "These settings have no value in the package. Supply them with -SettingsFile:`n    " + ($missing -join "`n    ")
    }

    return $entries
}

# Built-in accounts, virtual accounts and group managed service accounts have no password to supply.
function Test-AccountNeedsPassword {
    param([string]$Account)

    if ([string]::IsNullOrEmpty($Account) -or $Account -eq 'LocalSystem') {
        return $false
    }

    return -not ($Account -like 'NT AUTHORITY\*' -or $Account -like 'NT SERVICE\*' -or $Account.EndsWith('$'))
}

# Returns the credential for each user account that needs its password set, keyed by account name.
function Get-AccountCredentials {
    param($Manifest, [pscredential]$Credential)

    $credentials = @{}
    foreach ($resource in $Manifest.resources) {
        $account = $resource.windowsService.account
        if (-not (Test-AccountNeedsPassword $account) -or $credentials.ContainsKey($account)) {
            continue
        }

        if ($Credential -and $Credential.UserName -eq $account) {
            $credentials[$account] = $Credential
            continue
        }

        $existing = Get-ServiceInstance $resource.windowsService.serviceName
        if ($existing -and $existing.StartName -eq $account) {
            continue
        }

        try {
            $credentials[$account] = Get-Credential -UserName $account -Message "Password for the service account '$account'"
        }
        catch {
            $credentials[$account] = $null
        }

        if ($null -eq $credentials[$account]) {
            throw "The service account '$account' needs its password. Pass -Credential, or run this script in an interactive session."
        }
    }

    return $credentials
}

function Write-InstallPlan {
    param($Resource, [string[]]$Environment)

    $definition = $Resource.windowsService
    $action = 'create'
    if (Get-ServiceInstance $definition.serviceName) {
        $action = 'update'
    }

    $account = $definition.account
    if ([string]::IsNullOrEmpty($account)) {
        $account = 'LocalSystem'
    }

    Write-Host "[$($Resource.name)] Would $action the Windows service '$($definition.serviceName)' (version $($Resource.version))"
    Write-Step "Install to:  $($definition.installPath)"
    Write-Step "Back up to:  $($definition.backupPath)"
    Write-Step "Run as:      $account"
    Write-Step "Start mode:  $($definition.startMode)"
    Write-Step "Settings:    $((@($Environment | ForEach-Object { $_.Split('=')[0] })) -join ', ')"
}

# Creates the folder, and any missing folder above it, with restricted permissions. Warns about each folder that
# already exists and that accounts other than administrators can change.
function Initialize-ServiceDirectory {
    param([string]$Path)

    $unprotected = @(Get-UnprotectedFolders $Path)
    if ($unprotected.Count -gt 0) {
        Write-Warning ("Accounts other than administrators own or can change these folders:`n    " + ($unprotected -join "`n    ") +
            "`nThe installer left their permissions unchanged. If any of these accounts shouldn't be able to change the service, fix the folders' permissions.")
    }

    foreach ($folder in Get-FolderChain $Path) {
        if (-not (Test-Path -LiteralPath $folder)) {
            New-ProtectedDirectory $folder
        }
    }
}

function New-ServiceBackup {
    param($Definition, $Existing, [string]$RunId)

    $folder = Join-Path $Definition.backupPath $RunId
    New-Item -ItemType Directory -Path $folder -Force | Out-Null

    $record = [ordered]@{ serviceExisted = [bool]$Existing }
    if ($Existing) {
        $record.wasRunning = $Existing.State -eq 'Running'
        $record.binaryPath = $Existing.PathName
        $record.displayName = $Existing.DisplayName
        $record.description = $Existing.Description
        $record.startMode = $Existing.StartMode
        $record.delayedAutoStart = [bool]$Existing.DelayedAutoStart
        $record.account = $Existing.StartName
        $record.environment = @(Get-ServiceEnvironment $Definition.serviceName)
    }

    if (Test-Path -LiteralPath $Definition.installPath) {
        Copy-DirectoryMirror $Definition.installPath (Join-Path $folder 'app')
    }

    # Written last, so a backup that was interrupted is never used for a rollback.
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'backup.json') -Encoding UTF8
    return $folder
}

function Install-WindowsService {
    param($Resource, [string]$PackageRoot, [string[]]$Environment, [pscredential]$Credential, [int]$KeepBackups, [string]$RunId)

    $definition = $Resource.windowsService
    $name = $definition.serviceName
    $existing = Get-ServiceInstance $name
    $account = $definition.account
    if ([string]::IsNullOrEmpty($account)) {
        $account = 'LocalSystem'
    }

    Write-Host "[$($Resource.name)] Windows service '$name' (version $($Resource.version))"

    # The service is changed only after its backup is complete, so rollback.ps1 can always undo this run.
    # The backup holds the service's settings, which can include secrets, so only administrators may read it.
    Initialize-ServiceDirectory $definition.backupPath
    $backup = New-ServiceBackup $definition $existing $RunId
    $script:backupTaken = $true
    Write-Step "Backed up the current install to $backup"

    Initialize-ServiceDirectory $definition.installPath

    if ($existing) {
        Write-Step 'Stopping the service'
        Stop-ServiceAndWait $name
    }

    Write-Step "Copying files to $($definition.installPath)"
    Copy-DirectoryMirror (Join-Path $PackageRoot $Resource.source.Replace('/', '\')) $definition.installPath

    $binaryPath = '"' + $definition.installPath + '\' + $definition.executable + '"'
    if ($existing) {
        Write-Step 'Updating the service'
    }
    else {
        Write-Step 'Creating the service'
        New-Service -Name $name -BinaryPathName $binaryPath | Out-Null
    }

    Invoke-ServiceChange $name @{ PathName = $binaryPath; DisplayName = $definition.displayName }
    if ($null -ne $definition.description) {
        Set-Service -Name $name -Description $definition.description
    }

    $startModes = @{ automatic = 'auto'; delayedAutomatic = 'delayed-auto'; manual = 'demand' }
    Set-ServiceStartMode $name $startModes[$definition.startMode]

    if ($Credential) {
        Invoke-ServiceChange $name @{ StartName = $account; StartPassword = $Credential.GetNetworkCredential().Password }
    }
    elseif ((Get-ServiceInstance $name).StartName -ne $account) {
        Invoke-ServiceChange $name @{ StartName = $account; StartPassword = '' }
    }

    # Granted on every run, because the account can change between installs. It comes after the service exists,
    # because a virtual account (NT SERVICE\<name>) can't be looked up before then.
    if ($account -ne 'LocalSystem') {
        Grant-DirectoryRead $definition.installPath $account
    }

    Set-ServiceEnvironment $name $Environment

    if ($definition.startMode -ne 'manual') {
        Write-Step 'Starting the service'
        Start-ServiceAndVerify $name
    }

    Get-ServiceBackups $definition.backupPath | Select-Object -Skip $KeepBackups | Remove-Item -Recurse -Force
}

$backupTaken = $false
try {
    $manifest = Read-PackageManifest $PSScriptRoot

    $ignoredFiles = @()
    if ($SettingsFile) {
        $ignoredFiles += (Resolve-Path -LiteralPath $SettingsFile).Path
    }

    Assert-PackageIntegrity $PSScriptRoot $ignoredFiles
    $environments = Resolve-Settings $manifest $SettingsFile

    if ($WhatIf) {
        Write-Host "The package passed its integrity check. Nothing will be changed."
        foreach ($resource in $manifest.resources) {
            Write-InstallPlan $resource $environments[$resource.name]
        }
        exit 0
    }

    if (-not (Test-IsAdministrator)) {
        throw 'Run this script in an elevated PowerShell session (Run as administrator).'
    }

    $credentials = Get-AccountCredentials $manifest $Credential

    # Every backup this run takes is named after it, which is how rollback.ps1 finds the services the run changed.
    # UTC keeps the names in order when the clocks go back.
    $runId = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff')
    foreach ($resource in $manifest.resources) {
        Install-WindowsService $resource $PSScriptRoot $environments[$resource.name] $credentials[[string]$resource.windowsService.account] $KeepBackups $runId
    }

    Write-Host 'Installation complete.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    if ($backupTaken) {
        Write-Host 'The installation stopped partway. Run rollback.ps1 to undo it: services this run did not reach are left alone.'
    }
    else {
        Write-Host 'Nothing was changed.'
    }
    exit 1
}
