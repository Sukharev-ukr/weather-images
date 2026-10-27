# Weather images (Azure + GitHub assignment)

An HTTP API that creates **one image per Dutch weather station**: a public photo with the station's current
weather written on it. Work is done in the background through two Azure Storage queues; the images end up in
Blob Storage; progress is kept in Table Storage.

```text
                          POST /api/jobs  (x-api-key)                      GET /api/jobs/{id}        GET /api/jobs/{id}/images
 client ───────────────────────┐                                              │  status                    │  links (SAS)
                               ▼                                              ▼                            ▼
                    ┌─────────────────────┐   1 row/job          ┌────────────────────────────────────────────────┐
                    │ Azure Function app  │ ───────────────────► │ Table Storage:  jobs · jobimages               │
                    │ (Flex Consumption,  │                      └────────────────────────────────────────────────┘
                    │  .NET 10 isolated)  │
                    └─────────┬───────────┘
              {jobId,count}   │ queue 1: start-jobs
                              ▼
                    ┌─────────────────────┐   Buienradar JSON feed (weather stations)
                    │ FanOutJob           │ ◄───────────────────────────────────────────
                    └─────────┬───────────┘
        1 message per station │ queue 2: image-jobs   (fan-out: 40-50 messages)
                              ▼
                    ┌─────────────────────┐   public photo (picsum.photos)
                    │ GenerateImage (×N)  │ ◄────────────────────────────────  + weather text (ImageSharp)
                    └─────────┬───────────┘
                              ▼
                    Blob Storage  container "images" (private) ── read-only SAS links in the results
```

## API

Full, runnable documentation: [`http/weather-images.http`](http/weather-images.http).

| Method | Route | Auth | Result |
| --- | --- | --- | --- |
| `POST` | `/api/jobs` (`?count=1..50`, default 50) | header `x-api-key` | `202 Accepted` + `{ jobId, status, statusUrl, resultsUrl }` and a `Location` header |
| `GET` | `/api/jobs/{jobId}` | – | `200` `{ status, requested, total, completed, failed, percentComplete, … }` |
| `GET` | `/api/jobs/{jobId}/images` | – | `200` when finished, `202` (+ `Retry-After`) while running: `{ status, isComplete, images: [{ stationId, stationName, region, fileName, url }] }` |

`status` is `Queued → Running → Completed | CompletedWithErrors | Failed`. Unknown or malformed ids give `404`,
a bad `count` gives `400`, a missing/wrong key gives `401`.

## Requirements checklist

| | Requirement | Where |
| --- | --- | --- |
| **Must** | Publicly accessible API for requesting fresh images | `JobsApi.StartJob` – `POST /api/jobs` (Function app, anonymous HTTP trigger + API key) |
| **Must** | Queues, so the first call stays fast | the POST only writes a table row + one queue message and returns `202` |
| **Must** | Multiple queues: one to start, one to fetch/update an image | `start-jobs` → `FanOutJob`, `image-jobs` → `GenerateImage` ([`QueueWorkers.cs`](src/WeatherImages.Functions/Functions/QueueWorkers.cs)) |
| **Must** | Queue Storage: create, read **and delete** messages | messages are created with `QueueClient` ([`JobQueues.cs`](src/WeatherImages.Functions/Services/JobQueues.cs)); the queue trigger reads them and deletes each one when the function succeeds (retry after failure, `*-poison` queue after 5 attempts) |
| **Must** | Blob Storage stores and exposes the images | [`ImageStore.cs`](src/WeatherImages.Functions/Services/ImageStore.cs), container `images` |
| **Must** | Buienradar API for the stations | [`BuienradarClient.cs`](src/WeatherImages.Functions/Services/BuienradarClient.cs) |
| **Must** | A public API for the photo | [Lorem Picsum](https://picsum.photos) (no key needed) – [`ImageSourceClient.cs`](src/WeatherImages.Functions/Services/ImageSourceClient.cs); URL is configurable |
| **Must** | Public API to fetch the generated images | `GET /api/jobs/{id}/images` |
| **Must** | HTTP files with working links | [`http/weather-images.http`](http/weather-images.http) (`deploy.ps1` writes the real URL into it) |
| **Must** | Bicep template, including the queues | [`infra/main.bicep`](infra/main.bicep) |
| **Must** | `deploy.ps1`: dotnet publish + Bicep + az CLI deploy | [`deploy.ps1`](deploy.ps1) |
| **Must** | Code in GitHub, Hijdra added | see *Hand-in checklist* below |
| **Could** | SAS instead of public blobs | container is private; results contain read-only **user delegation SAS** links (signed by Entra ID, no storage key exists – shared key access is disabled) |
| **Could** | Build + deploy automatically from GitHub | [`.github/workflows/ci-cd.yml`](.github/workflows/ci-cd.yml) (OIDC, no stored passwords) |
| **Could** | Authentication on the request API | `x-api-key` header on `POST /api/jobs` (credentials: see *Hand-in checklist*) |
| **Could** | Status endpoint, status saved in a Table | `GET /api/jobs/{id}`, tables `jobs` + `jobimages` |

> The assignment text starts with "Using Azure Container Apps" but lists **Azure Function apps** as an allowed
> development service and asks `deploy.ps1` to "deploy the function using the azure cli". This solution therefore
> uses a Function app.

## Design notes

* **Hosting: Flex Consumption (Linux).** .NET 10 is not supported on the Linux *Consumption* plan, so the
  template uses the Flex Consumption plan (scale-to-zero, pay per use).
* **No secrets for storage.** The function app has a user-assigned managed identity with *Storage Blob Data Owner*,
  *Queue Data Contributor* and *Table Data Contributor* on one storage account. The host (`AzureWebJobsStorage__…`),
  the queue triggers and the app code all use that identity. Locally the same code uses the Azurite connection string.
* **Fan-out without contention.** Every image worker only writes *its own* row in table `jobimages`, so 40-50
  workers never fight over one entity. The overall status is derived when you ask for it
  ([`JobStatusCalculator`](src/WeatherImages.Functions/Services/JobStatusCalculator.cs), unit tested).
* **Failures don't hang a job.** A failing message is retried by the runtime. On the last attempt the worker
  records `Failed` in the table, so the job ends as `CompletedWithErrors`/`Failed` instead of staying `Running`.
  Tested locally with a broken image source and with Buienradar unreachable.
* **Retries are idempotent.** Same seed → same photo, same blob name → overwritten, table rows are upserted.
* **Fonts are embedded** (Liberation Sans, SIL OFL – see `Fonts/LICENSE-LiberationSans.txt`) because Linux hosts in
  Azure don't guarantee system fonts. The text rendering is adapted from the course sample `ImageEditor`.
* **Stations:** the assignment says 50, but the Buienradar feed currently contains **40** stations. The API asks for
  up to 50 (`requested`) and renders what exists (`total`).
* **ImageSharp advisories.** The course sample pins ImageSharp 3.1.12. Five advisories published on 2026-10-07
  have no 3.x fix (4.x / Drawing 3.x need a paid license). They concern TIFF encoding/decoding, histogram
  equalization and ICC parsing, none of which this app reaches. It also decodes defensively: only JPEG/PNG
  decoders registered, first frame only, metadata skipped, maximum 25 megapixels
  ([`ImageHelper.SafeDecoderOptions`](src/WeatherImages.Functions/Imaging/ImageHelper.cs), unit tested). The audit
  warning is suppressed on purpose in [`Directory.Build.props`](Directory.Build.props) with that reasoning.

## Deploy to Azure

Prerequisites: [.NET 10 SDK](https://dot.net), [Azure CLI](https://aka.ms/installazurecli), PowerShell 7+ (or Windows PowerShell 5.1),
an Azure subscription where you may create resources and role assignments.

```powershell
az login
./deploy.ps1                       # defaults: rg-weather-images, westeurope
./deploy.ps1 -ResourceGroup rg-wx -Location northeurope -SubscriptionId <id>
```

The script: checks the tools and resource providers → asks Azure for the Flex region/.NET 10 runtime string →
`dotnet publish` + zip → `az group create` + `az deployment group create` (Bicep) →
`az functionapp deployment source config-zip` → **smoke test** (starts a 3-image job on the live API and downloads
an image). At the end it prints the API URL and the API key and updates `@baseUrl` in the `.http` file.
Re-running it updates the existing deployment. Commit the updated `.http` file afterwards.

Remove everything when you're done (after grading): `az group delete -n rg-weather-images`.

## Run locally

```powershell
npm i -g azurite azure-functions-core-tools@4      # once
azurite --skipApiVersionCheck --location .azurite  # terminal 1: local Storage emulator
cd src/WeatherImages.Functions
copy local.settings.sample.json local.settings.json
func start                                         # terminal 2 -> http://localhost:7071/api
```

Then use `http/weather-images.http` with `@baseUrl = http://localhost:7071/api` and `@apiKey = local-dev-key`.
Tests: `dotnet test WeatherImages.slnx`.

## Build and deploy automatically from GitHub (optional)

`.github/workflows/ci-cd.yml` builds and tests on every push/PR. It deploys on pushes to `main` once you have run
`deploy.ps1` once and configured an OIDC login (no password stored in GitHub):

```powershell
$sub  = az account show --query id -o tsv
$rg   = "rg-weather-images"
$repo = "<github-user>/<repo>"

$app = az ad app create --display-name "gh-weather-images" --query "{id:id,appId:appId}" -o json | ConvertFrom-Json
az ad sp create --id $app.appId | Out-Null
az ad app federated-credential create --id $app.id --parameters "{`"name`":`"main`",`"issuer`":`"https://token.actions.githubusercontent.com`",`"subject`":`"repo:${repo}:ref:refs/heads/main`",`"audiences`":[`"api://AzureADTokenExchange`"]}"
az role assignment create --assignee $app.appId --role "Website Contributor" --scope "/subscriptions/$sub/resourceGroups/$rg"
```

Then in GitHub → Settings → Secrets and variables → Actions: secrets `AZURE_CLIENT_ID` (= `$app.appId`),
`AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`; variables `AZURE_FUNCTIONAPP_NAME` (printed by `deploy.ps1`) and
`AZURE_RESOURCE_GROUP`.

## Hand-in checklist

1. `./deploy.ps1` succeeded (smoke test green) and you committed the updated `http/weather-images.http`.
2. Push everything to GitHub and add **Hijdra** / `mark.hijdra@wearetriple.com` to the organization and the project.
3. Send the credentials (API URL + the `x-api-key` from `.deploy/api-key.txt`) with your mail. **Don't commit the key.**
4. Keep the Azure resources until the assignment has been graded.
5. Mail `mark.hijdra@wearetriple.com` before **Monday 9 November, 09:00 CET**: repository link, name, student number,
   and whether you used AI.

## Use of AI

This solution was generated with Claude (Anthropic) and then verified by running it: unit tests, a full local
end-to-end run (Azurite + Functions host, real Buienradar and Picsum calls), failure-path tests, and a dry run of
`deploy.ps1` against a stubbed Azure CLI. The Bicep template compiles with the Bicep CLI, but a real Azure
deployment has to be done by the repository owner. Read the code and the design notes above so you can explain
every part.

## Project layout

```text
deploy.ps1                          publish → Bicep → az deploy → smoke test
infra/main.bicep                    storage (blob, 2+2 queues, 2 tables), identity + RBAC, App Insights, Flex function app
http/weather-images.http            API documentation (runnable)
src/WeatherImages.Functions/        the Azure Functions app (.NET 10, isolated worker)
  Functions/JobsApi.cs              3 HTTP endpoints
  Functions/QueueWorkers.cs         FanOutJob (queue 1) and GenerateImage (queue 2)
  Services/                         Buienradar + image clients, Table/Queue/Blob access, status logic, API key check
  Imaging/                          ImageHelper (from the course sample) + WeatherImageRenderer
tests/WeatherImages.Tests/          unit tests
.github/workflows/ci-cd.yml         build, test, (deploy)
```
