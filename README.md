# n8n Move Manager

n8n Move Manager is a self-hosted .NET 10 and Angular 22 application for keeping n8n workflows and Data Table schemas under Git-backed change control. It imports workflow snapshots from files, Docker, or the n8n public API; normalizes them; stores each environment on its own branch; and provides review, promotion, merge, restore, and deployment workflows.

The first run creates a **Local** environment with key `local` on branch `env/local`. Imports only update the selected environment branch. Nothing is automatically promoted or activated in n8n, and credential secrets are never exported.

## Current capabilities

- Manage multiple environments, each backed by an isolated Git branch.
- Import one or more n8n workflow JSON files and commit only meaningful changes.
- Preview the difference between n8n and the Git snapshot, including node, connection, credential, and settings changes, then selectively sync remote workflows.
- Export workflows from a local n8n Docker container or schedule API/Docker synchronization with Hangfire.
- Browse commits, raw patches, and workflow-aware semantic diffs; edit commit messages and inspect or download files from a commit.
- Compare environments, create promotion plans and baselines, preview merges, and resolve per-workflow conflicts in the manual merge assistant.
- Inventory credential references, define logical credentials, map them per environment, and export remapped workflow snapshots without storing secrets.
- Sync and compare n8n Data Table schemas, map environment-specific table IDs manually or with AI, stage promotion snapshots, and explicitly deploy selected schemas to a live target.
- Preview and deploy selected workflows through the n8n API. Deployed workflows remain inactive.
- Create ZIP backups, restore a workflow or an entire environment from a commit, and review restore history.
- Inspect recent failed n8n executions for an environment.
- Optionally use an OpenAI-compatible chat-completions endpoint to summarize diffs and promotion plans, explain conflicts, suggest credential mappings, and answer questions using scoped project context.
- Optionally enable JWT authentication with `Viewer`, `Editor`, `Approver`, and `Admin` roles.

## Tech stack

- Backend: ASP.NET Core Minimal API on .NET 10
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

- .NET 10 SDK
- Node.js with npm
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

Create `.env` from the example and set these values:

```dotenv
AUTH_ENABLED=true
AUTH_SIGNING_KEY=replace-with-a-random-secret-at-least-32-characters-long
AUTH_BOOTSTRAP_ADMIN_USER=admin
AUTH_BOOTSTRAP_ADMIN_PASSWORD=replace-with-a-strong-password-at-least-12-characters
WEB_PORT=4300
API_PORT=5107
```

`AUTH_SIGNING_KEY` signs JWTs and must be a random secret of at least 32 characters. The bootstrap password must contain at least 12 characters. Bootstrap credentials create the first administrator only when the user database is empty; changing them later does not reset an existing administrator. Keep `.env` private and never commit real credentials.

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

## How to use

### 1. Sign in

Open the web address for the way you started the application:

- Local development: `http://localhost:4200`
- Docker Compose with the default port: `http://localhost:4300`

When authentication is enabled, sign in with `AUTH_BOOTSTRAP_ADMIN_USER` and `AUTH_BOOTSTRAP_ADMIN_PASSWORD`. These credentials create the first administrator only when the user database is empty.

### 2. Create the environments

Open **Environments**. The application creates `local` automatically; add an environment for each additional n8n instance. Give each environment:

- a readable name, such as `Production`;
- a stable lowercase key, such as `production`;
- a unique Git branch, such as `env/production`; and
- optional notes describing the instance.

Use the environment selector in the sidebar before working with environment-specific workflows, credentials, or Data Tables.

### 3. Connect each n8n instance

Select an environment, open **Data Tables**, and configure **n8n API connection**:

1. Enable API sync.
2. Enter the n8n base URL and API key.
3. Keep the workflow path as `/api/v1/workflows` unless the instance uses a different path.
4. Set the Data Tables read path for the installed n8n version.
5. To allow live schema updates, provide a write path containing `{id}`.
6. Select **Save connection**.

Repeat this for every environment. API keys are encrypted at rest and are not written into workflow snapshots or Git.

### 4. Import workflow and table snapshots

Open **Workflows List**, select **Preview n8n changes**, review the reconciliation results, select the remote changes you want, and choose **Sync selected**. Alternatively, use **Upload Workflows** for exported JSON files or **Docker Export** for a locally reachable container.

For Data Tables, open **Data Tables** and select **Sync schemas**. This stores table names, IDs, columns, and row counts; it never copies table rows.

### 5. Configure mappings

Open **Mappings** to pair credentials that serve the same purpose in the source and target environments. Create the pairs manually or use **AI create mappings**, then run export validation. Credential secrets are never copied.

For Data Tables:

1. Open **Data Tables** in the source environment.
2. Choose the target under **Compare environments** and select **Compare**.
3. In **Data Table mappings**, pair each source table with its target counterpart, or select **AI create mappings**.
4. Review AI warnings and skipped suggestions. Existing manual mappings are not overwritten automatically.

Mappings are necessary because credential and Data Table IDs normally differ between n8n environments.

### 6. Review and promote Git snapshots

Open **Promotion**, choose different source and target environments, and generate the plan. Review semantic workflow changes, credential checks, warnings, and blocking errors. Use **Manual Merge** when a workflow needs conflict resolution, then preview and apply the approved changes to the target Git branch.

Promotion changes the managed Git snapshot only. It does not modify the live target n8n instance.

### 7. Deploy to the target n8n instance

To deploy workflows, open **Workflows List** in the source environment, select workflows, choose the target, and select **Preview deployment**. Resolve every missing or stale credential/Data Table mapping, then choose **Deploy selected workflows**. Deployed workflows remain inactive so activation stays an explicit n8n action.

To deploy table schemas, open **Data Tables**, compare with the target, select changed mapped tables, and choose **Deploy selected schemas**. Only schemas are updated; rows are never copied.

When authentication is enabled, live deployment requires an `Approver` or `Admin` account.

### 8. Audit and recover

Use **Git History** and **Latest Diff** to inspect committed changes. Use **Backups** before significant promotions or deployments, and always preview a restore before applying it. **Scheduled Jobs** can automate snapshot imports and backups without automatically promoting or deploying them.

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

The Data Tables screen can sync schema snapshots from n8n, compare source and target environments, map source table IDs to their environment-specific target IDs, generate a promotion plan, and stage selected schemas into the target Git branch. After choosing a target and running **Compare**, use **Data Table mappings** to save pairs manually or select **AI create mappings**. AI suggestions use table names and column schemas, are validated against synced snapshots, and never overwrite an existing mapping automatically.

Workflow deployment validates these mappings and rewrites Data Table node references to target IDs. Missing or stale table mappings block deployment. Live schema deployment is a separate confirmed action requiring `Approver` or `Admin` when authentication is enabled and is recorded in the deployment audit.

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

Only scoped workflow, diff, promotion, credential, or Data Table schema metadata is assembled for a request, and sensitive fields are redacted by the context builder. Review AI output as advisory: deterministic validation, previews, role checks, and explicit confirmation remain authoritative.

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
GET    /api/data-tables/mappings
POST   /api/data-tables/mappings
POST   /api/data-tables/ai-create-mappings
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
