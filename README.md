# LegalChatRewrite – Engineering Handover

This document is the developer handover for the LegalChat → LegalChatRewrite migration. It summarizes the architecture, how we met the rewrite spec, how to run and deploy, and the operational guardrails for TEST and PROD.

## Executive Summary
- LegalChatRewrite is a RAG system consisting of:
  - `LegalApi/` – .NET 8 Azure Functions (isolated) HTTP API for Q&A and static hosting of the Angular SPA.
  - `LegalDocProcessor/` – .NET 8 Azure Functions (isolated) ingestion pipeline (BlobTrigger) + admin endpoints (upload, cleanup, diagnostic).
  - `frontend/` – Angular v18 SPA (end-user Ask + Admin Upload flows), built for Functions static hosting under `/app`.
- Data flow: Admin uploads `XX.docx` → blob stored in `legaldocsrag` → `ProcessDocument` extracts chunks, generates embeddings (Azure OpenAI), and uploads to Azure AI Search → `AskFunction` retrieves + composes answer.

## Compliance With Rewrite Specification
- Backend: C# Azure Functions v4 (.NET 8, isolated) – 
  - Verified via `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated` and .NET 8 target.
- Frontend: Angular v18+ – (`package.json` shows `@angular/*` at `^18.2.0`).
- Visual Studio 2022 – Project Type “Azure Function” – 
  - Two Function Apps: `LegalApi/` and `LegalDocProcessor/`, each with `Program.cs`.
- One file per endpoint – 
  - API (`LegalApi/`): `AskFunction.cs` (`/api/ask`), `UiFunction.cs` (serves SPA at `/app`).
  - Processor (`LegalDocProcessor/`): `ProcessDocument.cs` (BlobTrigger), `UploadBlob.cs` (`/api/upload_blob`), `CleanupIndex.cs` (`/api/cleanup_index`), `DiagnosticFunction.cs` (`/api/diagnostic`).
- Fifth file `Program.cs` present –  in both projects.

## Repo Structure (high level)
```
LegalApi/
  AskFunction.cs
  UiFunction.cs
  Program.cs
  appsettings.json

LegalDocProcessor/
  ProcessDocument.cs
  UploadBlob.cs
  CleanupIndex.cs
  DiagnosticFunction.cs
  Program.cs
  appsettings.json

frontend/
  angular.json
  package.json
  proxy.test.conf.json
  src/
    environments/
      environment.prod.ts
      environment.test.ts

.github/workflows/
  main_fct-euw-legalcb-legalapi-test.yml
  main_fct-euw-legalcb-legaldocprocessor-test.yml
  main_fct-euw-legalcb-legalapi-prod.yml
  main_fct-euw-legalcb-legaldocprocessor-prod.yml
```

## Key Endpoints and Triggers
- `LegalApi/AskFunction.cs` – `GET|POST /api/ask`
  - `?ping=1` returns `ok`.
  - `?diag=1` calls Azure OpenAI chat+embedding to verify external connectivity.
- `LegalApi/UiFunction.cs`
  - `GET /` → 301 to `/app/`.
  - `GET /app/{*}` → serves Angular SPA from `wwwroot/app`.
- `LegalDocProcessor/ProcessDocument.cs` – BlobTrigger `legaldocsrag/{name}`
  - Parses `XX.docx`, extracts text/tables/images; captions/OCR via Azure OpenAI Vision (if chat deploy set); generates embeddings; deletes prior docs for ISO; uploads new docs.
- `LegalDocProcessor/UploadBlob.cs` – `POST /api/upload_blob`
  - Accepts `{ filename: "XX.docx", file_data: base64, container: "legaldocsrag" }`.
  - Dynamic CORS via `CORS_ALLOWED_ORIGINS` (details below).
- `LegalDocProcessor/CleanupIndex.cs` – `POST /api/cleanup_index`
  - Body `{ iso_code: "ALL" | "CH" }`. Deletes from Azure AI Search; optionally deletes blobs/images/receipts.
- `LegalDocProcessor/DiagnosticFunction.cs` – `GET|POST /api/diagnostic`
  - Reports env presence and connectivity for Storage, Search, and OpenAI.

## Environment & Configuration
Group by concern (no secrets in code):

- Azure AI Search
  - `KNIFE_SEARCH_ENDPOINT`, `KNIFE_SEARCH_KEY`, `KNIFE_SEARCH_INDEX` (default `knife-index`).
- Azure OpenAI
  - `KNIFE_OPENAI_ENDPOINT`, `KNIFE_OPENAI_KEY`.
  - Deploys: `OPENAI_CHAT_DEPLOY` (e.g., `gpt-5-chat`), `OPENAI_EMBED_DEPLOY` (e.g., `text-embedding-3-large`).
  - API versions: `OPENAI_CHAT_API_VERSION`, `OPENAI_EMBED_API_VERSION` (fallback `OPENAI_API_VERSION`).
- Storage
  - `KNIFE_STORAGE_CONNECTION_STRING` (Function runtime and blob IO).
  - Container: `legaldocsrag` (inputs and derived images `images/{ISO}/...`).
- CORS
  - `LegalDocProcessor/UploadBlob.cs` reads `CORS_ALLOWED_ORIGINS` (comma-separated) and echoes the request `Origin` when allowed.
  - Recommended for TEST: add Test UI origin, Prod UI origin, and `http://localhost:4200`.
- Angular UI
  - TEST build uses `--configuration test --base-href /app/` and is served from `/app`.
  - `environment.test.ts`: `adminBase` points to the Test DocProcessor host.

Hosts (example, no secrets):
- TEST LegalApi: `https://fct-euw-legalcb-legalapi-test-…azurewebsites.net`
- TEST DocProcessor: `https://fct-euw-legalcb-legaldocprocessor-test-…azurewebsites.net`
- PROD DocProcessor: `https://fct-euw-legalcb-legaldocprocessor-prod-e6cxa7fbddenexgf.westeurope-01.azurewebsites.net`

Embedding vector dimensions:
- Ensure the Azure AI Search index `embedding` field dimension matches the embedding model:
  - `text-embedding-3-large` → 3072 dims.
  - `text-embedding-3-small` → 1536 dims.

## Build & Deploy
- GitHub Actions workflows in `.github/workflows/`:
  - TEST API: `main_fct-euw-legalcb-legalapi-test.yml`
    - Builds Angular with `--configuration test --base-href /app/`.
    - Copies `dist/<project>/browser` to `LegalApi/publish_output/app` so `UiFunction` serves `/app`.
  - TEST Processor: `main_fct-euw-legalcb-legaldocprocessor-test.yml`
  - PROD equivalents for both apps.
- Triggers: `push` to `main` and `workflow_dispatch` (manual run from Actions UI).

## Local Development
- Requirements
  - Node.js 20.x, npm.
  - .NET 8 SDK, VS 2022 (Azure Functions isolated worker).
- Angular (TEST proxy workflow)
  - `cd frontend`
  - `npm ci`
  - `npm run start:test` (uses `proxy.test.conf.json` to call Test Functions)
  - Build: `npm run build:test`
- Functions
  - Configure secrets via Function App settings when running in Azure.
  - For local-only references, use `local.settings.json` (not committed). Templates with placeholders: `LegalApi/local.settings.test.json`, `LegalDocProcessor/local.settings.test.json`.

## Diagnostics & Smoke Tests
- API health
  - `/api/ask?ping=1` → `200 ok`.
  - `/api/ask?diag=1` → returns status/bodies for chat and embeddings.
- Processor health
  - `/api/diagnostic` → shows env flags, storage list, and search counts.
- Search verification (PowerShell example)
```powershell
$search  = "https://<search>.search.windows.net"
$index   = "knife-index"
$apiKey  = "<key>"
$payload = '{"search":"*","filter":"iso_code eq ''CO''","count":true,"top":0}'
Invoke-WebRequest -Uri "$search/indexes/$index/docs/search?api-version=2023-11-01" `
  -Method POST -Headers @{ "api-key"=$apiKey; "Content-Type"="application/json" } `
  -Body $payload -UseBasicParsing
```

## Security & Ops Notes
- Secrets must not be committed; set in Azure App Settings.
- CORS: set `CORS_ALLOWED_ORIGINS` to include Test UI, Prod UI, and `http://localhost:4200` to support browser admin uploads.
- Index schema must match embedding dimension; if model changes, recreate/migrate index accordingly.

## Known Limitations / Next Steps
- Consider PR triggers for TEST workflows if you want pre-merge validation.
- Optional UI enhancement: add a Diagnostics link that calls `/api/ask?diag=1` on the current host.
- Add an automated bulk uploader for fixtures if needed.

---

Default working repository remains `stardustx8/LegalChatRewrite`. A one-time mirror to `victorinox-com/knife-legislation-bot-rewrite` was performed on `main` (no further pushes unless explicitly requested).
