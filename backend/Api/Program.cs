using Api;
using Application;
using Application.Contracts;
using Application.Models;
using Hangfire;
using Hangfire.Dashboard;
using Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

var appDataPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(appDataPath);
var authEnabled = builder.Configuration.GetValue("Auth:Enabled", false);
var signingKey = builder.Configuration["Auth:SigningKey"];
if (authEnabled && string.IsNullOrWhiteSpace(signingKey)) throw new InvalidOperationException("Auth:SigningKey is required when authentication is enabled.");
builder.Configuration["ConnectionStrings:Default"] = $"Data Source={Path.Combine(appDataPath, "n8n-move-manager.db")}";
builder.Configuration["Workspace:RepoPath"] = Path.Combine(appDataPath, "workspaces", "default", "repo");

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(appDataPath, "protection-keys"))).SetApplicationName("n8n-move-manager");
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("viewer", policy => policy.RequireRole("Viewer", "Editor", "Approver", "Admin"));
    options.AddPolicy("editor", policy => policy.RequireRole("Editor", "Approver", "Admin"));
    options.AddPolicy("approver", policy => policy.RequireRole("Approver", "Admin"));
    options.AddPolicy("admin", policy => policy.RequireRole("Admin"));
});
if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = "n8n-move-manager", ValidateAudience = true, ValidAudience = "n8n-move-manager",
            ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey!)),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
}
builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
        policy.WithOrigins("http://localhost:4200", "http://127.0.0.1:4200")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScheduledJobHangfire(builder.Configuration, appDataPath);
builder.Services.AddScoped<WorkflowNormalizer>();
builder.Services.AddScoped<WorkflowCredentialScanner>();
builder.Services.AddScoped<WorkflowImportService>();
builder.Services.AddScoped<IWorkflowImportService>(provider => provider.GetRequiredService<WorkflowImportService>());
builder.Services.AddScoped<WorkflowRemapExportService>();
builder.Services.AddScoped<WorkflowSemanticDiffService>();
builder.Services.AddScoped<PromotionService>();
builder.Services.AddScoped<BackupRestoreService>();
builder.Services.AddScoped<MergedWorkflowValidationService>();
builder.Services.AddScoped<ManualWorkflowMergeService>();
builder.Services.AddScoped<DockerN8nExportService>();
builder.Services.AddScoped<AiContextBuilder>();
builder.Services.AddScoped<AiDiffAssistantService>();
builder.Services.AddScoped<AiCredentialMappingService>();
builder.Services.AddScoped<AiDataTableMappingService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
app.UseCors("AngularDev");
if (authEnabled) { app.UseAuthentication(); app.UseAuthorization(); }
if (authEnabled)
{
    app.Use(async (context, next) =>
    {
        var isManagerApi = context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/auth");
        var isWrite = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
        if (isManagerApi && isWrite && !context.User.IsInRole("Editor") && !context.User.IsInRole("Approver") && !context.User.IsInRole("Admin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "An Editor, Approver, or Admin role is required for changes." });
            return;
        }
        await next();
    });
}
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new LocalOnlyHangfireDashboardAuthorizationFilter()]
});

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await dbContext.Database.EnsureCreatedAsync();
    await EnsureIteration2SchemaAsync(dbContext);
    await EnsureIteration3SchemaAsync(dbContext);
    await EnsureIteration5SchemaAsync(dbContext);
    await EnsureIteration7SchemaAsync(dbContext);
    await EnsureIteration8SchemaAsync(dbContext);
    await EnsureIteration9SchemaAsync(dbContext);
    await EnsureIteration10SchemaAsync(dbContext);
    await EnsureIteration11SchemaAsync(dbContext);
    await EnsureIteration12SchemaAsync(dbContext);
    await EnsureIteration13SchemaAsync(dbContext);
    await EnsureIteration14SchemaAsync(dbContext);
    var workspaceService = scope.ServiceProvider.GetRequiredService<IWorkspaceService>();
    var environmentService = scope.ServiceProvider.GetRequiredService<IEnvironmentService>();
    var gitService = scope.ServiceProvider.GetRequiredService<IGitRepositoryService>();
    var workspace = await workspaceService.GetOrCreateDefaultWorkspaceAsync(CancellationToken.None);
    var localEnvironment = await environmentService.GetByKeyAsync("local", CancellationToken.None);
    gitService.EnsureRepository(workspace.RepoPath);
    gitService.EnsureBranch(workspace.RepoPath, localEnvironment.Environment.GitBranch);
    var scheduledJobService = scope.ServiceProvider.GetRequiredService<IScheduledJobService>();
    await scheduledJobService.RegisterEnabledJobsAsync(CancellationToken.None);
    if (authEnabled)
    {
        var bootstrapPassword = builder.Configuration["Auth:BootstrapAdminPassword"];
        if (string.IsNullOrWhiteSpace(bootstrapPassword)) throw new InvalidOperationException("Auth:BootstrapAdminPassword is required only for the first authenticated startup.");
        await scope.ServiceProvider.GetRequiredService<ILocalUserService>().EnsureBootstrapAdminAsync(builder.Configuration["Auth:BootstrapAdminUser"] ?? "admin", bootstrapPassword, CancellationToken.None);
    }
}

var auth = app.MapGroup("/api/auth");
auth.MapPost("/login", async (LoginRequest request, ILocalUserService users, CancellationToken cancellationToken) =>
{
    if (!authEnabled) return Results.BadRequest(new { error = "Authentication is disabled for this deployment." });
    var user = await users.ValidateAsync(request.UserName, request.Password, cancellationToken);
    if (user is null) return Results.Unauthorized();
    var expires = DateTimeOffset.UtcNow.AddHours(8);
    var token = new JwtSecurityToken("n8n-move-manager", "n8n-move-manager", [new(ClaimTypes.Name, user.UserName), new(ClaimTypes.Role, user.Role)], expires: expires.UtcDateTime, signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey!)), SecurityAlgorithms.HmacSha256));
    return Results.Ok(new LoginResult(new JwtSecurityTokenHandler().WriteToken(token), expires, user.UserName, user.Role));
});
auth.MapGet("/users", async (ILocalUserService users, CancellationToken cancellationToken) => Results.Ok(await users.ListAsync(cancellationToken))).RequireAuthorization("admin");
auth.MapPost("/users", async (LocalUserRequest request, ILocalUserService users, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await users.CreateAsync(request, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireAuthorization("admin");

var api = app.MapGroup("/api");
if (authEnabled) api.RequireAuthorization("viewer");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/environments", async (IEnvironmentService environmentService, CancellationToken cancellationToken) =>
{
    var environments = await environmentService.ListAsync(cancellationToken);
    return Results.Ok(environments);
});

api.MapPost("/environments", async (
    EnvironmentRequest request,
    IEnvironmentService environmentService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var environment = await environmentService.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/environments/{environment.Key}", environment);
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}", async (
    string environmentKey,
    IEnvironmentService environmentService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var context = await environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        return Results.Ok(new EnvironmentDto(
            context.Environment.Id,
            context.Environment.Name,
            context.Environment.Key,
            context.Environment.Description,
            context.Environment.GitBranch,
            context.Environment.GitBranch,
            context.Environment.CreatedAt,
            context.Environment.UpdatedAt,
            context.Environment.IsDefault));
    }
    catch (WorkflowImportException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapPut("/environments/{environmentKey}", async (
    string environmentKey,
    EnvironmentRequest request,
    IEnvironmentService environmentService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await environmentService.UpdateAsync(environmentKey, request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/environments/{environmentKey}", async (
    string environmentKey,
    bool? force,
    IEnvironmentService environmentService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await environmentService.DeleteAsync(environmentKey, force ?? false, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/environments/{environmentKey}/clear", async (
    string environmentKey,
    EnvironmentClearRequest request,
    IEnvironmentService environmentService,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (!request.Confirmation)
        {
            return Results.BadRequest(new { error = "Confirmation is required to clear an environment." });
        }

        return Results.Ok(await environmentService.ClearAsync(environmentKey, request.CommitMessage, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (DbUpdateException ex)
    {
        return Results.BadRequest(new { error = $"Database update failed: {ex.GetBaseException().Message}" });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
    }
});

api.MapGet("/environments/compare", async (
    string source,
    string target,
    IEnvironmentService environmentService,
    IGitRepositoryService gitRepositoryService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var sourceContext = await environmentService.GetByKeyAsync(source, cancellationToken);
        var targetContext = await environmentService.GetByKeyAsync(target, cancellationToken);
        var files = gitRepositoryService.CompareBranches(
            sourceContext.Workspace.RepoPath,
            sourceContext.Environment.GitBranch,
            targetContext.Environment.GitBranch);
        return Results.Ok(new EnvironmentCompareDto(sourceContext.Environment.Key, targetContext.Environment.Key, files));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/semantic-compare", async (
    string source,
    string target,
    IEnvironmentService environmentService,
    IGitRepositoryService gitRepositoryService,
    WorkflowSemanticDiffService semanticDiffService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var sourceContext = await environmentService.GetByKeyAsync(source, cancellationToken);
        var targetContext = await environmentService.GetByKeyAsync(target, cancellationToken);
        var sourceFiles = gitRepositoryService.ReadWorkflowFilesFromBranch(sourceContext.Workspace.RepoPath, sourceContext.Environment.GitBranch);
        var targetFiles = gitRepositoryService.ReadWorkflowFilesFromBranch(targetContext.Workspace.RepoPath, targetContext.Environment.GitBranch);
        return Results.Ok(semanticDiffService.CompareWorkflowFiles(targetFiles, sourceFiles, target, source));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/workflows", async (
    string environmentKey,
    IEnvironmentService environmentService,
    IWorkflowMetadataService metadataService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var workflows = await metadataService.ListAsync(environmentKey, cancellationToken);
        return Results.Ok(workflows);
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/workflows/page", async (
    string environmentKey,
    int? page,
    int? pageSize,
    string? search,
    string? sort,
    string? direction,
    AppDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    var key = environmentKey.Trim().ToLowerInvariant();
    var currentPage = Math.Max(page ?? 1, 1);
    var size = Math.Clamp(pageSize ?? 25, 1, 100);
    var query = dbContext.Workflows.AsNoTracking().Where(workflow => workflow.EnvironmentKey == key);
    if (!string.IsNullOrWhiteSpace(search))
    {
        var pattern = $"%{search.Trim()}%";
        query = query.Where(workflow => EF.Functions.Like(workflow.Name, pattern) || (workflow.ExternalId != null && EF.Functions.Like(workflow.ExternalId, pattern)));
    }
    var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
    query = (sort ?? "name").ToLowerInvariant() switch
    {
        "updated" => descending ? query.OrderByDescending(item => item.UpdatedAt) : query.OrderBy(item => item.UpdatedAt),
        "nodes" => descending ? query.OrderByDescending(item => item.NodesCount) : query.OrderBy(item => item.NodesCount),
        "status" => descending ? query.OrderByDescending(item => item.Active) : query.OrderBy(item => item.Active),
        _ => descending ? query.OrderByDescending(item => item.Name) : query.OrderBy(item => item.Name)
    };
    var totalCount = await query.CountAsync(cancellationToken);
    var items = await query.Skip((currentPage - 1) * size).Take(size).Select(workflow => new WorkflowListItemDto(workflow.ExternalId, workflow.Name, workflow.Active, workflow.NodesCount, workflow.CreatedAt, workflow.UpdatedAt, workflow.EnvironmentKey, workflow.FilePath, workflow.LastImportedAt)).ToArrayAsync(cancellationToken);
    return Results.Ok(new PagedResult<WorkflowListItemDto>(items, totalCount, currentPage, size));
});

api.MapGet("/environments/{environmentKey}/n8n-api/config", async (string environmentKey, IEnvironmentN8nApiConfigStore configStore, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await configStore.GetAsync(environmentKey, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPut("/environments/{environmentKey}/n8n-api/config", async (string environmentKey, EnvironmentN8nApiConfigRequest request, IEnvironmentN8nApiConfigStore configStore, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await configStore.SaveAsync(environmentKey, request, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/environments/{environmentKey}/n8n-api/sync-workflows", async (string environmentKey, IWorkflowApiSyncService syncService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await syncService.SyncAsync(environmentKey, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
api.MapGet("/environments/{environmentKey}/n8n-api/workflow-reconciliation", async (string environmentKey, IWorkflowApiSyncService syncService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await syncService.PreviewAsync(environmentKey, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
api.MapPost("/environments/{environmentKey}/n8n-api/sync-workflows/selected", async (string environmentKey, WorkflowApiSyncSelectionRequest request, IWorkflowApiSyncService syncService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await syncService.SyncSelectedAsync(environmentKey, request.WorkflowIds, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
api.MapGet("/environments/{environmentKey}/n8n-api/workflow-health", async (string environmentKey, IWorkflowHealthService healthService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await healthService.GetAsync(environmentKey, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapGet("/environments/{environmentKey}/data-tables", async (string environmentKey, int? page, int? pageSize, string? search, string? sort, string? direction, IDataTableService dataTableService, CancellationToken cancellationToken) =>
    Results.Ok(await dataTableService.ListAsync(environmentKey, page ?? 1, pageSize ?? 25, search, sort, direction, cancellationToken)));

api.MapPost("/environments/{environmentKey}/data-tables/sync", async (string environmentKey, IDataTableService dataTableService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dataTableService.SyncAsync(environmentKey, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapGet("/data-tables/compare", async (string source, string target, IDataTableService dataTableService, CancellationToken cancellationToken) =>
    Results.Ok(await dataTableService.CompareAsync(source, target, cancellationToken)));

api.MapGet("/data-tables/promotion-plan", async (string source, string target, IDataTableService dataTableService, CancellationToken cancellationToken) =>
    Results.Ok(await dataTableService.GetPromotionPlanAsync(source, target, cancellationToken)));

api.MapGet("/data-tables/mappings", async (string source, string target, IDataTableService dataTableService, CancellationToken cancellationToken) =>
    Results.Ok(await dataTableService.GetMappingsAsync(source, target, cancellationToken)));

api.MapPost("/data-tables/mappings", async (DataTableMappingRequest request, IDataTableService dataTableService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dataTableService.SaveMappingAsync(request, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapDelete("/data-tables/mappings/{mappingId:guid}", async (Guid mappingId, IDataTableService dataTableService, CancellationToken cancellationToken) =>
{
    await dataTableService.DeleteMappingAsync(mappingId, cancellationToken);
    return Results.NoContent();
});

api.MapPost("/data-tables/ai-create-mappings", async (AiDataTableMappingRequest request, AiDataTableMappingService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.CreateMappingsAsync(request, cancellationToken)); }
    catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException or HttpRequestException or TaskCanceledException) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapPost("/data-tables/promotions/stage", async (DataTablePromotionApplyRequest request, IDataTableService dataTableService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dataTableService.ApplyPromotionAsync(request, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
var deployDataTablesEndpoint = api.MapPost("/data-tables/promotions/deploy-live", async (DataTableLiveDeployRequest request, IDataTableService dataTableService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dataTableService.DeploySchemasAsync(request, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
var previewWorkflowDeploymentEndpoint = api.MapPost("/workflows/deployment/preview", async (WorkflowDeploymentPreviewRequest request, IWorkflowDeploymentService deploymentService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await deploymentService.PreviewAsync(request, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
var deployWorkflowsEndpoint = api.MapPost("/workflows/deployment/deploy", async (WorkflowDeploymentApplyRequest request, IWorkflowDeploymentService deploymentService, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await deploymentService.DeployAsync(request, cancellationToken)); }
    catch (WorkflowImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
if (authEnabled)
{
    deployDataTablesEndpoint.RequireAuthorization("approver");
    previewWorkflowDeploymentEndpoint.RequireAuthorization("approver");
    deployWorkflowsEndpoint.RequireAuthorization("approver");
}
api.MapGet("/data-tables/deployment-audit", async (AppDbContext db, CancellationToken cancellationToken) => Results.Ok(await db.DataTableDeploymentAudits.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToArrayAsync(cancellationToken)));

api.MapPost("/environments/{environmentKey}/workflows/upload", async Task<Results<Ok<object>, BadRequest<object>>> (
    string environmentKey,
    HttpRequest request,
    WorkflowImportService importService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var upload = await ReadUploadPayloadAsync(request, cancellationToken);
        var result = await importService.ImportAsync(environmentKey, upload.Sources, upload.CommitMessage, cancellationToken);
        return TypedResults.Ok<object>(result);
    }
    catch (WorkflowImportException ex)
    {
        return TypedResults.BadRequest<object>(new { error = ex.Message });
    }
    catch (DbUpdateException ex)
    {
        return TypedResults.BadRequest<object>(new { error = $"Database update failed: {ex.GetBaseException().Message}" });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return TypedResults.BadRequest<object>(new { error = $"Git operation failed: {ex.Message}" });
    }
});

api.MapGet("/environments/{environmentKey}/commits/{commitSha}/files", async (
    string environmentKey,
    string commitSha,
    BackupRestoreService restoreService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await restoreService.ListCommitFilesAsync(environmentKey, commitSha, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/commits/{commitSha}/files/content", async (
    string environmentKey,
    string commitSha,
    string path,
    BackupRestoreService restoreService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await restoreService.GetCommitFileContentAsync(environmentKey, commitSha, path, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/commits/{commitSha}/files/download", async (
    string environmentKey,
    string commitSha,
    string path,
    BackupRestoreService restoreService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var file = await restoreService.GetCommitFileContentAsync(environmentKey, commitSha, path, cancellationToken);
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(file.Content),
            "application/json",
            Path.GetFileName(file.FilePath));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/restore/preview", async (
    string environmentKey,
    string commitSha,
    BackupRestoreService restoreService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await restoreService.PreviewEnvironmentRestoreAsync(environmentKey, commitSha, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/environments/{environmentKey}/restore/workflow", async (
    string environmentKey,
    RestoreWorkflowRequest request,
    BackupRestoreService restoreService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await restoreService.RestoreWorkflowAsync(environmentKey, request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (DbUpdateException ex)
    {
        return Results.BadRequest(new { error = $"Database update failed: {ex.GetBaseException().Message}" });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
    }
});

api.MapPost("/environments/{environmentKey}/restore/environment", async (
    string environmentKey,
    RestoreEnvironmentRequest request,
    BackupRestoreService restoreService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await restoreService.RestoreEnvironmentAsync(environmentKey, request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (DbUpdateException ex)
    {
        return Results.BadRequest(new { error = $"Database update failed: {ex.GetBaseException().Message}" });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
    }
});

api.MapPost("/environments/{environmentKey}/backups/from-commit", async (
    string environmentKey,
    BackupFromCommitRequest request,
    BackupRestoreService restoreService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await restoreService.CreateBackupFromCommitAsync(environmentKey, request, appDataPath, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/backups", (BackupRestoreService restoreService) =>
    Results.Ok(restoreService.ListBackups(appDataPath)));

api.MapGet("/backups/{backupId}/download", (string backupId, BackupRestoreService restoreService) =>
{
    try
    {
        var path = restoreService.GetBackupPath(appDataPath, backupId);
        return Results.File(path, "application/zip", Path.GetFileName(path));
    }
    catch (WorkflowImportException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapDelete("/backups/{backupId}", (string backupId, BackupRestoreService restoreService) =>
{
    try
    {
        restoreService.DeleteBackup(appDataPath, backupId);
        return Results.NoContent();
    }
    catch (WorkflowImportException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapGet("/docker/status", async (
    DockerN8nExportService dockerExportService,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await dockerExportService.GetStatusAsync(cancellationToken));
});

api.MapGet("/environments/{environmentKey}/docker/config", async (
    string environmentKey,
    IEnvironmentDockerConfigStore configStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await configStore.GetAsync(environmentKey, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/environments/{environmentKey}/docker/config", async (
    string environmentKey,
    EnvironmentDockerConfigRequest request,
    IEnvironmentDockerConfigStore configStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await configStore.SaveAsync(environmentKey, request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/environments/{environmentKey}/docker/test", async (
    string environmentKey,
    DockerN8nExportService dockerExportService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await dockerExportService.TestAsync(environmentKey, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/environments/{environmentKey}/docker/export-workflows", async (
    string environmentKey,
    DockerN8nExportService dockerExportService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await dockerExportService.ExportWorkflowsAsync(environmentKey, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (DbUpdateException ex)
    {
        return Results.BadRequest(new { error = $"Database update failed: {ex.GetBaseException().Message}" });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
    }
});

var scheduledJobs = api.MapGroup("/scheduled-jobs");

scheduledJobs.MapGet("/", async (
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
    Results.Ok(await scheduledJobService.ListAsync(cancellationToken)));

scheduledJobs.MapPost("/", async (
    ScheduledJobRequest request,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var job = await scheduledJobService.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/scheduled-jobs/{job.Id}", job);
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

scheduledJobs.MapGet("/{id:guid}", async (
    Guid id,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await scheduledJobService.GetAsync(id, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

scheduledJobs.MapPut("/{id:guid}", async (
    Guid id,
    ScheduledJobRequest request,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await scheduledJobService.UpdateAsync(id, request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

scheduledJobs.MapDelete("/{id:guid}", async (
    Guid id,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await scheduledJobService.DeleteAsync(id, cancellationToken);
        return Results.NoContent();
    }
    catch (WorkflowImportException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

scheduledJobs.MapPost("/{id:guid}/enable", async (
    Guid id,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await scheduledJobService.EnableAsync(id, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

scheduledJobs.MapPost("/{id:guid}/disable", async (
    Guid id,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await scheduledJobService.DisableAsync(id, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

scheduledJobs.MapPost("/{id:guid}/run-now", async (
    Guid id,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await scheduledJobService.RunNowAsync(id, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

scheduledJobs.MapGet("/{id:guid}/runs", async (
    Guid id,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await scheduledJobService.ListRunsAsync(id, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

scheduledJobs.MapGet("/{id:guid}/runs/{runId:guid}", async (
    Guid id,
    Guid runId,
    IScheduledJobService scheduledJobService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await scheduledJobService.GetRunAsync(id, runId, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapPut("/environments/{environmentKey}/git/commits/{commitSha}/message", async Task<Results<Ok<GitCommitDto>, BadRequest<object>>> (
    string environmentKey,
    string commitSha,
    CommitMessageRequest request,
    IEnvironmentService environmentService,
    IGitRepositoryService gitRepositoryService,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return TypedResults.BadRequest<object>(new { error = "Commit message is required." });
        }

        var context = await environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        var commit = gitRepositoryService.AmendCommitMessage(context.Workspace.RepoPath, context.Environment.GitBranch, commitSha, request.Message);
        return TypedResults.Ok(commit);
    }
    catch (WorkflowImportException ex)
    {
        return TypedResults.BadRequest<object>(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return TypedResults.BadRequest<object>(new { error = ex.Message });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return TypedResults.BadRequest<object>(new { error = $"Git operation failed: {ex.Message}" });
    }
});

api.MapGet("/environments/{environmentKey}/git/commits", async (
    string environmentKey,
    IEnvironmentService environmentService,
    IGitRepositoryService gitRepositoryService,
    int? limit,
    CancellationToken cancellationToken) =>
{
    try
    {
        var context = await environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        return Results.Ok(gitRepositoryService.GetRecentCommits(context.Workspace.RepoPath, context.Environment.GitBranch, limit ?? 25));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/git/diff/latest", async (
    string environmentKey,
    IEnvironmentService environmentService,
    IGitRepositoryService gitRepositoryService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var context = await environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        return Results.Ok(gitRepositoryService.GetLatestDiff(context.Workspace.RepoPath, context.Environment.GitBranch));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/git/diff/{commitSha}", async (
    string environmentKey,
    string commitSha,
    IEnvironmentService environmentService,
    IGitRepositoryService gitRepositoryService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var context = await environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        var diff = gitRepositoryService.GetCommitDiff(context.Workspace.RepoPath, commitSha);
        return diff.Count == 0 ? Results.NotFound(new { error = "Commit was not found or has no diff." }) : Results.Ok(diff);
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/semantic-diff/{commitSha}", async (
    string environmentKey,
    string commitSha,
    IEnvironmentService environmentService,
    IGitRepositoryService gitRepositoryService,
    WorkflowSemanticDiffService semanticDiffService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var context = await environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        var oldFiles = gitRepositoryService.ReadWorkflowFilesFromCommit(context.Workspace.RepoPath, commitSha, parent: true);
        var newFiles = gitRepositoryService.ReadWorkflowFilesFromCommit(context.Workspace.RepoPath, commitSha, parent: false);
        if (oldFiles.Count == 0 && newFiles.Count == 0)
        {
            return Results.NotFound(new { error = "Commit was not found or has no workflow files." });
        }

        return Results.Ok(semanticDiffService.CompareWorkflowFiles(oldFiles, newFiles, "parent", commitSha));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/credentials", async (
    string environmentKey,
    ICredentialInventoryService credentialInventoryService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await credentialInventoryService.ListEnvironmentCredentialsAsync(environmentKey, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/{environmentKey}/credential-references", async (
    string environmentKey,
    ICredentialInventoryService credentialInventoryService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await credentialInventoryService.ListCredentialReferencesAsync(environmentKey, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/logical-credentials", async (
    ILogicalCredentialService logicalCredentialService,
    CancellationToken cancellationToken) =>
    Results.Ok(await logicalCredentialService.ListAsync(cancellationToken)));

api.MapPost("/logical-credentials", async (
    LogicalCredentialRequest request,
    ILogicalCredentialService logicalCredentialService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var credential = await logicalCredentialService.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/logical-credentials/{credential.Id}", credential);
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPut("/logical-credentials/{id:guid}", async (
    Guid id,
    LogicalCredentialRequest request,
    ILogicalCredentialService logicalCredentialService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await logicalCredentialService.UpdateAsync(id, request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/logical-credentials/{id:guid}", async (
    Guid id,
    ILogicalCredentialService logicalCredentialService,
    CancellationToken cancellationToken) =>
{
    await logicalCredentialService.DeleteAsync(id, cancellationToken);
    return Results.NoContent();
});

api.MapPost("/logical-credentials/mappings", async (
    LogicalCredentialMappingRequest request,
    ILogicalCredentialService logicalCredentialService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await logicalCredentialService.SetMappingAsync(request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/logical-credentials/mappings/pair", async (
    LogicalCredentialPairMappingRequest request,
    ILogicalCredentialService logicalCredentialService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await logicalCredentialService.SetPairMappingAsync(request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/logical-credentials/mappings/{mappingId:guid}", async (
    Guid mappingId,
    ILogicalCredentialService logicalCredentialService,
    CancellationToken cancellationToken) =>
{
    await logicalCredentialService.DeleteMappingAsync(mappingId, cancellationToken);
    return Results.NoContent();
});

api.MapPost("/logical-credentials/ai-create-mappings", async (
    AiCredentialMappingRequest request,
    AiCredentialMappingService aiCredentialMappingService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await aiCredentialMappingService.CreateMappingsAsync(request, cancellationToken));
    }
    catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/export-remapped/validate", async (
    string source,
    string target,
    WorkflowRemapExportService exportService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await exportService.ValidateAsync(source, target, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/environments/export-remapped/preview", async (
    string source,
    string target,
    WorkflowRemapExportService exportService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await exportService.PreviewAsync(source, target, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/environments/export-remapped", async (
    string source,
    string target,
    WorkflowRemapExportService exportService,
    HttpResponse response,
    CancellationToken cancellationToken) =>
{
    try
    {
        var export = await exportService.ExportAsync(source, target, cancellationToken);
        if (export.Warnings.Count > 0)
        {
            response.Headers.Append("X-Export-Warnings", string.Join(" | ", export.Warnings.Select(warning => warning.Message)));
        }

        return Results.File(
            export.Content,
            "application/zip",
            $"workflows-{source}-to-{target}-remapped.zip");
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/promotions/plan", async (
    string source,
    string target,
    bool? includeDeletions,
    PromotionService promotionService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await promotionService.GeneratePlanAsync(
            source,
            target,
            selectedWorkflowFiles: null,
            includeDeletions: includeDeletions ?? false,
            recordAudit: true,
            cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/promotions/baseline", async (
    string source,
    string target,
    IPromotionBaselineService baselineService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await baselineService.GetAsync(source, target, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPut("/promotions/baseline", async (
    PromotionComparisonBaselineRequest request,
    IPromotionBaselineService baselineService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await baselineService.SetAsync(request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/promotions/merge-preview", async (
    PromotionMergePreviewRequest request,
    PromotionService promotionService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await promotionService.PreviewMergeAsync(request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
    }
});

api.MapPost("/promotions/apply", async (
    PromotionApplyRequest request,
    PromotionService promotionService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await promotionService.ApplyAsync(request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
    }
});

api.MapPost("/manual-merge/session", async (
    ManualMergeCreateRequest request,
    ManualWorkflowMergeService manualMergeService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await manualMergeService.CreateSessionAsync(request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
    }
});

api.MapGet("/manual-merge/session/{sessionId}", async (
    string sessionId,
    ManualWorkflowMergeService manualMergeService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await manualMergeService.GetSessionAsync(sessionId, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
});

api.MapPut("/manual-merge/session/{sessionId}/selection", async (
    string sessionId,
    ManualMergeSelectionUpdateRequest request,
    ManualWorkflowMergeService manualMergeService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await manualMergeService.UpdateSelectionAsync(sessionId, request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/manual-merge/session/{sessionId}/preview", async (
    string sessionId,
    ManualWorkflowMergeService manualMergeService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await manualMergeService.PreviewAsync(sessionId, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapGet("/manual-merge/session/{sessionId}/download", async (
    string sessionId,
    ManualWorkflowMergeService manualMergeService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var download = await manualMergeService.DownloadAsync(sessionId, cancellationToken);
        return Results.File(Encoding.UTF8.GetBytes(download.WorkflowJson), "application/json", download.FileName);
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapPost("/manual-merge/session/{sessionId}/apply", async (
    string sessionId,
    ManualMergeApplyRequest request,
    ManualWorkflowMergeService manualMergeService,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await manualMergeService.ApplyAsync(sessionId, request, cancellationToken));
    }
    catch (WorkflowImportException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (DbUpdateException ex)
    {
        return Results.BadRequest(new { error = $"Database update failed: {ex.GetBaseException().Message}" });
    }
    catch (LibGit2Sharp.LibGit2SharpException ex)
    {
        return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
    }
});

var ai = api.MapGroup("/ai");

ai.MapGet("/settings", async (
    IAiProviderSettingsStore settingsStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await settingsStore.GetAsync(cancellationToken)));

ai.MapPut("/settings", async (
    AiSettingsRequest request,
    IAiProviderSettingsStore settingsStore,
    CancellationToken cancellationToken) =>
{
    if (request.Enabled
        && (string.IsNullOrWhiteSpace(request.Endpoint)
            || string.IsNullOrWhiteSpace(request.ModelName)))
    {
        return Results.BadRequest(new { error = "Endpoint and model name are required when AI is enabled." });
    }

    return Results.Ok(await settingsStore.SaveAsync(request, cancellationToken));
});

ai.MapPost("/test", async (
    AiDiffAssistantService assistant,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await assistant.TestAsync(cancellationToken));
    }
    catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

ai.MapPost("/summarize-workflow-diff", async (
    AiWorkflowDiffRequest request,
    AiDiffAssistantService assistant,
    CancellationToken cancellationToken) =>
{
    if (request.DiffContext is null)
    {
        return Results.BadRequest(new { error = "Diff context is required." });
    }

    try
    {
        return Results.Ok(await assistant.SummarizeWorkflowDiffAsync(request, cancellationToken));
    }
    catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

ai.MapPost("/summarize-promotion-plan", async (
    AiPromotionPlanRequest request,
    AiDiffAssistantService assistant,
    IEnvironmentService environmentService,
    IPromotionAuditService auditService,
    CancellationToken cancellationToken) =>
{
    if (request.PromotionPlan is null)
    {
        return Results.BadRequest(new { error = "Promotion plan is required." });
    }

    try
    {
        var response = await assistant.SummarizePromotionPlanAsync(request, cancellationToken);
        if (request.SaveToAuditLog)
        {
            var source = await environmentService.GetByKeyAsync(request.PromotionPlan.SourceEnvironment.Key, cancellationToken);
            var target = await environmentService.GetByKeyAsync(request.PromotionPlan.TargetEnvironment.Key, cancellationToken);
            await auditService.RecordAsync(new PromotionAuditCreate(
                source.Workspace.Id,
                source.Environment.Id,
                source.Environment.Key,
                target.Environment.Id,
                target.Environment.Key,
                "ai-summary",
                null,
                System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                null), cancellationToken);
        }

        return Results.Ok(response);
    }
    catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

ai.MapPost("/explain-conflict", async (
    AiConflictRequest request,
    AiDiffAssistantService assistant,
    CancellationToken cancellationToken) =>
{
    if (request.WorkflowChange is null && request.WorkflowDiff is null)
    {
        return Results.BadRequest(new { error = "Conflict workflow context is required." });
    }

    try
    {
        return Results.Ok(await assistant.ExplainConflictAsync(request, cancellationToken));
    }
    catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

ai.MapPost("/ask", async (
    AiAskRequest request,
    AiDiffAssistantService assistant,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Question is required." });
    }

    try
    {
        return Results.Ok(await assistant.AskAssistantAsync(request, cancellationToken));
    }
    catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Run();

static async Task<WorkflowUploadPayload> ReadUploadPayloadAsync(HttpRequest request, CancellationToken cancellationToken)
{
    if (request.HasFormContentType)
    {
        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.Count == 0)
        {
            throw new WorkflowImportException("Multipart upload did not include any files.");
        }

        var sources = new List<WorkflowUploadSource>();
        foreach (var file in form.Files)
        {
            await using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            sources.Add(new WorkflowUploadSource(file.FileName, await reader.ReadToEndAsync(cancellationToken)));
        }

        return new WorkflowUploadPayload(sources, form.TryGetValue("commitMessage", out var message) ? message.ToString() : null);
    }

    using var bodyReader = new StreamReader(request.Body);
    var body = await bodyReader.ReadToEndAsync(cancellationToken);
    return new WorkflowUploadPayload([new WorkflowUploadSource(null, body)], null);
}

static async Task EnsureIteration2SchemaAsync(AppDbContext dbContext)
{
    var commands = new[]
    {
        "ALTER TABLE Environments ADD COLUMN Description TEXT NULL",
        "ALTER TABLE Environments ADD COLUMN UpdatedAt TEXT NOT NULL DEFAULT '0001-01-01 00:00:00+00:00'",
        "ALTER TABLE Environments ADD COLUMN IsDefault INTEGER NOT NULL DEFAULT 0",
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_Environments_WorkspaceId_GitBranch ON Environments (WorkspaceId, GitBranch)",
        """
        CREATE TABLE IF NOT EXISTS CredentialReferences (
            Id TEXT NOT NULL CONSTRAINT PK_CredentialReferences PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            EnvironmentId TEXT NOT NULL,
            EnvironmentKey TEXT NOT NULL,
            WorkflowExternalId TEXT NULL,
            WorkflowName TEXT NOT NULL,
            WorkflowFilePath TEXT NOT NULL,
            NodeId TEXT NULL,
            NodeName TEXT NOT NULL,
            NodeType TEXT NOT NULL,
            CredentialType TEXT NOT NULL,
            CredentialId TEXT NULL,
            CredentialName TEXT NULL,
            DetectedAt TEXT NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_CredentialReferences_WorkspaceId_EnvironmentId_WorkflowFilePath ON CredentialReferences (WorkspaceId, EnvironmentId, WorkflowFilePath)",
        """
        CREATE TABLE IF NOT EXISTS EnvironmentCredentials (
            Id TEXT NOT NULL CONSTRAINT PK_EnvironmentCredentials PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            EnvironmentId TEXT NOT NULL,
            EnvironmentKey TEXT NOT NULL,
            CredentialType TEXT NOT NULL,
            CredentialId TEXT NULL,
            CredentialName TEXT NULL,
            FirstDetectedAt TEXT NOT NULL,
            LastDetectedAt TEXT NOT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_EnvironmentCredentials_Unique ON EnvironmentCredentials (WorkspaceId, EnvironmentId, CredentialType, CredentialId, CredentialName)",
        """
        CREATE TABLE IF NOT EXISTS LogicalCredentials (
            Id TEXT NOT NULL CONSTRAINT PK_LogicalCredentials PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            Key TEXT NOT NULL,
            DisplayName TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_LogicalCredentials_WorkspaceId_Key ON LogicalCredentials (WorkspaceId, Key)",
        """
        CREATE TABLE IF NOT EXISTS LogicalCredentialEnvironmentMappings (
            Id TEXT NOT NULL CONSTRAINT PK_LogicalCredentialEnvironmentMappings PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            LogicalCredentialId TEXT NOT NULL,
            EnvironmentId TEXT NOT NULL,
            EnvironmentCredentialId TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_LogicalCredentialEnvironmentMappings_Unique ON LogicalCredentialEnvironmentMappings (WorkspaceId, LogicalCredentialId, EnvironmentId)"
    };

    foreach (var command in commands)
    {
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(command);
        }
        catch (Exception ex) when (ex.GetBaseException().Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    await dbContext.Database.ExecuteSqlRawAsync("UPDATE Environments SET UpdatedAt = CreatedAt WHERE UpdatedAt = '0001-01-01 00:00:00+00:00'");
    await dbContext.Database.ExecuteSqlRawAsync("UPDATE Environments SET IsDefault = 1 WHERE Key = 'local'");
}

static async Task EnsureIteration3SchemaAsync(AppDbContext dbContext)
{
    var commands = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS PromotionAuditLogs (
            Id TEXT NOT NULL CONSTRAINT PK_PromotionAuditLogs PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            SourceEnvironmentId TEXT NOT NULL,
            SourceEnvironmentKey TEXT NOT NULL,
            TargetEnvironmentId TEXT NOT NULL,
            TargetEnvironmentKey TEXT NOT NULL,
            Status TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            AppliedAt TEXT NULL,
            CommitSha TEXT NULL,
            Summary TEXT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_PromotionAuditLogs_WorkspaceId_CreatedAt ON PromotionAuditLogs (WorkspaceId, CreatedAt)"
        ,
        """
        CREATE TABLE IF NOT EXISTS PromotionComparisonBaselines (
            Id TEXT NOT NULL CONSTRAINT PK_PromotionComparisonBaselines PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            SourceEnvironmentId TEXT NOT NULL,
            TargetEnvironmentId TEXT NOT NULL,
            CommitSha TEXT NOT NULL,
            Label TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_PromotionComparisonBaselines_Unique ON PromotionComparisonBaselines (WorkspaceId, SourceEnvironmentId, TargetEnvironmentId)"
    };

    foreach (var command in commands)
    {
        await dbContext.Database.ExecuteSqlRawAsync(command);
    }
}

static async Task EnsureIteration5SchemaAsync(AppDbContext dbContext)
{
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS AiProviderSettings (
            Id TEXT NOT NULL CONSTRAINT PK_AiProviderSettings PRIMARY KEY,
            Enabled INTEGER NOT NULL,
            Endpoint TEXT NOT NULL,
            ModelName TEXT NOT NULL,
            SensitiveApiKey TEXT NULL,
            UpdatedAt TEXT NOT NULL
        )
        """);
}

static async Task EnsureIteration7SchemaAsync(AppDbContext dbContext)
{
    await dbContext.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE IF NOT EXISTS EnvironmentDockerConfigs (
            EnvironmentId TEXT NOT NULL CONSTRAINT PK_EnvironmentDockerConfigs PRIMARY KEY,
            DockerEnabled INTEGER NOT NULL,
            ContainerName TEXT NOT NULL,
            N8nCliCommand TEXT NOT NULL,
            TempContainerPath TEXT NOT NULL,
            TempHostImportPath TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        )
        """);
}

static async Task EnsureIteration8SchemaAsync(AppDbContext dbContext)
{
    var commands = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS ScheduledJobs (
            Id TEXT NOT NULL CONSTRAINT PK_ScheduledJobs PRIMARY KEY,
            Name TEXT NOT NULL,
            JobType TEXT NOT NULL,
            EnvironmentId TEXT NOT NULL,
            CronExpression TEXT NOT NULL,
            Timezone TEXT NOT NULL,
            IsEnabled INTEGER NOT NULL,
            ConfigJson TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            LastRunAt TEXT NULL,
            NextRunAt TEXT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_ScheduledJobs_EnvironmentId ON ScheduledJobs (EnvironmentId)",
        "CREATE INDEX IF NOT EXISTS IX_ScheduledJobs_JobType ON ScheduledJobs (JobType)",
        """
        CREATE TABLE IF NOT EXISTS ScheduledJobRuns (
            Id TEXT NOT NULL CONSTRAINT PK_ScheduledJobRuns PRIMARY KEY,
            ScheduledJobId TEXT NOT NULL,
            StartedAt TEXT NOT NULL,
            FinishedAt TEXT NULL,
            Status TEXT NOT NULL,
            Logs TEXT NOT NULL,
            ErrorMessage TEXT NULL,
            CommitSha TEXT NULL,
            ResultJson TEXT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_ScheduledJobRuns_ScheduledJobId_StartedAt ON ScheduledJobRuns (ScheduledJobId, StartedAt)"
    };

    foreach (var command in commands)
    {
        await dbContext.Database.ExecuteSqlRawAsync(command);
    }
}

static async Task EnsureIteration9SchemaAsync(AppDbContext dbContext)
{
    var commands = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS RestoreAuditLogs (
            Id TEXT NOT NULL CONSTRAINT PK_RestoreAuditLogs PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            EnvironmentId TEXT NOT NULL,
            EnvironmentKey TEXT NOT NULL,
            RestoreType TEXT NOT NULL,
            SourceCommitSha TEXT NOT NULL,
            NewCommitSha TEXT NULL,
            FilePath TEXT NULL,
            CreatedAt TEXT NOT NULL,
            Status TEXT NOT NULL,
            Warnings TEXT NULL,
            Errors TEXT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS IX_RestoreAuditLogs_WorkspaceId_EnvironmentId_CreatedAt ON RestoreAuditLogs (WorkspaceId, EnvironmentId, CreatedAt)"
    };

    foreach (var command in commands)
    {
        await dbContext.Database.ExecuteSqlRawAsync(command);
    }
}

static async Task EnsureIteration10SchemaAsync(AppDbContext dbContext)
{
    var commands = new[]
    {
        """
        CREATE TABLE IF NOT EXISTS EnvironmentN8nApiConfigs (
            EnvironmentId TEXT NOT NULL CONSTRAINT PK_EnvironmentN8nApiConfigs PRIMARY KEY,
            Enabled INTEGER NOT NULL,
            BaseUrl TEXT NOT NULL,
            DataTablesPath TEXT NOT NULL,
            ApiKey TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS DataTableSnapshots (
            Id TEXT NOT NULL CONSTRAINT PK_DataTableSnapshots PRIMARY KEY,
            WorkspaceId TEXT NOT NULL,
            EnvironmentId TEXT NOT NULL,
            EnvironmentKey TEXT NOT NULL,
            ExternalId TEXT NOT NULL,
            Name TEXT NOT NULL,
            ColumnsJson TEXT NOT NULL,
            RowCount INTEGER NULL,
            LastSyncedAt TEXT NOT NULL
        )
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_DataTableSnapshots_EnvironmentId_ExternalId ON DataTableSnapshots (EnvironmentId, ExternalId)",
        "CREATE INDEX IF NOT EXISTS IX_DataTableSnapshots_WorkspaceId_EnvironmentKey_Name ON DataTableSnapshots (WorkspaceId, EnvironmentKey, Name)"
    };
    foreach (var command in commands) await dbContext.Database.ExecuteSqlRawAsync(command);
}

static async Task EnsureIteration11SchemaAsync(AppDbContext dbContext)
{
    try { await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE EnvironmentN8nApiConfigs ADD COLUMN DataTablesWritePathTemplate TEXT NULL"); }
    catch (Exception ex) when (ex.GetBaseException().Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
}

static async Task EnsureIteration12SchemaAsync(AppDbContext dbContext)
{
    await dbContext.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS DataTableDeploymentAudits (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, SourceEnvironmentKey TEXT NOT NULL, TargetEnvironmentKey TEXT NOT NULL, Status TEXT NOT NULL, TableIdsJson TEXT NOT NULL, CreatedAt TEXT NOT NULL)");
}

static async Task EnsureIteration13SchemaAsync(AppDbContext dbContext)
{
    await dbContext.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS LocalUsers (Id TEXT NOT NULL PRIMARY KEY, UserName TEXT NOT NULL, PasswordHash TEXT NOT NULL, Role TEXT NOT NULL, IsEnabled INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)");
    await dbContext.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalUsers_UserName ON LocalUsers (UserName)");
    try { await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE EnvironmentN8nApiConfigs ADD COLUMN WorkflowApiPath TEXT NOT NULL DEFAULT '/api/v1/workflows'"); }
    catch (Exception ex) when (ex.GetBaseException().Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
    try { await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE PromotionAuditLogs ADD COLUMN ActorUserName TEXT NULL"); }
    catch (Exception ex) when (ex.GetBaseException().Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
    try { await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE DataTableDeploymentAudits ADD COLUMN ActorUserName TEXT NULL"); }
    catch (Exception ex) when (ex.GetBaseException().Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase)) { }
}

static async Task EnsureIteration14SchemaAsync(AppDbContext dbContext)
{
    await dbContext.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS DataTableMappings (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL, SourceEnvironmentId TEXT NOT NULL, TargetEnvironmentId TEXT NOT NULL, SourceTableId TEXT NOT NULL, TargetTableId TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)");
    await dbContext.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_DataTableMappings_WorkspaceId_SourceEnvironmentId_TargetEnvironmentId_SourceTableId ON DataTableMappings (WorkspaceId, SourceEnvironmentId, TargetEnvironmentId, SourceTableId)");
}
