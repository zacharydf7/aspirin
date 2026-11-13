# IIS functions shared by install.ps1 and rollback.ps1. Written for Windows PowerShell 5.1. They use appcmd.exe,
# netsh.exe and the registry, which every IIS server has, rather than the IIS PowerShell modules, which are an
# optional feature.

$script:AppCmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'

# The application ID IIS Manager gives the certificate bindings it makes.
$script:IisAppId = '{4dc3e181-e14b-4a21-b022-59fc669b0914}'

$script:OfflineFile = 'app_offline.htm'

function Invoke-AppCmd {
    $output = & $script:AppCmd @args
    if ($LASTEXITCODE -ne 0) {
        throw "appcmd.exe $($args -join ' ') failed (exit code $LASTEXITCODE): $($output -join ' ')"
    }
    return $output
}

# Runs appcmd.exe with its input or output in a file. A file keeps the text exactly as appcmd reads or writes it,
# where PowerShell's pipeline would change the encoding of characters outside ASCII.
function Invoke-AppCmdWithFile {
    param([string[]]$Arguments, [string]$InputPath, [string]$OutputPath)

    $quoted = @($Arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $temporary = @()
    if (-not $OutputPath) {
        $OutputPath = [IO.Path]::GetTempFileName()
        $temporary += $OutputPath
    }
    $errorPath = [IO.Path]::GetTempFileName()
    $temporary += $errorPath

    $redirect = @{ RedirectStandardOutput = $OutputPath; RedirectStandardError = $errorPath }
    if ($InputPath) {
        $redirect.RedirectStandardInput = $InputPath
    }

    try {
        $process = Start-Process -FilePath $script:AppCmd -ArgumentList $quoted -NoNewWindow -Wait -PassThru @redirect
        if ($process.ExitCode -ne 0) {
            $message = ((Get-Content -LiteralPath $OutputPath, $errorPath) -join ' ').Trim()
            throw "appcmd.exe $($Arguments -join ' ') failed (exit code $($process.ExitCode)): $message"
        }
    }
    finally {
        $temporary | ForEach-Object { Remove-Item -LiteralPath $_ -ErrorAction SilentlyContinue }
    }
}

# Returns appcmd's description of every site or application pool (Kind 'site' or 'apppool'), configuration included.
function Get-IisObjects {
    param([string]$Kind)

    return Get-AppCmdXml @('list', $Kind, '/config', '/xml') $Kind.ToUpperInvariant()
}

# Runs an appcmd list command with /xml and returns the elements it lists.
function Get-AppCmdXml {
    param([string[]]$Arguments, [string]$ElementName)

    $path = [IO.Path]::GetTempFileName()
    try {
        Invoke-AppCmdWithFile $Arguments -OutputPath $path
        if ((Get-Item -LiteralPath $path).Length -eq 0) {
            return @()
        }
        $document = New-Object Xml.XmlDocument
        $document.Load($path)
        return @($document.DocumentElement.SelectNodes($ElementName))
    }
    finally {
        Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
    }
}

function Get-IisSite {
    param([string]$Name)
    return Get-IisObjects 'site' | Where-Object { $_.GetAttribute('SITE.NAME') -eq $Name }
}

function Get-IisAppPool {
    param([string]$Name)
    return Get-IisObjects 'apppool' | Where-Object { $_.GetAttribute('APPPOOL.NAME') -eq $Name }
}

# Saves one site or pool as appcmd XML, which Import-IisObject can add back as it was.
function Export-IisObject {
    param([string]$Kind, [string]$Name, [string]$Path)
    Invoke-AppCmdWithFile @('list', $Kind, $Name, '/config', '/xml') -OutputPath $Path
}

function Import-IisObject {
    param([string]$Kind, [string]$Path)
    Invoke-AppCmdWithFile @('add', $Kind, '/in') -InputPath $Path
}

function Get-IisState {
    param([string]$Kind, [string]$Name)
    return [string](Invoke-AppCmd list $Kind $Name /text:state)
}

function Start-IisObject {
    param([string]$Kind, [string]$Name)
    if ((Get-IisState $Kind $Name) -ne 'Started') {
        Invoke-AppCmd start $Kind $Name | Out-Null
    }
}

# The identityType IIS uses for the account a pool runs as. No account means the pool's own identity.
function Get-PoolIdentityType {
    param([string]$Account)

    if ([string]::IsNullOrEmpty($Account)) { return 'ApplicationPoolIdentity' }
    if ($Account -eq 'LocalSystem' -or $Account -eq 'NT AUTHORITY\SYSTEM') { return 'LocalSystem' }
    if ($Account -eq 'NT AUTHORITY\NetworkService' -or $Account -eq 'NT AUTHORITY\NETWORK SERVICE') { return 'NetworkService' }
    if ($Account -eq 'NT AUTHORITY\LocalService' -or $Account -eq 'NT AUTHORITY\LOCAL SERVICE') { return 'LocalService' }
    return 'SpecificUser'
}

# The account that file permissions are granted to for a pool, or nothing for LocalSystem, which needs none.
function Get-PoolFileAccount {
    param($Definition)

    switch (Get-PoolIdentityType $Definition.account) {
        'ApplicationPoolIdentity' { return "IIS AppPool\$($Definition.appPoolName)" }
        'NetworkService' { return 'NT AUTHORITY\NETWORK SERVICE' }
        'LocalService' { return 'NT AUTHORITY\LOCAL SERVICE' }
        'SpecificUser' { return $Definition.account }
    }
}

# The account that file permissions are granted to for an existing pool, given its APPPOOL element from appcmd, or
# nothing for LocalSystem. IIS leaves identityType out when it's the default, the pool's own identity.
function Get-ExistingPoolFileAccount {
    param($Pool)

    $processModel = $Pool.SelectSingleNode('add/processModel')
    $identityType = 'ApplicationPoolIdentity'
    if ($processModel -and $processModel.GetAttribute('identityType')) {
        $identityType = $processModel.GetAttribute('identityType')
    }

    $accounts = @{
        ApplicationPoolIdentity = $null; LocalSystem = 'LocalSystem'
        NetworkService = 'NT AUTHORITY\NetworkService'; LocalService = 'NT AUTHORITY\LocalService'
    }
    $account = $accounts[$identityType]
    if ($identityType -eq 'SpecificUser') {
        $account = $processModel.GetAttribute('userName')
    }
    return Get-PoolFileAccount ([pscustomobject]@{ account = $account; appPoolName = $Pool.GetAttribute('APPPOOL.NAME') })
}

# The user account an existing pool runs as, or nothing when it doesn't exist or uses a built-in identity.
function Get-PoolUserName {
    param([string]$Name)

    $pool = Get-IisAppPool $Name
    if ($pool) {
        $processModel = $pool.SelectSingleNode('add/processModel')
        if ($processModel -and $processModel.GetAttribute('identityType') -eq 'SpecificUser') {
            return $processModel.GetAttribute('userName')
        }
    }
}

# Creates or replaces the pool. Settings this doesn't manage, such as recycling, are kept, because the new pool starts
# from the old one's configuration.
function Set-IisAppPool {
    param($Definition, [string[]]$Environment, [pscredential]$Credential, [string]$WorkFolder)

    $name = $Definition.appPoolName
    $existing = Get-IisAppPool $name
    $document = New-Object Xml.XmlDocument
    [void]$document.AppendChild($document.CreateXmlDeclaration('1.0', 'UTF-8', $null))
    $root = $document.AppendChild($document.CreateElement('appcmd'))
    $poolElement = $root.AppendChild($document.CreateElement('APPPOOL'))
    $poolElement.SetAttribute('APPPOOL.NAME', $name)
    if ($existing) {
        $add = $poolElement.AppendChild($document.ImportNode($existing.SelectSingleNode('add'), $true))
    }
    else {
        $add = $poolElement.AppendChild($document.CreateElement('add'))
        $add.SetAttribute('name', $name)
    }

    # ASP.NET Core runs in the ASP.NET Core Module, not the .NET Framework CLR.
    $add.SetAttribute('managedRuntimeVersion', '')
    $add.SetAttribute('managedPipelineMode', 'Integrated')
    $add.SetAttribute('autoStart', 'true')
    $processModel = $add.SelectSingleNode('processModel')
    if (-not $processModel) {
        $processModel = $add.AppendChild($document.CreateElement('processModel'))
    }

    if ($Definition.alwaysRunning) {
        $add.SetAttribute('startMode', 'AlwaysRunning')
        $processModel.SetAttribute('idleTimeout', '00:00:00')
    }
    else {
        $add.SetAttribute('startMode', 'OnDemand')
        $processModel.RemoveAttribute('idleTimeout')
    }

    $identityType = Get-PoolIdentityType $Definition.account
    $processModel.SetAttribute('identityType', $identityType)
    if ($identityType -eq 'SpecificUser') {
        $processModel.SetAttribute('userName', $Definition.account)
        if ($Credential) {
            $processModel.SetAttribute('password', $Credential.GetNetworkCredential().Password)
        }
        elseif ($Definition.account.EndsWith('$')) {
            # A group managed service account has no password to give.
            $processModel.SetAttribute('password', '')
        }
    }
    else {
        $processModel.RemoveAttribute('userName')
        $processModel.RemoveAttribute('password')
    }

    $variables = $add.SelectSingleNode('environmentVariables')
    if ($variables) {
        [void]$add.RemoveChild($variables)
    }
    if ($Environment.Count -gt 0) {
        $variables = $add.AppendChild($document.CreateElement('environmentVariables'))
        foreach ($entry in $Environment) {
            $separator = $entry.IndexOf('=')
            $variable = $variables.AppendChild($document.CreateElement('add'))
            $variable.SetAttribute('name', $entry.Substring(0, $separator))
            $variable.SetAttribute('value', $entry.Substring($separator + 1))
        }
    }

    # The file holds the settings and can hold the password, so it goes in the run's backup folder, which only
    # administrators can read, and is deleted straight after.
    $path = Join-Path $WorkFolder 'apppool-new.xml'
    $document.Save($path)
    try {
        if ($existing) {
            Invoke-AppCmd delete apppool $name | Out-Null
        }
        Import-IisObject 'apppool' $path
    }
    finally {
        Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
    }
}

# The bindingInformation IIS stores for a binding: address:port:host name.
function Get-BindingInformation {
    param($Binding)

    $address = $Binding.ipAddress
    if ($address -ne '*' -and $address.Contains(':')) {
        $address = "[$address]"
    }
    return "${address}:$($Binding.port):$($Binding.hostName)"
}

# Splits a bindingInformation value into its address, port and host name.
function Split-BindingInformation {
    param([string]$Information)

    if ($Information -match '^(\[[^\]]+\]|[^:]*):(\d+):(.*)$') {
        return [pscustomobject]@{ Address = $Matches[1].Trim('[', ']'); Port = [int]$Matches[2]; HostName = $Matches[3] }
    }
}

function Get-SiteBindings {
    param($Site)

    if (-not $Site) {
        return @()
    }
    return @($Site.SelectNodes('site/bindings/binding') | ForEach-Object {
        $sslFlags = 0
        if ($_.GetAttribute('sslFlags')) {
            $sslFlags = [int]$_.GetAttribute('sslFlags')
        }
        [pscustomobject]@{ Protocol = $_.GetAttribute('protocol'); Information = $_.GetAttribute('bindingInformation'); SslFlags = $sslFlags }
    })
}

# Returns the HTTP and HTTPS bindings to add to and remove from the site so they match the manifest. Bindings for
# other protocols, which an AppHost can't describe, are left alone.
function Compare-SiteBindings {
    param($Definition, $Site)

    $wanted = @($Definition.bindings | ForEach-Object {
        $sslFlags = 0
        if ($_.sni) {
            $sslFlags = 1
        }
        [pscustomobject]@{ Protocol = $_.protocol; Information = Get-BindingInformation $_; SslFlags = $sslFlags }
    })
    $current = @(Get-SiteBindings $Site | Where-Object { $_.Protocol -eq 'http' -or $_.Protocol -eq 'https' })

    $key = { param($binding) "$($binding.Protocol)|$($binding.Information)|$($binding.SslFlags)".ToLowerInvariant() }
    $wantedKeys = @($wanted | ForEach-Object { & $key $_ })
    $currentKeys = @($current | ForEach-Object { & $key $_ })
    return [pscustomobject]@{
        Add = @($wanted | Where-Object { $currentKeys -notcontains (& $key $_) })
        Remove = @($current | Where-Object { $wantedKeys -notcontains (& $key $_) })
    }
}

function Sync-SiteBindings {
    param($Definition, $Site)

    $name = $Definition.siteName
    $changes = Compare-SiteBindings $Definition $Site
    foreach ($binding in $changes.Remove) {
        Invoke-AppCmd set site "/site.name:$name" "/-bindings.[protocol='$($binding.Protocol)',bindingInformation='$($binding.Information)']" | Out-Null
    }
    foreach ($binding in $changes.Add) {
        Invoke-AppCmd set site "/site.name:$name" "/+bindings.[protocol='$($binding.Protocol)',bindingInformation='$($binding.Information)',sslFlags='$($binding.SslFlags)']" | Out-Null
    }

    # Removing an HTTPS binding from IIS leaves its certificate binding in HTTP.sys, so remove that too, unless a binding
    # of any site, this one included, still uses the endpoint. A certificate binding with another application ID belongs
    # to a program other than IIS, which may still listen there.
    $removed = @($changes.Remove | Where-Object { $_.Protocol -eq 'https' } | ForEach-Object { Get-ListedBindingEndpoint $_ })
    if ($removed.Count -gt 0) {
        $inUse = @(Get-IisObjects 'site' | ForEach-Object { Get-SiteBindings $_ } | Where-Object { $_.Protocol -eq 'https' } |
            ForEach-Object { (Get-ListedBindingEndpoint $_).Endpoint })
        foreach ($endpoint in $removed | Where-Object { $inUse -notcontains $_.Endpoint }) {
            $current = Get-SslCertificateBinding $endpoint
            if ($current -and $current.AppId -eq $script:IisAppId) {
                Remove-SslCertificateBinding $endpoint
            }
        }
    }
}

# The HTTP.sys endpoint of an HTTPS binding as Get-SiteBindings lists it.
function Get-ListedBindingEndpoint {
    param($Listed)

    $parts = Split-BindingInformation $Listed.Information
    return Get-SslEndpoint ([pscustomobject]@{
        ipAddress = $parts.Address; port = $parts.Port; hostName = $parts.HostName; sni = ($Listed.SslFlags -band 1) -eq 1
    })
}

function Format-Binding {
    param($Binding)

    $text = "$($Binding.protocol)://"
    if ($Binding.hostName) { $text += $Binding.hostName } else { $text += '*' }
    $text += ":$($Binding.port)"
    if ($Binding.ipAddress -ne '*') {
        $text += " on $($Binding.ipAddress)"
    }
    return $text
}

# Returns the certificate an HTTPS binding names: the one with its thumbprint, or the newest with its subject. Either
# way it must have a private key and be valid today.
function Find-BindingCertificate {
    param($Binding)

    # The store is opened through .NET rather than the Cert: drive, which is missing when Windows PowerShell starts
    # from PowerShell 7 and can't load its own modules.
    $store = New-Object Security.Cryptography.X509Certificates.X509Store($Binding.certificateStore, 'LocalMachine')
    $store.Open('ReadOnly')
    try {
        $certificates = @($store.Certificates)
    }
    finally {
        $store.Close()
    }

    $now = Get-Date
    $valid = @($certificates | Where-Object { $_.HasPrivateKey -and $_.NotBefore -le $now -and $_.NotAfter -gt $now })
    if ($Binding.certificateThumbprint) {
        $certificate = $valid | Where-Object { $_.Thumbprint -eq $Binding.certificateThumbprint } | Select-Object -First 1
        $description = "with the thumbprint $($Binding.certificateThumbprint)"
    }
    else {
        # Windows writes a subject with a space after each comma; people don't always.
        $subject = ($Binding.certificateSubject -replace ',\s*', ', ').Trim()
        $certificate = $valid | Where-Object { ($_.Subject -replace ',\s*', ', ') -eq $subject } |
            Sort-Object NotBefore -Descending | Select-Object -First 1
        $description = "with the subject '$($Binding.certificateSubject)'"
    }

    if (-not $certificate) {
        throw "No certificate in LocalMachine\$($Binding.certificateStore) $description has a private key and is valid today. It's needed for $(Format-Binding $Binding)."
    }
    return $certificate
}

# Where HTTP.sys keeps the certificate for an HTTPS binding: by host name and port for SNI, otherwise by address and
# port.
function Get-SslEndpoint {
    param($Binding)

    if ($Binding.sni) {
        return [pscustomobject]@{ Kind = 'hostnameport'; Endpoint = "$($Binding.hostName):$($Binding.port)" }
    }

    $address = $Binding.ipAddress
    if ($address -eq '*') {
        $address = '0.0.0.0'
    }
    elseif ($address.Contains(':')) {
        $address = "[$address]"
    }
    return [pscustomobject]@{ Kind = 'ipport'; Endpoint = "${address}:$($Binding.port)" }
}

# Returns the certificate HTTP.sys has for the endpoint, or nothing. netsh.exe translates its labels into the
# server's language, so the values are found by their form and order: the hash is the only 40 hexadecimal digits,
# the application ID the only GUID, and the store name comes right after it.
function Get-SslCertificateBinding {
    param($Endpoint)

    $output = @(& netsh.exe http show sslcert "$($Endpoint.Kind)=$($Endpoint.Endpoint)")
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    $values = @($output | Where-Object { $_ -match '\s:\s' } | ForEach-Object { ($_ -split '\s:\s', 2)[1].Trim() })
    $hash = $values | Where-Object { $_ -match '^[0-9A-Fa-f]{40}$' } | Select-Object -First 1
    $appIdIndex = [array]::FindIndex([string[]]$values, [Predicate[string]] { param($value) $value -match '^\{[0-9A-Fa-f-]{36}\}$' })
    if (-not $hash -or $appIdIndex -lt 0 -or $appIdIndex + 1 -ge $values.Count) {
        throw "netsh.exe showed the certificate of $($Endpoint.Endpoint) in a form the installer doesn't recognize:`n$($output -join "`n")"
    }

    return [pscustomobject]@{ CertificateHash = $hash.ToUpperInvariant(); AppId = $values[$appIdIndex]; Store = $values[$appIdIndex + 1] }
}

function Remove-SslCertificateBinding {
    param($Endpoint)

    if (Get-SslCertificateBinding $Endpoint) {
        & netsh.exe http delete sslcert "$($Endpoint.Kind)=$($Endpoint.Endpoint)" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Removing the certificate from $($Endpoint.Endpoint) failed (netsh exit code $LASTEXITCODE)."
        }
    }
}

function Set-SslCertificateBinding {
    param($Endpoint, [string]$CertificateHash, [string]$Store, [string]$AppId = $script:IisAppId)

    $current = Get-SslCertificateBinding $Endpoint
    if ($current -and $current.CertificateHash -eq $CertificateHash -and $current.Store -eq $Store) {
        return
    }

    Remove-SslCertificateBinding $Endpoint
    & netsh.exe http add sslcert "$($Endpoint.Kind)=$($Endpoint.Endpoint)" "certhash=$CertificateHash" "appid=$AppId" "certstorename=$Store" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Giving $($Endpoint.Endpoint) the certificate $CertificateHash failed (netsh exit code $LASTEXITCODE)."
    }
}

# Checks what the sites in the package need from the server, so a missing piece stops the install before anything
# has changed.
function Assert-IisReady {
    param($Manifest)

    $sites = @($Manifest.resources | Where-Object { $_.kind -eq 'iisSite' })
    if ($sites.Count -eq 0) {
        return
    }

    $version = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\InetStp' -ErrorAction SilentlyContinue).MajorVersion
    if (-not (Test-Path -LiteralPath $script:AppCmd) -or $version -lt 10) {
        throw 'The package has IIS sites, which need IIS 10 or later (Windows Server 2016 or later) installed.'
    }

    if (-not (Test-Path -LiteralPath "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")) {
        throw 'The package has IIS sites, which need the ASP.NET Core Module. Install the .NET Hosting Bundle, then run the installer again.'
    }

    $alwaysRunning = @($sites | Where-Object { $_.iisSite.alwaysRunning } | ForEach-Object { $_.iisSite.siteName })
    if ($alwaysRunning.Count -gt 0 -and -not (Test-Path -LiteralPath "$env:windir\System32\inetsrv\warmup.dll")) {
        throw ("These sites keep running without waiting for a request, which needs the IIS Application Initialization feature: " +
            ($alwaysRunning -join ', ') + ". The installer doesn't add Windows features, because adding one can restart every site on the server.")
    }

    foreach ($site in $sites) {
        foreach ($binding in @($site.iisSite.bindings | Where-Object { $_.protocol -eq 'https' })) {
            Find-BindingCertificate $binding | Out-Null

            # A certificate binding with any other application ID belongs to a program other than IIS. If the installer
            # replaced it, that program would start serving the site's certificate.
            $endpoint = Get-SslEndpoint $binding
            $current = Get-SslCertificateBinding $endpoint
            if ($current -and $current.AppId -ne $script:IisAppId) {
                throw ("A program other than IIS (application ID $($current.AppId)) has a certificate on $($endpoint.Endpoint), which the site " +
                    "'$($site.iisSite.siteName)' needs for $(Format-Binding $binding). In the AppHost, give the binding another port, or a host name if it has none.")
            }
        }
    }

    $ours = @($sites | ForEach-Object { $_.iisSite.siteName })
    foreach ($other in Get-IisObjects 'site' | Where-Object { $ours -notcontains $_.GetAttribute('SITE.NAME') }) {
        $otherName = $other.GetAttribute('SITE.NAME')
        foreach ($existing in Get-SiteBindings $other | Where-Object { $_.Protocol -eq 'http' -or $_.Protocol -eq 'https' }) {
            $parts = Split-BindingInformation $existing.Information
            foreach ($site in $sites) {
                foreach ($binding in $site.iisSite.bindings) {
                    $addressesOverlap = $parts.Address -eq '*' -or $binding.ipAddress -eq '*' -or $parts.Address -eq $binding.ipAddress
                    if ($parts.Port -ne $binding.port -or -not $addressesOverlap) {
                        continue
                    }

                    if ($parts.HostName -eq [string]$binding.hostName) {
                        throw "The site '$otherName' already answers on $(Format-Binding $binding), which the site '$($site.iisSite.siteName)' needs."
                    }

                    # Without SNI, HTTP.sys has one certificate per address and port, shared by every site there.
                    if ($binding.protocol -eq 'https' -and -not $binding.sni -and $existing.Protocol -eq 'https' -and ($existing.SslFlags -band 1) -eq 0) {
                        throw "The site '$otherName' has an HTTPS binding on port $($binding.port) without SNI, so it shares the certificate the site '$($site.iisSite.siteName)' would change. Give the HTTPS binding of '$($site.iisSite.siteName)' a host name in the AppHost, so it uses SNI."
                    }
                }
            }
        }
    }

    # The installer replaces a pool's settings and account, which would change every application in it.
    $apps = @(Get-AppCmdXml @('list', 'app', '/xml') 'APP')
    foreach ($site in $sites) {
        $pool = $site.iisSite.appPoolName
        foreach ($app in $apps | Where-Object { $_.GetAttribute('APPPOOL.NAME') -eq $pool }) {
            $appName = $app.GetAttribute('APP.NAME')
            if ($ours -notcontains $appName.Split('/')[0]) {
                throw "The application pool '$pool' also runs '$appName', which isn't in the package. Give the site '$($site.iisSite.siteName)' a pool of its own."
            }
        }
    }
}

# Asks the site for its health check path, or for / when it has none, until it answers or 30 seconds pass. With a
# health check path the answer must be a success status; without one, any status below 500 shows the app started.
#
# The request goes to the server's own address and names the site's host in the Host header, so it needs no DNS
# entry. It uses an HTTPS binding when the site has one, because an app that redirects HTTP to HTTPS would answer an
# HTTP request with a redirect. It accepts the certificate the installer bound and no other, instead of validating it
# the usual way: the request never leaves the server, and its URL names an address, which the certificate normally
# doesn't.
function Test-SiteHealth {
    param($Definition)

    $binding = @($Definition.bindings | Where-Object { $_.protocol -eq 'https' })[0]
    if (-not $binding) {
        $binding = @($Definition.bindings)[0]
    }

    $path = $Definition.healthCheckPath
    if (-not $path) {
        $path = '/'
    }

    $address = $binding.ipAddress
    if ($address -eq '*') {
        $address = '127.0.0.1'
    }
    elseif ($address.Contains(':')) {
        $address = "[$address]"
    }
    $url = "$($binding.protocol)://${address}:$($binding.port)$path"

    # A wildcard host name matches any name that ends the same way, such as health-check.contoso.local for
    # *.contoso.local. With SNI, the Host header also picks the certificate.
    $hostHeader = $null
    if ($binding.hostName) {
        $hostHeader = ($binding.hostName -replace '^\*', 'health-check') + ":$($binding.port)"
    }

    $thumbprint = $null
    if ($binding.protocol -eq 'https') {
        $thumbprint = (Find-BindingCertificate $binding).Thumbprint
    }

    if ([Net.ServicePointManager]::SecurityProtocol -ne [Net.SecurityProtocolType]::SystemDefault) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    $deadline = (Get-Date).AddSeconds(30)
    $last = 'no answer'
    do {
        $status = $null
        $request = [Net.HttpWebRequest]::Create($url)
        $request.Timeout = 10000
        $request.AllowAutoRedirect = $false
        if ($hostHeader) {
            $request.Host = $hostHeader
        }
        if ($thumbprint) {
            $request.ServerCertificateValidationCallback = {
                param($sender, $certificate)
                $certificate.GetCertHashString() -eq $thumbprint
            }.GetNewClosure()
        }

        try {
            $response = $request.GetResponse()
            $status = [int]$response.StatusCode
            $response.Close()
        }
        catch {
            $exception = $_.Exception
            if ($exception.InnerException) {
                $exception = $exception.InnerException
            }
            if ($exception.Response) {
                $status = [int]$exception.Response.StatusCode
                $exception.Response.Close()
            }
            else {
                $last = $exception.Message
            }
        }

        if ($null -ne $status) {
            $last = "status $status"
            if (($Definition.healthCheckPath -and $status -ge 200 -and $status -lt 300) -or (-not $Definition.healthCheckPath -and $status -lt 500)) {
                return
            }
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    throw "The site '$($Definition.siteName)' didn't answer $url as expected within 30 seconds (last: $last)."
}

# Waits for the pool's worker processes to exit, so they no longer lock the site's files.
function Wait-AppPoolIdle {
    param([string]$Name)

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $processes = @(& $script:AppCmd list wp "/apppool.name:$Name")
        if ($LASTEXITCODE -ne 0 -or $processes.Count -eq 0) {
            return
        }
        Start-Sleep -Seconds 1
    }
}

# Takes a backup of the whole IIS configuration, named after the run, for an administrator to restore by hand if
# needed. Older ones the installer made are removed, keeping KeepBackups.
function New-IisConfigurationBackup {
    param([string]$RunId, [int]$KeepBackups)

    $name = "aspirin-$RunId"
    Invoke-AppCmd add backup $name | Out-Null

    Get-AppCmdXml @('list', 'backup', '/xml') 'BACKUP' | ForEach-Object { $_.GetAttribute('BACKUP.NAME') } |
        Where-Object { $_ -like 'aspirin-*' } | Sort-Object -Descending | Select-Object -Skip $KeepBackups |
        ForEach-Object { Invoke-AppCmd delete backup $_ | Out-Null }
    return $name
}
