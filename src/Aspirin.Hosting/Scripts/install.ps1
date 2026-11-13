<#
.SYNOPSIS
    Installs or updates the Windows services and IIS sites in this package.

.DESCRIPTION
    Reads package.json from the folder this script is in. For each service it stops the running service, backs up
    the installed files, copies the new files, creates or updates the service, and starts it. For each IIS site it
    backs up the files and the site's IIS configuration, shows an "updating" page while it copies the new files,
    creates or updates the site and its application pool, and checks that the site answers. Running the script again
    updates the same services and sites, so it is safe to repeat.

    Nothing is changed until the package has passed its integrity check, every setting has a value, and the server
    has what the sites need.

.PARAMETER SettingsFile
    A JSON file with values for settings the package doesn't carry, such as secrets, or values to override:
    { "<resource name>": { "<setting name>": "<value>" } }

.PARAMETER Credential
    The account and its password, for a service or application pool that runs as a user account. Without it, the
    script asks for the password when a service or pool is created or its account changes.

.PARAMETER KeepBackups
    How many backups to keep for each service and site.

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
. (Join-Path $PSScriptRoot 'iis.ps1')

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

function Get-ResourceAccount {
    param($Resource)

    if ($Resource.kind -eq 'iisSite') {
        return $Resource.iisSite.account
    }
    return $Resource.windowsService.account
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
        $account = Get-ResourceAccount $resource
        if (-not (Test-AccountNeedsPassword $account) -or $credentials.ContainsKey($account)) {
            continue
        }

        if ($Credential -and $Credential.UserName -eq $account) {
            $credentials[$account] = $Credential
            continue
        }

        # IIS and the service manager keep the password of an account that doesn't change.
        if ($resource.kind -eq 'iisSite') {
            $current = Get-PoolUserName $resource.iisSite.appPoolName
        }
        else {
            $current = (Get-ServiceInstance $resource.windowsService.serviceName).StartName
        }
        if ($current -eq $account) {
            continue
        }

        try {
            $credentials[$account] = Get-Credential -UserName $account -Message "Password for the account '$account'"
        }
        catch {
            $credentials[$account] = $null
        }

        if ($null -eq $credentials[$account]) {
            throw "The account '$account' needs its password. Pass -Credential, or run this script in an interactive session."
        }
    }

    return $credentials
}

function Write-InstallPlan {
    param($Resource, [string[]]$Environment, [bool]$CanReadIis)

    $settings = "$((@($Environment | ForEach-Object { $_.Split('=')[0] })) -join ', ')"
    if ($Resource.kind -eq 'iisSite') {
        $definition = $Resource.iisSite
        $site = $null
        if ($CanReadIis) {
            $site = Get-IisSite $definition.siteName
        }
        $action = 'create or update'
        if ($CanReadIis) {
            $action = if ($site) { 'update' } else { 'create' }
        }

        $account = $definition.account
        if ([string]::IsNullOrEmpty($account)) {
            $account = "the pool's own identity"
        }

        Write-Host "[$($Resource.name)] Would $action the IIS site '$($definition.siteName)' (version $($Resource.version))"
        Write-Step "Install to:  $($Resource.installPath)"
        Write-Step "Back up to:  $($Resource.backupPath)"
        Write-Step "App pool:    $($definition.appPoolName), running as $account"
        foreach ($binding in $definition.bindings) {
            $line = "Binding:     $(Format-Binding $binding)"
            if ($binding.protocol -eq 'https') {
                if ($CanReadIis) {
                    $certificate = Find-BindingCertificate $binding
                    $line += " with the certificate $($certificate.Thumbprint), valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
                }
                elseif ($binding.certificateThumbprint) {
                    $line += " with the certificate $($binding.certificateThumbprint)"
                }
                else {
                    $line += " with the newest certificate whose subject is '$($binding.certificateSubject)'"
                }
            }
            Write-Step $line
        }
        if ($site) {
            foreach ($binding in (Compare-SiteBindings $definition $site).Remove) {
                Write-Step "Remove:      the binding $($binding.Protocol) $($binding.Information), which the package doesn't have"
            }
        }
        Write-Step "Settings:    $settings"
        if (-not $CanReadIis) {
            Write-Step 'Run this in an elevated session to compare with what IIS has now and to check the certificates.'
        }
        return
    }

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
    Write-Step "Install to:  $($Resource.installPath)"
    Write-Step "Back up to:  $($Resource.backupPath)"
    Write-Step "Run as:      $account"
    Write-Step "Start mode:  $($definition.startMode)"
    Write-Step "Settings:    $settings"
}

# Creates the folder, and any missing folder above it, with restricted permissions. Warns about each folder that
# already exists and that accounts other than administrators can change.
function Initialize-ResourceDirectory {
    param([string]$Path)

    $unprotected = @(Get-UnprotectedFolders $Path)
    if ($unprotected.Count -gt 0) {
        Write-Warning ("Accounts other than administrators own or can change these folders:`n    " + ($unprotected -join "`n    ") +
            "`nThe installer left their permissions unchanged. If any of these accounts shouldn't be able to change what is installed, fix the folders' permissions.")
    }

    foreach ($folder in Get-FolderChain $Path) {
        if (-not (Test-Path -LiteralPath $folder)) {
            New-ProtectedDirectory $folder
        }
    }
}

function New-ServiceBackup {
    param($Resource, $Existing, [string]$RunId)

    $definition = $Resource.windowsService
    $folder = Join-Path $Resource.backupPath $RunId
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
        $record.environment = @(Get-ServiceEnvironment $definition.serviceName)
    }

    if (Test-Path -LiteralPath $Resource.installPath) {
        Copy-DirectoryMirror $Resource.installPath (Join-Path $folder 'app')
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
    Initialize-ResourceDirectory $Resource.backupPath
    $backup = New-ServiceBackup $Resource $existing $RunId
    $script:backupTaken = $true
    Write-Step "Backed up the current install to $backup"

    Initialize-ResourceDirectory $Resource.installPath

    if ($existing) {
        Write-Step 'Stopping the service'
        Stop-ServiceAndWait $name
    }

    Write-Step "Copying files to $($Resource.installPath)"
    Copy-DirectoryMirror (Join-Path $PackageRoot $Resource.source.Replace('/', '\')) $Resource.installPath

    $binaryPath = '"' + $Resource.installPath + '\' + $definition.executable + '"'
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
        Grant-DirectoryRead $Resource.installPath $account
    }

    Set-ServiceEnvironment $name $Environment

    if ($definition.startMode -ne 'manual') {
        Write-Step 'Starting the service'
        Start-ServiceAndVerify $name
    }

    Get-Backups $Resource.backupPath | Select-Object -Skip $KeepBackups | Remove-Item -Recurse -Force
}

function New-SiteBackup {
    param($Resource, $Site, $Pool, [string]$RunId)

    $definition = $Resource.iisSite
    $folder = Join-Path $Resource.backupPath $RunId
    New-Item -ItemType Directory -Path $folder -Force | Out-Null

    $record = [ordered]@{ siteExisted = [bool]$Site; poolExisted = [bool]$Pool; siteWasStarted = $false; certificateBindings = @() }
    if ($Site) {
        $record.siteWasStarted = (Get-IisState 'site' $definition.siteName) -eq 'Started'
        Export-IisObject 'site' $definition.siteName (Join-Path $folder 'site.xml')
    }
    if ($Pool) {
        # The pool's configuration can hold its account's password, which is why the folder is for administrators only.
        Export-IisObject 'apppool' $definition.appPoolName (Join-Path $folder 'apppool.xml')
    }

    # The endpoints the package's HTTPS bindings use, and those of the site's HTTPS bindings the install may remove.
    $endpoints = @($definition.bindings | Where-Object { $_.protocol -eq 'https' } | ForEach-Object { Get-SslEndpoint $_ }) +
        @(Get-SiteBindings $Site | Where-Object { $_.Protocol -eq 'https' } | ForEach-Object { Get-ListedBindingEndpoint $_ })
    foreach ($endpoint in $endpoints | Sort-Object Endpoint -Unique) {
        $current = Get-SslCertificateBinding $endpoint
        $entry = [ordered]@{ kind = $endpoint.Kind; endpoint = $endpoint.Endpoint; existed = [bool]$current }
        if ($current) {
            $entry.certificateHash = $current.CertificateHash
            $entry.appId = $current.AppId
            $entry.store = $current.Store
        }
        $record.certificateBindings += [pscustomobject]$entry
    }

    if (Test-Path -LiteralPath $Resource.installPath) {
        # An earlier install that stopped partway can have left the "updating" page behind; a rollback mustn't bring it back.
        Copy-DirectoryMirror $Resource.installPath (Join-Path $folder 'app') -Exclude $script:OfflineFile
    }

    # Written last, so a backup that was interrupted is never used for a rollback.
    $record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $folder 'backup.json') -Encoding UTF8
    return $folder
}

function Install-IisSite {
    param($Resource, [string]$PackageRoot, [string[]]$Environment, [pscredential]$Credential, [int]$KeepBackups, [string]$RunId)

    $definition = $Resource.iisSite
    $siteName = $definition.siteName
    $poolName = $definition.appPoolName
    $site = Get-IisSite $siteName
    $pool = Get-IisAppPool $poolName

    Write-Host "[$($Resource.name)] IIS site '$siteName' (version $($Resource.version))"

    Initialize-ResourceDirectory $Resource.backupPath
    $backup = New-SiteBackup $Resource $site $pool $RunId
    $script:backupTaken = $true
    Write-Step "Backed up the current install to $backup"

    Initialize-ResourceDirectory $Resource.installPath

    # The ASP.NET Core Module stops the app while this file exists and shows it to visitors instead.
    $offlinePage = Join-Path $Resource.installPath $script:OfflineFile
    if ($site) {
        Write-Step 'Showing the "updating" page'
        Set-Content -LiteralPath $offlinePage -Encoding UTF8 -Value '<!DOCTYPE html><title>Updating</title><p>This site is being updated. Try again in a minute.</p>'
        Wait-AppPoolIdle $poolName
    }

    Write-Step "Copying files to $($Resource.installPath)"
    Copy-DirectoryMirror (Join-Path $PackageRoot $Resource.source.Replace('/', '\')) $Resource.installPath -Exclude $script:OfflineFile

    if ($pool) {
        Write-Step "Updating the application pool '$poolName'"
    }
    else {
        Write-Step "Creating the application pool '$poolName'"
    }
    Set-IisAppPool $definition $Environment $Credential $backup

    if ($site) {
        Write-Step 'Updating the site'
        Invoke-AppCmd set vdir "$siteName/" "/physicalPath:$($Resource.installPath)" | Out-Null
    }
    else {
        Write-Step 'Creating the site'
        Invoke-AppCmd add site "/name:$siteName" "/physicalPath:$($Resource.installPath)" | Out-Null
    }
    $preload = if ($definition.alwaysRunning) { 'true' } else { 'false' }
    Invoke-AppCmd set app "$siteName/" "/applicationPool:$poolName" "/preloadEnabled:$preload" | Out-Null
    Sync-SiteBindings $definition (Get-IisSite $siteName)

    foreach ($binding in @($definition.bindings | Where-Object { $_.protocol -eq 'https' })) {
        $certificate = Find-BindingCertificate $binding
        Set-SslCertificateBinding (Get-SslEndpoint $binding) $certificate.Thumbprint $binding.certificateStore
        Write-Step "Using the certificate $($certificate.Thumbprint) for $(Format-Binding $binding)"
    }

    # Granted on every run, because the account can change between installs. It comes after the pool exists,
    # because its identity (IIS AppPool\<name>) can't be looked up before then.
    $fileAccount = Get-PoolFileAccount $definition
    if ($fileAccount) {
        Grant-DirectoryRead $Resource.installPath $fileAccount
    }

    Remove-Item -LiteralPath $offlinePage -ErrorAction SilentlyContinue
    Write-Step 'Starting the site'
    Start-IisObject 'apppool' $poolName
    Start-IisObject 'site' $siteName
    Test-SiteHealth $definition

    Get-Backups $Resource.backupPath | Select-Object -Skip $KeepBackups | Remove-Item -Recurse -Force
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
    $isAdministrator = Test-IsAdministrator

    if ($WhatIf) {
        # Only administrators can read the IIS configuration, so without elevation the plan can't be compared with it.
        if ($isAdministrator) {
            Assert-IisReady $manifest
        }
        Write-Host "The package passed its integrity check. Nothing will be changed."
        foreach ($resource in $manifest.resources) {
            Write-InstallPlan $resource $environments[$resource.name] $isAdministrator
        }
        exit 0
    }

    if (-not $isAdministrator) {
        throw 'Run this script in an elevated PowerShell session (Run as administrator).'
    }

    Assert-IisReady $manifest
    $credentials = Get-AccountCredentials $manifest $Credential

    # Every backup this run takes is named after it, which is how rollback.ps1 finds the resources the run changed.
    # UTC keeps the names in order when the clocks go back.
    $runId = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff')
    if (@($manifest.resources | Where-Object { $_.kind -eq 'iisSite' }).Count -gt 0) {
        $iisBackup = New-IisConfigurationBackup $runId $KeepBackups
        Write-Host "Backed up the IIS configuration as '$iisBackup'."
    }

    foreach ($resource in $manifest.resources) {
        $account = [string](Get-ResourceAccount $resource)
        if ($resource.kind -eq 'iisSite') {
            Install-IisSite $resource $PSScriptRoot $environments[$resource.name] $credentials[$account] $KeepBackups $runId
        }
        else {
            Install-WindowsService $resource $PSScriptRoot $environments[$resource.name] $credentials[$account] $KeepBackups $runId
        }
    }

    Write-Host 'Installation complete.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    if ($backupTaken) {
        Write-Host 'The installation stopped partway. Run rollback.ps1 to undo it: resources this run did not reach are left alone.'
    }
    else {
        Write-Host 'Nothing was changed.'
    }
    exit 1
}
