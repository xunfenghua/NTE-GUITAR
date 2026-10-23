param(
    [string]$BaseUrl = $(if ($env:NTE_GUITAR_PRESENCE_TEST_URL) { $env:NTE_GUITAR_PRESENCE_TEST_URL } else { 'http://127.0.0.1:2893' }),
    [string]$AdminToken = $env:NTE_GUITAR_ADMIN_TOKEN
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($AdminToken)) {
    throw 'Set NTE_GUITAR_ADMIN_TOKEN to a test-only token before running this smoke test.'
}

$http = [System.Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(5)
$baseUri = [Uri]::new($BaseUrl.TrimEnd('/') + '/')
$presencePath = 'api/client-presence/heartbeat'
$adminPath = 'api/admin/client-presence'
$sessionId = [Guid]::NewGuid().ToString()

function Send-JsonPost([string]$Path, [object]$Body) {
    $json = $Body | ConvertTo-Json -Compress
    $content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, 'application/json')
    try {
        return $http.PostAsync([Uri]::new($baseUri, $Path), $content).GetAwaiter().GetResult()
    }
    finally {
        $content.Dispose()
    }
}

try {
    $first = Send-JsonPost $presencePath @{ sessionId = $sessionId; version = 'smoke-1.0' }
    if ([int]$first.StatusCode -ne 204) { throw "Heartbeat returned $([int]$first.StatusCode); expected 204." }
    $first.Dispose()

    $duplicate = Send-JsonPost $presencePath @{ sessionId = $sessionId; version = 'smoke-1.0' }
    if ([int]$duplicate.StatusCode -ne 204) { throw "Repeated heartbeat returned $([int]$duplicate.StatusCode); expected 204." }
    $duplicate.Dispose()

    $changed = Send-JsonPost $presencePath @{ sessionId = $sessionId; version = 'smoke-2.0' }
    if ([int]$changed.StatusCode -ne 204) { throw "Version update returned $([int]$changed.StatusCode); expected 204." }
    $changed.Dispose()

    $invalid = Send-JsonPost $presencePath @{ sessionId = 'not-a-guid'; version = '' }
    if ([int]$invalid.StatusCode -ne 400) { throw "Invalid heartbeat returned $([int]$invalid.StatusCode); expected 400." }
    $invalid.Dispose()

    $unauthorizedRequest = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, [Uri]::new($baseUri, $adminPath))
    $unauthorized = $http.SendAsync($unauthorizedRequest).GetAwaiter().GetResult()
    if ([int]$unauthorized.StatusCode -ne 401) { throw "Unauthenticated stats returned $([int]$unauthorized.StatusCode); expected 401." }
    $unauthorized.Dispose()
    $unauthorizedRequest.Dispose()

    $authorizedRequest = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, [Uri]::new($baseUri, $adminPath))
    [void]$authorizedRequest.Headers.Add('X-Admin-Token', $AdminToken)
    $authorized = $http.SendAsync($authorizedRequest).GetAwaiter().GetResult()
    if (-not $authorized.IsSuccessStatusCode) { throw "Authorized stats returned $([int]$authorized.StatusCode); expected 2xx." }
    $stats = $authorized.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    $row = @($stats.versions | Where-Object { $_.version -eq 'smoke-2.0' })
    if ($row.Count -ne 1 -or $row[0].activeInstances -ne 1) { throw 'Session was not grouped once under its latest version.' }
    if (@($stats.versions | Where-Object { $_.version -eq 'smoke-1.0' }).Count -ne 0) { throw 'Session remained under its previous version.' }
    $authorized.Dispose()
    $authorizedRequest.Dispose()

    Write-Output '[PASS] heartbeat insert, idempotent update, version regrouping, payload validation, and admin authorization'
}
finally {
    $http.Dispose()
}
