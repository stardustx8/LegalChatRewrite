# LegalChatRewrite

This repository contains the .NET 8 Azure Functions (isolated worker) backend and Angular v18 SPA frontend for the LegalChat RAG system.

Structure:
- LegalApi: AskFunction (HTTP GET/POST)
- LegalDocProcessor: CleanupIndex (HTTP POST), UploadBlob (HTTP POST), ProcessDocument (BlobTrigger)
- frontend: Angular v18 SPA (Ask + Admin Upload flows)

Follow project README sections below after initial implementation is completed.
