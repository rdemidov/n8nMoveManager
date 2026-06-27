# n8n Move Manager

n8n Move Manager is a self-hosted .NET 9 and Angular 22 application for keeping n8n workflows and Data Table schemas under Git-backed change control. It imports workflow snapshots from files, Docker, or the n8n public API; normalizes them; stores each environment on its own branch; and provides review, promotion, merge, restore, and deployment workflows.

The first run creates a **Local** environment with key `local` on branch `env/local`. Imports only update the selected environment branch. Nothing is automatically promoted or activated in n8n, and credential secrets are never exported.

## Current capabilities

- Manage multiple environments, each backed by an isolated Git branch.
- Import one or more n8n workflow JSON files and commit only meaningful changes.
- Preview the difference between n8n and the Git snapshot, including node, connection, credential, and settings changes, then selectively sync remote workflows.
- Export workflows from a local n8n Docker container or schedule API/Docker synchronization with Hangfire.
- Browse commits, raw patches, and workflow-aware semantic diffs; edit commit messages and inspect or download files from a commit.
- Compare environments, create promotion plans and baselines, preview merges, and resolve per-workflow conflicts in the manual merge assistant.
- Inventory credential references, define logical credentials, map them per environment, and export remapped workflow snapshots without storing secrets.
- Sync and compare n8n Data Table schemas, stage promotion snapshots, and explicitly deploy selected schemas to a live target.
- Preview and deploy selected workflows through the n8n API. Deployed workflows remain inactive.
- Create ZIP backups, restore a workflow or an entire environment from a commit, and review restore history.
- Inspect recent failed n8n executions for an environment.
- Optionally use an OpenAI-compatible chat-completions endpoint to summarize diffs and promotion plans, explain conflicts, suggest credential mappings, and answer questions using scoped project context.
- Optionally enable JWT authentication with `Viewer`, `Editor`, `Approver`, and `Admin` roles.

## Tech stack

- Backend: ASP.NET Core Minimal API on .NET 9
- Frontend: Angular 22, TypeScript 6, RxJS 7
- Metadata: SQLite with Entity Framework Core
- Versioning: LibGit2Sharp
- Scheduling: Hangfire with SQLite storage
- AI integration: Microsoft Agent Framework with an OpenAI-compatible endpoint

## Project layout

- `backend/Api` — API host, authentication, endpoint definitions, and startup schema initialization
- `backend/Application` — workflow operations, semantic diffs, promotions, merges, backup/restore, AI context, DTOs, and contracts
- `backend/Domain` — persisted entities
- `backend/Infrastructure` — SQLite, Git, n8n API, Docker, scheduling, encryption, and service implementations
- `backend/Application.Tests` — xUnit application and integration-style service tests
- `frontend` — Angular application

Runtime data is created under `backend/Api/App_Data` when running locally:

```text
App_Data/
├── n8n-move-manager.db
├── hangfire.db
├── backups/
├── protection-keys/
└── workspaces/default/repo/
    └── workflows/*.json
```

Persist all of `App_Data`, especially `protection-keys`: n8n and AI API keys are encrypted with ASP.NET Core Data Protection and require those keys to be decrypted later.

## Run locally

Prerequisites:

- .NET 9 SDK
- Node.js with npm (the frontend currently declares npm 11.12.1)
- Docker only if you want Docker export or the Compose deployment

Start the API:

```powershell
dotnet run --project backend/Api --launch-profile http
```

The API listens at `http://localhost:5107`. In Development, its OpenAPI document is available at `http://localhost:5107/openapi/v1.json`, and the loopback-only Hangfire dashboard is at `http://localhost:5107/hangfire`.

In another terminal, start the UI:

```powershell
cd frontend
npm install
npm start
```

Open `http://localhost:4200`. The Angular development server proxies `/api` to port 5107.

Authentication is disabled by default for local development. To test with authentication enabled:

```powershell
$env:Auth__Enabled = "true"
$env:Auth__SigningKey = "replace-with-a-random-32-character-or-longer-secret"
$env:Auth__BootstrapAdminUser = "admin"
$env:Auth__BootstrapAdminPassword = "replace-with-a-strong-12-character-or-longer-password"
dotnet run --project backend/Api --launch-profile http
```

The first authenticated start creates the bootstrap administrator. `Viewer` can read, `Editor` can make ordinary changes, `Approver` is required for live workflow/Data Table deployment, and `Admin` can also manage local users. Keep secrets in environment variables or a secret store.

## Docker Compose

The included stack builds both applications. The UI proxies API traffic over the internal Compose network, while a named volume persists all application data.

```powershell
Copy-Item .env.example .env
# Replace every placeholder in .env.
docker compose up --build -d
```

Open `http://localhost:4300`. The diagnostic API port is bound to loopback at `http://localhost:5107` by default. Both ports can be changed in `.env`.

```powershell
docker compose logs -f
docker compose down
```

`docker compose down --volumes` permanently removes the SQLite databases, embedded Git repository, backups, and encryption keys.

The default API image does not include a Docker CLI or mount the host Docker socket. Enabling container export in a Compose deployment requires both. A Docker socket mount gives the API high privileges over the host and should only be used in a trusted environment.

## Typical workflow

1. Create environments for the n8n instances you manage.
2. Import workflow snapshots by upload, Docker export, or n8n API sync.
3. Review Git history and semantic diffs.
4. Configure logical credential mappings between source and target environments.
5. Compare environments and build a promotion plan.
6. Preview the merge and resolve conflicts in the manual merge assistant when needed.
7. Apply the approved snapshot to the target environment branch.
8. Separately preview and explicitly deploy selected workflows or Data Table schemas to the target n8n instance.

Git promotion and live n8n deployment are intentionally separate operations.

## n8n public API integration

Configure each environment with:

- Base URL, such as `https://n8n.example.com`
- n8n API key
- Workflow path, normally `/api/v1/workflows`
- Data Tables read path and, for live deployment, a write-path template compatible with your n8n version

From **Workflows**, use **Preview n8n changes** to reconcile the remote instance with the current Git snapshot. The preview marks additions, modifications, in-sync items, and local-only workflows, and shows a semantic change summary before any write. Select only the remote workflows you want to import. Local-only files are not deleted.

Full sync and selective sync use the same normalizer and credential scanner as file upload. They never retrieve credential secrets or modify the remote instance.

## Workflow deployment

Workflow deployment is a deliberate two-step operation:

1. Preview selected source snapshots against the target n8n connection.
2. Confirm deployment as an `Approver` or `Admin`.

The service creates or updates workflows through the target's configured workflow API path. Every deployed workflow remains inactive; activation is left as a separate n8n action.

## Data Tables

The Data Tables screen can sync schema snapshots from n8n, compare source and target environments, generate a promotion plan, and stage selected schemas into the target Git branch. Live schema deployment is a separate confirmed action requiring `Approver` or `Admin` and is recorded in the deployment audit.

Data Table endpoints can vary between n8n releases. Verify the configured read path and write-path template against the target n8n instance before enabling live deployment.

## Docker workflow export

For an environment with Docker integration enabled, configure the container name and n8n CLI command, test the connection, then export. The operation is equivalent to:

```text
docker exec n8n n8n export:workflow --all --output=/tmp/n8nmm-workflows.json
docker cp n8n:/tmp/n8nmm-workflows.json <app temp path>
```

The result passes through the standard importer. The application never runs a decrypted credential export and never promotes the resulting commit automatically.

## Scheduled jobs

Hangfire supports three recurring job types:

- `DockerN8nWorkflowExport` — export and import workflows from a configured container.
- `N8nApiWorkflowSync` — import workflows through the configured n8n public API.
- `WorkspaceBackup` — create a timestamped ZIP backup and apply retention cleanup.

Jobs use standard five-field cron expressions and an IANA timezone, for example `0 21 * * *` with `Europe/Kyiv`. Definitions, next/last run information, logs, and run results are available in **Scheduled Jobs**. Import jobs write only to their selected environment branch and do not perform promotion or live deployment.

## Backups and restore

Backups are stored under `App_Data/backups` and can include the embedded Git repository and metadata database. The UI can also create a backup from a selected commit.

Restore always starts with a preview. You can restore one workflow or reconstruct an entire environment from an earlier commit; the restoration itself creates a new commit rather than rewriting Git history. Restore attempts are recorded in the audit log.

## AI assistant

AI features are disabled until configured under **AI Settings**. The default values target OpenAI's chat-completions endpoint and `gpt-4.1-mini`, but the endpoint and model are configurable. Test the connection before using AI actions.

Only scoped workflow, diff, promotion, or credential metadata is assembled for a request, and sensitive fields are redacted by the context builder. Review AI output as advisory: deterministic validation, previews, role checks, and explicit confirmation remain authoritative.

## Normalization and safety

Before a workflow is written, the importer:

- validates that each workflow is a JSON object;
- removes volatile top-level fields: `createdAt`, `updatedAt`, `versionId`, `staticData`, and `pinData`;
- keeps the workflow `id`;
- sorts top-level properties and writes stable indented JSON;
- preserves node logic, connections, and credential references;
- scans credential references without retrieving or storing secrets; and
- creates a commit only when tracked content changed.

## Useful API endpoints

```text
GET    /api/health
GET    /api/environments
GET    /api/environments/compare
GET    /api/environments/semantic-compare
POST   /api/environments/{key}/workflows/upload
GET    /api/environments/{key}/n8n-api/workflow-reconciliation
POST   /api/environments/{key}/n8n-api/sync-workflows/selected
GET    /api/environments/{key}/n8n-api/workflow-health
GET    /api/environments/{key}/git/commits
GET    /api/environments/{key}/semantic-diff/{commitSha}
GET    /api/promotions/plan
POST   /api/promotions/merge-preview
POST   /api/promotions/apply
POST   /api/workflows/deployment/preview
POST   /api/workflows/deployment/deploy
GET    /api/backups
GET    /api/scheduled-jobs
```

Use the OpenAPI document for the complete and current endpoint surface.

## Build and test

```powershell
dotnet build n8nMoveManager.slnx
dotnet test n8nMoveManager.slnx

cd frontend
npm ci
npm run build
npm test -- --watch=false
```

## License

Licensed under the [Apache License 2.0](LICENSE).
