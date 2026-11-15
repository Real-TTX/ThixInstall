<#
.SYNOPSIS
    Rebuilds the ThixInstall.Cloud image from this folder and (re)starts the local stack (docker-compose.local.yml): the way to test every change.

.PARAMETER Clean
    Also removes the volumes first: an empty database (the setup assistant comes again) and new session keys.

.EXAMPLE
    ./scripts/redeploy.ps1
    ./scripts/redeploy.ps1 -Clean
#>
param(
    [switch] $Clean
)

# Native commands (docker) write their progress to stderr; Windows PowerShell 5.1 turns that into errors under 'Stop'. The exit codes decide.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$compose = Join-Path $root 'docker-compose.local.yml'

Push-Location $root
try {
    if ($Clean) {
        docker compose -f $compose down --volumes --remove-orphans
        if ($LASTEXITCODE -ne 0) { throw 'docker compose down failed' }
    }

    $date = (Get-Date).ToUniversalTime().ToString('yyyyMMdd')
    docker compose -f $compose build --build-arg Channel=local --build-arg BuildNumber=0 --build-arg BuildDate=$date web
    if ($LASTEXITCODE -ne 0) { throw 'docker compose build failed' }

    docker compose -f $compose up -d --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed' }

    # wait until the app answers its health check (migrations run at start-up)
    $deadline = (Get-Date).AddSeconds(120)
    do {
        Start-Sleep -Seconds 2
        try { $ok = (Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 http://localhost:4955/health).StatusCode -eq 200 } catch { $ok = $false }
    } until ($ok -or (Get-Date) -gt $deadline)

    if (-not $ok) {
        docker compose -f $compose logs --tail 80 web
        throw 'ThixInstall.Cloud did not become healthy within 120 s'
    }

    Write-Host 'ThixInstall.Cloud runs: http://localhost:4955   (pgAdmin: http://localhost:4956, Postgres: localhost:4957)'
}
finally {
    Pop-Location
}
