[CmdletBinding()]
param(
    [switch]$KeepStack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot

$projectName = "blackbox-e2e-$([guid]::NewGuid().ToString('N').Substring(0, 10))"
$composeStarted = $false
$envPath = Join-Path $repoRoot '.env'
$envLines = [System.Collections.Generic.List[string]]::new()
$envChanged = $false

function New-HexSecret {
    $bytes = New-Object byte[] 32
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
    }
    finally {
        $random.Dispose()
    }

    return [System.BitConverter]::ToString($bytes).Replace('-', '')
}

function Get-EnvValue([string]$name) {
    foreach ($line in $script:envLines) {
        if ($line -match ('^' + [regex]::Escape($name) + '=(.*)$')) {
            return $Matches[1].Trim().Trim('"').Trim("'")
        }
    }

    return $null
}

function Set-EnvValue([string]$name, [string]$value) {
    for ($index = 0; $index -lt $script:envLines.Count; $index++) {
        if ($script:envLines[$index] -match ('^' + [regex]::Escape($name) + '=')) {
            $script:envLines[$index] = "$name=$value"
            $script:envChanged = $true
            return
        }
    }

    $script:envLines.Add("$name=$value")
    $script:envChanged = $true
}

try {
    & git check-ignore --quiet .env
    if ($LASTEXITCODE -ne 0) {
        throw '.env must be ignored by Git before local secrets can be written.'
    }

    if (-not (Test-Path -LiteralPath $envPath)) {
        Copy-Item -LiteralPath (Join-Path $repoRoot '.env.example') -Destination $envPath
    }

    foreach ($line in [System.IO.File]::ReadAllLines($envPath)) {
        $envLines.Add($line)
    }

    $apiKey = Get-EnvValue 'BLACKBOX_API_KEY'
    if ([string]::IsNullOrWhiteSpace($apiKey) -or $apiKey -match '^(replace|change|your-)' ) {
        $apiKey = New-HexSecret
        Set-EnvValue 'BLACKBOX_API_KEY' $apiKey
    }
    elseif ([System.Text.Encoding]::UTF8.GetByteCount($apiKey) -lt 32) {
        throw 'BLACKBOX_API_KEY must contain at least 32 UTF-8 bytes. Update the ignored local .env file.'
    }

    $databasePassword = Get-EnvValue 'BLACKBOX_POSTGRES_PASSWORD'
    if ([string]::IsNullOrWhiteSpace($databasePassword) -or $databasePassword -match '^(replace|change|your-)' ) {
        $databasePassword = New-HexSecret
        Set-EnvValue 'BLACKBOX_POSTGRES_PASSWORD' $databasePassword
    }

    if ($envChanged) {
        [System.IO.File]::WriteAllLines($envPath, $envLines, [System.Text.UTF8Encoding]::new($false))
        Write-Host 'Generated local development secrets in the Git-ignored .env file.'
    }

    # Match Docker Compose precedence: process environment overrides values in .env.
    # This keeps readiness checks and Playwright pointed at the same isolated stack
    # when a caller selects alternate host ports to avoid another local instance.
    $apiPort = $env:BLACKBOX_API_PORT
    if ([string]::IsNullOrWhiteSpace($apiPort)) {
        $apiPort = Get-EnvValue 'BLACKBOX_API_PORT'
    }
    if ([string]::IsNullOrWhiteSpace($apiPort)) {
        $apiPort = '5000'
    }
    if ($apiPort -notmatch '^\d{1,5}$' -or [int]$apiPort -lt 1 -or [int]$apiPort -gt 65535) {
        throw 'BLACKBOX_API_PORT in .env must be a valid TCP port.'
    }

    $nodeVersionText = (& node --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $nodeVersionText -notmatch '^v(\d+)') {
        throw 'Node.js 22 or later is required for the Playwright suite.'
    }
    if ([int]$Matches[1] -lt 22) {
        throw "Node.js 22 or later is required; found $nodeVersionText."
    }

    & docker info --format '{{.ServerVersion}}' | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Engine is unavailable. Start Docker Desktop and retry.'
    }

    Push-Location (Join-Path $repoRoot 'tests/blackbox-e2e')
    try {
        & npm ci
        if ($LASTEXITCODE -ne 0) {
            throw 'npm ci failed.'
        }

        & npx playwright install chromium
        if ($LASTEXITCODE -ne 0) {
            throw 'Playwright could not install Chromium.'
        }
    }
    finally {
        Pop-Location
    }

    $env:BLACKBOX_API_KEY = $apiKey
    $env:BLACKBOX_API_URL = "http://127.0.0.1:$apiPort"

    $composeStarted = $true
    & docker compose -p $projectName up --build --detach
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker Compose could not start the isolated Blackbox stack.'
    }

    $ready = $false
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Uri "$($env:BLACKBOX_API_URL)/health/ready" -TimeoutSec 3
            if ($health.status -eq 'Ready') {
                $ready = $true
                break
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }

    if (-not $ready) {
        throw 'Blackbox API did not become ready within three minutes. Inspect the isolated Compose logs.'
    }

    Push-Location (Join-Path $repoRoot 'tests/blackbox-e2e')
    try {
        & npm test
        if ($LASTEXITCODE -ne 0) {
            throw 'The Playwright suite failed. Use -KeepStack to retain the isolated services for diagnosis.'
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "E2E verification passed against $($env:BLACKBOX_API_URL)."
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if ($composeStarted) {
        if ($KeepStack) {
            Write-Host "Leaving isolated project '$projectName' running. API: $($env:BLACKBOX_API_URL)"
            Write-Host "Stop its containers without deleting the database with: docker compose -p $projectName down"
        }
        else {
            Write-Host "Removing only the disposable E2E stack and volume '$projectName'."
            & docker compose -p $projectName down --volumes --remove-orphans
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Cleanup failed. Inspect and remove only Compose project '$projectName' manually."
            }
        }
    }
}
