param(
    [string]$BaseUrl = $(if ($env:BASE_URL) { $env:BASE_URL } else { 'http://localhost:5270' }),
    [string]$AdminKey = $env:ADMIN_API_KEY
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($AdminKey)) {
    throw 'ADMIN_API_KEY environment variable is required.'
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$roleName = 'e2e_' + [Guid]::NewGuid().ToString('N').Substring(0, 10)
$createdKeyId = $null
$csrf = $null
$passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
    $script:passed++
    Write-Host "PASS $Message" -ForegroundColor Green
}

function Request(
    [string]$Method,
    [string]$Path,
    $Body = $null,
    [hashtable]$Headers = @{},
    $WebSession = $null
) {
    $request = @{
        Uri = $BaseUrl.TrimEnd('/') + $Path
        Method = $Method
        Headers = $Headers
        SkipHttpErrorCheck = $true
        TimeoutSec = 20
    }
    if ($null -ne $WebSession) { $request.WebSession = $WebSession }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }
    Invoke-WebRequest @request
}

function Json($Response) {
    if ([string]::IsNullOrWhiteSpace($Response.Content)) { return $null }
    $Response.Content | ConvertFrom-Json
}

try {
    $anonymous = Request GET '/metadata'
    Assert-True ($anonymous.StatusCode -eq 401) 'anonymous metadata is rejected'

    $login = Request POST '/admin/session' @{ apiKey = $AdminKey } @{} $session
    Assert-True ($login.StatusCode -eq 200) 'admin login succeeds'
    $loginBody = Json $login
    $csrf = $loginBody.csrfToken
    Assert-True (-not [string]::IsNullOrWhiteSpace($csrf)) 'login returns a CSRF token'
    Assert-True ($session.Cookies.Count -gt 0) 'login creates an admin session cookie'

    $config = Request GET '/admin/config' $null @{} $session
    Assert-True ($config.StatusCode -eq 200) 'admin config can be read'
    Assert-True (-not ($config.Content -match '"password"\s*:')) 'admin config does not expose database password'

    $lookup = Request GET '/admin/config/lookup' $null @{} $session
    Assert-True ($lookup.StatusCode -eq 200) 'metadata lookup is available'
    $lookupBody = Json $lookup
    Assert-True ($lookupBody.collections.Count -gt 0) 'lookup contains collections'

    $listener = Request GET '/admin/config/metadata-listener' $null @{} $session
    Assert-True ($listener.StatusCode -eq 200) 'metadata listener status is available'

    $noCsrf = Request POST '/admin/config/metadata-refresh' $null @{} $session
    Assert-True ($noCsrf.StatusCode -eq 401) 'admin mutation without CSRF is rejected'

    $adminHeaders = @{ 'X-PgDataHub-CSRF' = $csrf }
    $refresh = Request POST '/admin/config/metadata-refresh' $null $adminHeaders $session
    Assert-True ($refresh.StatusCode -eq 204) 'metadata refresh succeeds with CSRF'

    $role = @{
        description = 'Temporary end-to-end test role'
        collectionsAllow = @('public.osm*')
        collectionsDeny = @()
        functionsAllow = @()
        functionsDeny = @()
        columnRules = @()
    }
    $roleResult = Request PUT "/auth/roles/$roleName" $role $adminHeaders $session
    Assert-True ($roleResult.StatusCode -eq 204) 'temporary role is created'

    $keyResult = Request POST '/auth/apikeys' @{
        name = 'E2E temporary key'
        owner = 'automated-test'
        description = 'Automatically revoked after the test'
        roleName = $roleName
        expiresAt = [DateTimeOffset]::UtcNow.AddMinutes(10).ToString('O')
    } $adminHeaders $session
    Assert-True ($keyResult.StatusCode -eq 201) 'temporary API key is created'
    $keyBody = Json $keyResult
    $createdKeyId = $keyBody.id
    $rawKey = $keyBody.apiKey
    Assert-True ($rawKey.StartsWith('pgdh_')) 'raw API key is returned once'

    $apiHeaders = @{ 'X-API-Key' = $rawKey }
    $metadata = Request GET '/metadata' $null $apiHeaders
    Assert-True ($metadata.StatusCode -eq 200) 'role API key can read metadata'
    $metadataBody = Json $metadata
    $forbiddenCollection = @($metadataBody.collections | Where-Object {
        -not ("$($_.schema).$($_.name)" -like 'public.osm*')
    })
    Assert-True ($forbiddenCollection.Count -eq 0) 'metadata contains only role-allowed collections'
    Assert-True ($metadataBody.functions.Count -eq 0) 'metadata hides functions not allowed by the role'

    $allowed = @($metadataBody.collections)[0]
    Assert-True ($null -ne $allowed) 'role has at least one allowed collection'
    $collectionId = "$($allowed.schema).$($allowed.name)"

    $queryables = Request GET "/collections/$collectionId/queryables" $null $apiHeaders
    Assert-True ($queryables.StatusCode -eq 200) 'allowed collection queryables can be read'

    $items = Request GET "/collections/$collectionId/items?limit=2" $null $apiHeaders
    Assert-True ($items.StatusCode -eq 200) 'allowed collection items can be read'
    Assert-True (($items.Headers.'Content-Type' | Select-Object -First 1) -match 'json') 'items response is JSON'

    $invalid = Request GET "/collections/$collectionId/items?limit=invalid" $null $apiHeaders
    Assert-True ($invalid.StatusCode -eq 400) 'invalid query value returns 400'

    $functions = Request GET '/functions' $null $apiHeaders
    Assert-True ($functions.StatusCode -eq 200) 'function listing endpoint responds'
    Assert-True ((Json $functions).Count -eq 0) 'function listing respects the role'
}
finally {
    if ($csrf) {
        $headers = @{ 'X-PgDataHub-CSRF' = $csrf }
        if ($null -ne $createdKeyId) {
            try { [void](Request DELETE "/auth/apikeys/$createdKeyId" $null $headers $session) } catch {}
        }
        try { [void](Request DELETE "/auth/roles/$roleName" $null $headers $session) } catch {}
        try { [void](Request DELETE '/admin/session' $null $headers $session) } catch {}
    }
}

Write-Host "E2E COMPLETE: $passed assertions passed." -ForegroundColor Cyan
