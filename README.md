# n8n Move Manager

**n8n Move Manager** is a local .NET 9 Web API and Angular app for importing n8n workflow JSON through uploads, Docker, or the public API; it normalizes snapshots, stores them in an embedded Git repository, and supports review, diffs, credentials, promotions, and merges.

The app automatically creates one default environment named **Local** with key `local` and Git branch `env/local`. Docker export imports a snapshot into the selected environment branch only; it does not export decrypted credentials and does not automatically promote workflows between environments.

## Tech Stack

- Backend: .NET 9 Web API
- Frontend: Angular
- Database: SQLite with EF Core
- Git: LibGit2Sharp
- JSON: System.Text.Json

## Project Layout

- `backend/Api` - Web API host and HTTP endpoints
- `backend/Application` - import orchestration, normalization, DTOs, service contracts
- `backend/Domain` - workspace and workflow metadata entities
- `backend/Infrastructure` - EF Core SQLite and LibGit2Sharp implementations
- `frontend` - Angular app

On first backend run, the app creates:

- SQLite database: `backend/Api/App_Data/n8n-move-manager.db`
- Embedded Git repo: `backend/Api/App_Data/workspaces/default/repo`
- Local environment: `Local` / `local` / `env/local`
- Git branch: `env/local`
- Normalized workflow files: `backend/Api/App_Data/workspaces/default/repo/workflows/*.json`

## Run Locally

## Security and local accounts

Authentication is opt-in for local development and should be enabled before exposing the API beyond localhost. The first authenticated start creates the configured administrator account; supply these values through environment variables or a secret store, never commit them.

```powershell
$env:Auth__Enabled = "true"
$env:Auth__SigningKey = "replace-with-a-random-32-character-or-longer-secret"
$env:Auth__BootstrapAdminUser = "admin"
$env:Auth__BootstrapAdminPassword = "replace-with-a-strong-12-character-or-longer-password"
dotnet run --project backend/Api --launch-profile http
```

Roles are `Viewer`, `Editor`, `Approver`, and `Admin`. Approver (or Admin) is required for live Data Table deployment and workflow deployment. n8n and AI API keys are encrypted at rest using ASP.NET Core data protection; persist `backend/Api/App_Data/protection-keys` with the rest of App_Data when deploying.

## Workflow deployment

`POST /api/workflows/deployment/preview` validates the selected source snapshot and target n8n connection without making a remote change. `POST /api/workflows/deployment/deploy` requires an explicit confirmation and an Approver role. It creates or updates workflows through the configured `/api/v1/workflows` API path, but intentionally deploys every workflow inactive. Activation remains a distinct n8n action.

## n8n Public API Workflow Sync

In **Data Tables**, configure the selected environment's n8n base URL, API key, and Workflow API path (normally `/api/v1/workflows`). **Sync workflows from n8n** lists every workflow, fetches each complete definition, normalizes it, scans credential references, and commits only changed files to that environment's Git branch. It never retrieves credential secrets or modifies the remote n8n instance.

The same operation is available as `POST /api/environments/{environmentKey}/n8n-api/sync-workflows`.

### Backend

```powershell
dotnet run --project backend/Api --launch-profile http
```

The API listens on:

```text
http://localhost:5107
```

Useful endpoints:

- `GET /api/environments`
- `GET /api/environments/{environmentKey}/workflows`
- `POST /api/environments/{environmentKey}/workflows/upload`
- `GET /api/docker/status`
- `GET /api/environments/{environmentKey}/docker/config`
- `POST /api/environments/{environmentKey}/docker/config`
- `POST /api/environments/{environmentKey}/docker/test`
- `POST /api/environments/{environmentKey}/docker/export-workflows`
- `GET /api/scheduled-jobs`
- `POST /api/scheduled-jobs`
- `POST /api/scheduled-jobs/{id}/run-now`
- `GET /api/scheduled-jobs/{id}/runs`
- `GET /api/environments/{environmentKey}/git/commits`
- `GET /api/environments/{environmentKey}/git/diff/latest`
- `GET /api/environments/{environmentKey}/git/diff/{commitSha}`

### Frontend

```powershell
cd frontend
npm install
npm start
```

The UI listens on:

```text
http://localhost:4200
```

## Upload Examples

Upload one or more exported `.json` workflow files in the UI from **Upload Workflows**.

The API also accepts a raw JSON body containing a single workflow object or an array of workflow objects:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5107/api/environments/local/workflows/upload `
  -ContentType 'application/json' `
  -Body (Get-Content .\workflow.json -Raw)
```

## Docker n8n Export

Use **Docker Export** in the UI to configure a selected environment:

- Enable Docker integration.
- Set the container name, for example `n8n`.
- Keep the n8n CLI command as `n8n` unless your container needs a different command.
- Test the Docker/n8n connection.
- Click **Export workflows now** to run:

```text
docker exec n8n n8n export:workflow --all --output=/tmp/n8nmm-workflows.json
docker cp n8n:/tmp/n8nmm-workflows.json <app temp path>
```

The exported JSON is imported through the same workflow importer used by manual upload, so files are normalized, credential references are scanned, and a Git commit is created only when changes exist.

Safety rules:

- The app only exports workflows.
- It does not run `n8n export:credentials --decrypted`.
- It does not store credential secrets.
- It does not automatically promote changes to another environment.
- Production promotion still requires manual review and the Merge Assistant.

## Docker Compose

The included Compose stack builds the API and Angular UI. The UI proxies `/api` to the API container, and a named volume persists SQLite, Git workspaces, backups, Hangfire data, and data-protection keys.

```powershell
Copy-Item .env.example .env
# Edit .env and replace the authentication values.
docker compose up --build -d
```

Open `http://localhost:4300`. The API is exposed only on loopback at `http://localhost:5107` for diagnostics; normal UI traffic stays inside the Compose network.

```powershell
docker compose logs -f
docker compose down
```

Do not delete the `app-data` volume unless you intend to remove all application data:

```powershell
docker compose down --volumes
```

### Docker/n8n export from a container

The default stack does **not** mount the Docker socket. If you enable the Docker export feature, you must install a compatible Docker CLI in the API image and explicitly mount the host Docker socket. That grants the application high privileges over the host; use it only in a trusted self-hosted environment.

## Scheduled Jobs

Use **Scheduled Jobs** in the UI to create recurring background jobs. The backend uses Hangfire with SQLite storage at:

```text
backend/Api/App_Data/hangfire.db
```

The Hangfire dashboard is available locally at:

```text
http://localhost:5107/hangfire
```

The dashboard is restricted to loopback requests. The app registers enabled recurring jobs at startup and stores job definitions and run history in the main SQLite metadata database.

Supported job types:

- `DockerN8nWorkflowExport` exports workflows from a configured container, imports them into the selected environment branch, scans credential references, and commits only when files changed.
- `WorkspaceBackup` creates timestamped ZIP backups under `backend/Api/App_Data/backups` and applies retention cleanup.

Example scheduled export:

- Job type: `DockerN8nWorkflowExport`
- Environment: `Local`
- Container name: `n8n`
- Schedule: daily at `21:00`
- Timezone: `Europe/Kyiv`
- Cron expression: `0 21 * * *`

Docker export jobs never export decrypted credentials and never call promotion apply. Scheduled jobs may import snapshots and create commits only in the selected environment. Moving changes to production still requires manual review.

## Normalization Rules

Before files are written and committed, the importer:

- Pretty-prints JSON with stable indentation
- Removes top-level volatile fields: `createdAt`, `updatedAt`, `versionId`, `staticData`, `pinData`
- Keeps workflow `id`
- Sorts top-level properties alphabetically
- Leaves node logic and credential references untouched

## Iteration 1 Acceptance Flow

1. Start the backend and frontend.
2. Confirm `GET /api/environments` returns Local with branch `env/local`.
3. Upload an exported n8n workflow JSON file into Local.
4. Confirm a normalized file appears under the embedded repo.
5. Confirm the upload result shows a commit SHA on `env/local`.
6. Modify the same workflow and upload it again.
7. Open **Git History** or **Latest Diff**.
8. Confirm the raw patch shows the change between commits.
