<#
.SYNOPSIS
    Builds, provisions and deploys the weather images app to Azure.

.DESCRIPTION
    1. Checks the Azure CLI / dotnet CLI, signs in if needed, registers the resource providers.
    2. Publishes the Functions project with the dotnet CLI and zips it.
    3. Creates the resource group and deploys infra/main.bicep (storage + queues + tables + blob container,
       identity + role assignments, Application Insights, Flex Consumption function app).
    4. Deploys the zip to the function app with the Azure CLI.
    5. Smoke test: starts a small job on the live API and waits until the images are ready.

    Re-running the script updates the same resources (names are derived from the resource group).

.EXAMPLE
    ./deploy.ps1
    ./deploy.ps1 -ResourceGroup rg-weather-images -Location westeurope -SubscriptionId <id>
    ./deploy.ps1 -SkipBuild            # reuse the zip of the previous run
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-weather-images',

    # Must support Flex Consumption: az functionapp list-flexconsumption-locations
    [string] $Location = 'westeurope',

    # 3-9 lowercase letters/digits, used in resource names.
    [ValidatePattern('^[a-z0-9]{3,9}$')]
    [string] $NamePrefix = 'wximg',

    [string] $SubscriptionId,

    # API key for POST /api/jobs. Generated (and remembered in .deploy/api-key.txt) when not given.
    [string] $ApiKey,

    [switch] $SkipBuild,
    [switch] $SkipSmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root       = $PSScriptRoot
$project    = Join-Path $root 'src/WeatherImages.Functions/WeatherImages.Functions.csproj'
$bicepFile  = Join-Path $root 'infra/main.bicep'
$httpFile   = Join-Path $root 'http/weather-images.http'
$deployDir  = Join-Path $root '.deploy'       # git-ignored: zip, publish output, api key
$publishDir = Join-Path $deployDir 'publish'
$zipFile    = Join-Path $deployDir 'functionapp.zip'
$keyFile    = Join-Path $deployDir 'api-key.txt'
$paramsFile = Join-Path $deployDir 'parameters.json'

function Write-Step([string] $message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

# Runs a native command and stops the script when it fails (PowerShell does not do that by itself).
function Invoke-Native([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

function Get-HttpStatus([string] $uri, [string] $method = 'GET', [hashtable] $headers = @{}) {
    try {
        $r = Invoke-WebRequest -Uri $uri -Method $method -Headers $headers -UseBasicParsing -TimeoutSec 60
        return @{ Code = [int]$r.StatusCode; Body = $r.Content }
    }
    catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { return @{ Code = 0; Body = $_.Exception.Message } }
        $body = ''
        try { $body = $_.ErrorDetails.Message } catch { }
        return @{ Code = [int]$resp.StatusCode; Body = $body }
    }
}

# ------------------------------------------------------------------------------------------------
Write-Step 'Checking prerequisites'
# ------------------------------------------------------------------------------------------------
foreach ($tool in 'dotnet', 'az') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' was not found on the PATH. Install it first (dotnet SDK 10: https://dot.net, Azure CLI: https://aka.ms/installazurecli)."
    }
}

& az account show --only-show-errors -o none 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Not signed in to Azure - running az login...'
    Invoke-Native 'az login' { az login --only-show-errors -o none }
}
if ($SubscriptionId) {
    Invoke-Native 'az account set' { az account set --subscription $SubscriptionId --only-show-errors }
}
$account = az account show --only-show-errors -o json | ConvertFrom-Json
Write-Host "Subscription: $($account.name) ($($account.id))"

foreach ($ns in 'Microsoft.Web', 'Microsoft.Storage', 'Microsoft.Insights', 'Microsoft.OperationalInsights', 'Microsoft.ManagedIdentity') {
    $state = az provider show --namespace $ns --query registrationState -o tsv --only-show-errors
    if ($state -ne 'Registered') {
        Write-Host "Registering resource provider $ns (can take a minute)..."
        Invoke-Native "register $ns" { az provider register --namespace $ns --wait --only-show-errors }
    }
}

# The app runs on the Flex Consumption plan (the only serverless Linux plan that supports .NET 10).
$wantedLocation = ($Location -replace '\s', '').ToLowerInvariant()
$flexLocations = @(az functionapp list-flexconsumption-locations --query '[].name' -o tsv --only-show-errors) |
    ForEach-Object { ($_ -replace '\s', '').ToLowerInvariant() }
if ($flexLocations -and ($flexLocations -notcontains $wantedLocation)) {
    throw "Region '$Location' does not support Flex Consumption. Use one of: $($flexLocations -join ', ')"
}

# Ask Azure how it spells the .NET 10 runtime ('10.0' or '10') so the Bicep deployment cannot fail on that.
$dotnetVersion = @(az functionapp list-flexconsumption-runtimes --location $wantedLocation --runtime dotnet-isolated --query '[].version' -o tsv --only-show-errors) |
    Where-Object { $_ -match '^10(\.0)?$' } | Select-Object -First 1
if (-not $dotnetVersion) {
    throw "Flex Consumption in '$Location' does not offer .NET 10 yet. Pick another region."
}
Write-Host "Runtime: dotnet-isolated $dotnetVersion on Flex Consumption in $Location"

# ------------------------------------------------------------------------------------------------
Write-Step 'Preparing API key'
# ------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $deployDir | Out-Null
if (-not $ApiKey) {
    if (Test-Path $keyFile) {
        $ApiKey = (Get-Content $keyFile -Raw).Trim()
        Write-Host "Re-using the API key from $keyFile"
    }
    else {
        $bytes = New-Object byte[] 24
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($bytes)
        $rng.Dispose()
        $ApiKey = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        Write-Host "Generated a new API key (saved in $keyFile)"
    }
}
Set-Content -Path $keyFile -Value $ApiKey -NoNewline

# ------------------------------------------------------------------------------------------------
if ($SkipBuild -and (Test-Path $zipFile)) {
    Write-Step "Skipping build (re-using $zipFile)"
}
else {
    Write-Step 'Publishing with the dotnet CLI'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    if (Test-Path $zipFile)    { Remove-Item $zipFile -Force }

    Invoke-Native 'dotnet publish' { dotnet publish $project --configuration Release --output $publishDir --nologo }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipFile, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host ("Created {0} ({1:N1} MB)" -f $zipFile, ((Get-Item $zipFile).Length / 1MB))
}

# ------------------------------------------------------------------------------------------------
Write-Step "Creating resources in Azure ($ResourceGroup, $Location) with Bicep"
# ------------------------------------------------------------------------------------------------
Invoke-Native 'az group create' { az group create --name $ResourceGroup --location $Location --only-show-errors -o none }

# Parameters go through a file so the API key never shows up in the process list or shell history.
$parameters = [ordered]@{
    '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters     = [ordered]@{
        location      = @{ value = $Location }
        namePrefix    = @{ value = $NamePrefix }
        apiKey        = @{ value = $ApiKey }
        dotnetVersion = @{ value = $dotnetVersion }
    }
}
$parameters | ConvertTo-Json -Depth 5 | Set-Content -Path $paramsFile -Encoding UTF8

try {
    $raw = az deployment group create `
        --resource-group $ResourceGroup `
        --name "weather-images-$(Get-Date -Format 'yyyyMMddHHmmss')" `
        --template-file $bicepFile `
        --parameters "@$paramsFile" `
        --query properties.outputs -o json --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "az deployment group create failed (exit code $LASTEXITCODE)." }
}
finally {
    Remove-Item $paramsFile -Force -ErrorAction SilentlyContinue
}

$outputs = ($raw -join "`n") | ConvertFrom-Json
$functionApp = $outputs.functionAppName.value
$apiBaseUrl  = $outputs.apiBaseUrl.value
Write-Host "Function app : $functionApp"
Write-Host "Storage      : $($outputs.storageAccountName.value)"
Write-Host "API base URL : $apiBaseUrl"

# ------------------------------------------------------------------------------------------------
Write-Step 'Deploying the function app code with the Azure CLI'
# ------------------------------------------------------------------------------------------------
# Role assignments can take a moment to propagate, so a first attempt may fail - retry a few times.
$deployed = $false
for ($attempt = 1; $attempt -le 4 -and -not $deployed; $attempt++) {
    az functionapp deployment source config-zip --resource-group $ResourceGroup --name $functionApp --src $zipFile --only-show-errors -o none
    if ($LASTEXITCODE -eq 0) { $deployed = $true; break }
    if ($attempt -lt 4) {
        Write-Warning "Deployment attempt $attempt failed. Waiting 30 s before retrying..."
        Start-Sleep -Seconds 30
    }
}
if (-not $deployed) { throw 'Deploying the zip package failed 4 times. See the messages above.' }

# ------------------------------------------------------------------------------------------------
if (-not $SkipSmokeTest) {
    Write-Step 'Smoke test against the live API'
    # 1. wait until the host answers (an unknown job id must give 404 from our code)
    $up = $false
    for ($i = 1; $i -le 20; $i++) {
        $r = Get-HttpStatus "$apiBaseUrl/jobs/00000000000000000000000000000000"
        if ($r.Code -eq 404) { $up = $true; break }
        Write-Host "  waiting for the function host ($i/20, last status $($r.Code))..."
        Start-Sleep -Seconds 15
    }
    if (-not $up) { throw 'The API did not come up in time. Check the Log stream / Application Insights of the function app.' }

    # 2. start a small job and wait for the result
    $start = Get-HttpStatus "$apiBaseUrl/jobs?count=3" 'POST' @{ 'x-api-key' = $ApiKey }
    if ($start.Code -ne 202) { throw "POST /jobs returned $($start.Code): $($start.Body)" }
    $jobId = ($start.Body | ConvertFrom-Json).jobId
    Write-Host "  started job $jobId"

    $finished = $false
    for ($i = 1; $i -le 40; $i++) {
        Start-Sleep -Seconds 5
        $status = (Get-HttpStatus "$apiBaseUrl/jobs/$jobId").Body | ConvertFrom-Json
        Write-Host "  status: $($status.status) ($($status.completed)/$($status.total))"
        if ($status.status -in 'Completed', 'CompletedWithErrors', 'Failed') { $finished = $true; break }
    }
    if (-not $finished -or $status.status -ne 'Completed') {
        throw "Smoke test job ended as '$($status.status)'. Check Application Insights (queue-trigger exceptions) for details."
    }

    $images = (Get-HttpStatus "$apiBaseUrl/jobs/$jobId/images").Body | ConvertFrom-Json
    $first = $images.images | Select-Object -First 1
    $download = Get-HttpStatus $first.url
    if ($download.Code -ne 200) { throw "Image link returned $($download.Code)." }
    Write-Host "  OK: $($images.images.Count) images, first one: $($first.url)" -ForegroundColor Green
}

# ------------------------------------------------------------------------------------------------
# Point the .http documentation at the new deployment (the API key is NOT written into the file).
if (Test-Path $httpFile) {
    $content = Get-Content $httpFile -Raw
    $updated = [regex]::Replace($content, '(?m)^@baseUrl\s*=.*$', "@baseUrl = $apiBaseUrl")
    if ($updated -ne $content) {
        Set-Content -Path $httpFile -Value $updated -NoNewline
        Write-Host "Updated @baseUrl in http/weather-images.http"
    }
}

Write-Host ''
Write-Host 'Deployment finished.' -ForegroundColor Green
Write-Host "  API base URL : $apiBaseUrl"
Write-Host "  API key      : $ApiKey      (header x-api-key; also in $keyFile)"
Write-Host ''
Write-Host 'Try it:'
Write-Host "  curl -X POST `"$apiBaseUrl/jobs`" -H `"x-api-key: $ApiKey`""
