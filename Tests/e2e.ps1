param(
    [string]$BaseUrl = $(if ($env:BASE_URL) { $env:BASE_URL } else { 'http://localhost:5270' }),
    [string]$AdminKey = $env:ADMIN_API_KEY,
    [string]$CollectionPattern = 'public.osm*',
    [switch]$SkipCertificateCheck
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
    if ($SkipCertificateCheck) { $request.SkipCertificateCheck = $true }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }
    Invoke-WebRequest @request
}

function Json($Response) {
    $content = $Response.Content
    if ($content -is [byte[]]) { $content = [System.Text.Encoding]::UTF8.GetString($content) }
    if ([string]::IsNullOrWhiteSpace($content)) { return $null }
    $content | ConvertFrom-Json
}

try {
    $landing = Request GET '/'
    Assert-True ($landing.StatusCode -eq 200) 'OGC landing page is public'
    Assert-True (@((Json $landing).links | Where-Object rel -eq 'data').Count -eq 1) 'landing links to collections'
    $conformance = Request GET '/conformance'
    Assert-True ((Json $conformance).conformsTo -contains 'http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core') 'OGC conformance is declared'
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
        collectionsAllow = @($CollectionPattern)
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
        -not ("$($_.schema).$($_.name)" -like $CollectionPattern)
    })
    Assert-True ($forbiddenCollection.Count -eq 0) 'metadata contains only role-allowed collections'
    Assert-True ($metadataBody.functions.Count -eq 0) 'metadata hides functions not allowed by the role'

    $allowed = @($metadataBody.collections | Sort-Object isSpatial -Descending)[0]
    Assert-True ($null -ne $allowed) 'role has at least one allowed collection'
    $collectionId = "$($allowed.schema).$($allowed.name)"

    $collections = Request GET '/collections?f=json' $null $apiHeaders
    Assert-True ($collections.StatusCode -eq 200) 'OGC collections can be discovered'
    Assert-True (@((Json $collections).collections | Where-Object id -eq $collectionId).Count -eq 1) 'authorized collection appears in discovery'
    $description = Request GET "/collections/$collectionId" $null $apiHeaders
    Assert-True ((Json $description).itemType -eq 'feature') 'collection advertises feature items'
    $definition = Request GET '/api' $null $apiHeaders
    Assert-True ($definition.StatusCode -eq 200) 'dynamic OpenAPI responds'
    Assert-True ((Json $definition).paths.PSObject.Properties.Name -contains "/collections/$collectionId/items") 'OpenAPI contains the concrete collection path'

    $queryables = Request GET "/collections/$collectionId/queryables" $null $apiHeaders
    Assert-True ($queryables.StatusCode -eq 200) 'allowed collection queryables can be read'

    $items = Request GET "/collections/$collectionId/items?limit=2" $null $apiHeaders
    Assert-True ($items.StatusCode -eq 200) 'allowed collection items can be read'
    Assert-True (($items.Headers.'Content-Type' | Select-Object -First 1) -match 'json') 'items response is JSON'
    $itemBody = Json $items
    Assert-True ($itemBody.type -eq 'FeatureCollection') 'collection response is GeoJSON'
    Assert-True ($itemBody.numberReturned -le 2) 'lookahead row is not returned'
    $next = @($itemBody.links | Where-Object rel -eq 'next')
    if ($next.Count) {
        $nextPath = ([Uri]$next[0].href).PathAndQuery
        $nextResponse = Request GET $nextPath $null $apiHeaders
        Assert-True ($nextResponse.StatusCode -eq 200) 'next link can be followed'
    }
    if ($allowed.isSpatial) {
        $geojson = Json $items
        Assert-True (($items.Headers.'Content-Type' | Select-Object -First 1) -match 'application/geo\+json') 'spatial items have GeoJSON content type'
        Assert-True ($geojson.type -eq 'FeatureCollection') 'spatial items are a FeatureCollection'
        Assert-True ($null -ne $geojson.features) 'GeoJSON contains features'
        foreach ($feature in $geojson.features) {
            Assert-True ($feature.type -eq 'Feature') 'GeoJSON members are Features'
            Assert-True ($feature.PSObject.Properties.Name -contains 'geometry') 'GeoJSON Feature has geometry'
            Assert-True ($null -ne $feature.properties) 'GeoJSON Feature has properties'
        }
    }

    if ($allowed.primaryKey -and $itemBody.features.Count -gt 0) {
        $id = $itemBody.features[0].id
        $role.rowRules = @(@{ pattern = $collectionId; conditions = @(@{ column = $allowed.primaryKey; operator = 'eq'; values = @([string]$id) }) })
        $restricted = Request PUT "/auth/roles/$roleName" $role $adminHeaders $session
        Assert-True ($restricted.StatusCode -eq 204) 'row rule is saved through management API'
        $oneRow = Json (Request GET "/collections/$collectionId/items?limit=2&count=true" $null $apiHeaders)
        Assert-True ($oneRow.numberReturned -eq 1 -and $oneRow.numberMatched -eq 1) 'row policy limits results and count'
        Assert-True (@($oneRow.links | Where-Object rel -eq 'next').Count -eq 0) 'row policy prevents unauthorized next pages'
        $single = Json (Request GET "/collections/$collectionId/items/$id" $null $apiHeaders)
        Assert-True ($single.type -eq 'Feature') 'single item is a Feature rather than a collection'
        if ($allowed.isSpatial) {
            $analysis = Request POST "/collections/$collectionId/analysis/centroid?limit=2" @{} $apiHeaders
            Assert-True ($analysis.StatusCode -eq 200 -and (Json $analysis).numberReturned -eq 1) 'spatial analysis applies the row policy'
        }
    }

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
