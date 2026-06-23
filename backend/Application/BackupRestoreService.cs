using System.IO.Compression;
using System.Text.Json;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class BackupRestoreService
{
    private readonly IEnvironmentService _environmentService;
    private readonly IWorkflowMetadataService _metadataService;
    private readonly ICredentialInventoryService _credentialInventoryService;
    private readonly IGitRepositoryService _gitRepositoryService;
    private readonly IRestoreAuditService _auditService;
    private readonly WorkflowNormalizer _normalizer;
    private readonly WorkflowCredentialScanner _credentialScanner;
    private readonly WorkflowSemanticDiffService _semanticDiffService;

    public BackupRestoreService(
        IEnvironmentService environmentService,
        IWorkflowMetadataService metadataService,
        ICredentialInventoryService credentialInventoryService,
        IGitRepositoryService gitRepositoryService,
        IRestoreAuditService auditService,
        WorkflowNormalizer normalizer,
        WorkflowCredentialScanner credentialScanner,
        WorkflowSemanticDiffService semanticDiffService)
    {
        _environmentService = environmentService;
        _metadataService = metadataService;
        _credentialInventoryService = credentialInventoryService;
        _gitRepositoryService = gitRepositoryService;
        _auditService = auditService;
        _normalizer = normalizer;
        _credentialScanner = credentialScanner;
        _semanticDiffService = semanticDiffService;
    }

    public async Task<IReadOnlyList<CommitFileItemDto>> ListCommitFilesAsync(string environmentKey, string commitSha, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        EnsureCommitExists(context.Workspace.RepoPath, commitSha);
        return _gitRepositoryService.ReadWorkflowFilesFromCommit(context.Workspace.RepoPath, commitSha, parent: false)
            .OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase)
            .Select(file => new CommitFileItemDto(file.Key, Path.GetFileName(file.Key), System.Text.Encoding.UTF8.GetByteCount(file.Value)))
            .ToArray();
    }

    public async Task<CommitFileContentDto> GetCommitFileContentAsync(string environmentKey, string commitSha, string filePath, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var path = NormalizeWorkflowPath(filePath);
        var content = _gitRepositoryService.ReadWorkflowFileFromCommit(context.Workspace.RepoPath, commitSha, path)
            ?? throw new WorkflowImportException($"Workflow file '{path}' was not found at commit '{Short(commitSha)}'.");
        return new CommitFileContentDto(commitSha, path, content);
    }

    public async Task<RestorePreviewDto> PreviewEnvironmentRestoreAsync(string environmentKey, string commitSha, CancellationToken cancellationToken)
    {
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        _gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        var selectedCommit = EnsureCommitExists(context.Workspace.RepoPath, commitSha);
        var currentCommit = _gitRepositoryService.GetRecentCommits(context.Workspace.RepoPath, context.Environment.GitBranch, 1).FirstOrDefault();
        var selectedFiles = _gitRepositoryService.ReadWorkflowFilesFromCommit(context.Workspace.RepoPath, commitSha, parent: false);
        var currentFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
        var filesToAdd = selectedFiles.Keys.Except(currentFiles.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(path => path).ToArray();
        var filesToModify = selectedFiles
            .Where(file => currentFiles.TryGetValue(file.Key, out var current) && !Same(current, file.Value))
            .Select(file => file.Key)
            .OrderBy(path => path)
            .ToArray();
        var filesThatWouldBeDeleted = currentFiles.Keys.Except(selectedFiles.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(path => path).ToArray();
        var filesToRestore = filesToAdd.Concat(filesToModify).OrderBy(path => path).ToArray();
        var warnings = BuildRestoreWarnings(filesThatWouldBeDeleted);

        return new RestorePreviewDto(
            selectedCommit,
            currentCommit,
            filesToRestore,
            filesToAdd,
            filesToModify,
            filesThatWouldBeDeleted,
            warnings,
            _semanticDiffService.CompareWorkflowFiles(currentFiles, selectedFiles, context.Environment.Key, Short(commitSha)));
    }

    public async Task<RestoreWorkflowResult> RestoreWorkflowAsync(string environmentKey, RestoreWorkflowRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmation)
        {
            throw new WorkflowImportException("Confirmation is required to restore a workflow.");
        }

        var commitSha = RequireValue(request.CommitSha, "Commit SHA is required.");
        var filePath = NormalizeWorkflowPath(RequireValue(request.FilePath, "Workflow file path is required."));
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        try
        {
            _gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
            EnsureCommitExists(context.Workspace.RepoPath, commitSha);
            var sourceContent = _gitRepositoryService.ReadWorkflowFileFromCommit(context.Workspace.RepoPath, commitSha, filePath)
                ?? throw new WorkflowImportException($"Workflow file '{filePath}' was not found at commit '{Short(commitSha)}'.");
            var currentFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
            var normalized = NormalizeWorkflowContent(filePath, sourceContent);
            var targetPath = Path.Combine(context.Workspace.RepoPath, filePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, normalized, cancellationToken);
            await UpsertMetadataAndCredentialsAsync(context, filePath, normalized, cancellationToken);

            var commit = await _gitRepositoryService.CommitChangesAsync(
                context.Workspace.RepoPath,
                context.Environment.Key,
                [filePath],
                $"Restore workflow {filePath} from {Short(commitSha)}",
                cancellationToken);
            await RecordAuditAsync(context, "workflow", commitSha, commit.CommitSha, filePath, "succeeded", [], [], cancellationToken);

            return new RestoreWorkflowResult(
                context.Environment.Key,
                commitSha,
                filePath,
                commit.CommitCreated,
                commit.CommitSha,
                commit.CommitMessage,
                [],
                _semanticDiffService.CompareWorkflowContent(
                    currentFiles.TryGetValue(filePath, out var current) ? current : null,
                    normalized,
                    filePath,
                    filePath));
        }
        catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException)
        {
            await RecordAuditAsync(context, "workflow", commitSha, null, filePath, "failed", [], [ex.Message], cancellationToken);
            throw;
        }
    }

    public async Task<RestoreEnvironmentResult> RestoreEnvironmentAsync(string environmentKey, RestoreEnvironmentRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmation)
        {
            throw new WorkflowImportException("Confirmation is required to restore an environment.");
        }

        var commitSha = RequireValue(request.CommitSha, "Commit SHA is required.");
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        try
        {
            _gitRepositoryService.EnsureBranch(context.Workspace.RepoPath, context.Environment.GitBranch);
            var preview = await PreviewEnvironmentRestoreAsync(environmentKey, commitSha, cancellationToken);
            var selectedFiles = _gitRepositoryService.ReadWorkflowFilesFromCommit(context.Workspace.RepoPath, commitSha, parent: false);
            var changedPaths = new HashSet<string>(preview.FilesToRestore, StringComparer.OrdinalIgnoreCase);
            var deleted = request.IncludeDeletedFiles ? preview.FilesThatWouldBeDeleted : [];

            foreach (var file in preview.FilesToRestore)
            {
                var normalized = NormalizeWorkflowContent(file, selectedFiles[file]);
                var targetPath = Path.Combine(context.Workspace.RepoPath, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllTextAsync(targetPath, normalized, cancellationToken);
                await UpsertMetadataAndCredentialsAsync(context, file, normalized, cancellationToken);
            }

            foreach (var file in deleted)
            {
                var targetPath = Path.Combine(context.Workspace.RepoPath, file.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                    changedPaths.Add(file);
                }
            }

            var warnings = request.IncludeDeletedFiles ? preview.Warnings : BuildRestoreWarnings(preview.FilesThatWouldBeDeleted);
            var commit = await _gitRepositoryService.CommitChangesAsync(
                context.Workspace.RepoPath,
                context.Environment.Key,
                changedPaths,
                $"Restore environment {context.Environment.Key} to {Short(commitSha)}",
                cancellationToken);
            await RecordAuditAsync(context, "environment", commitSha, commit.CommitSha, null, "succeeded", warnings, [], cancellationToken);

            return new RestoreEnvironmentResult(
                context.Environment.Key,
                commitSha,
                commit.CommitCreated,
                commit.CommitSha,
                commit.CommitMessage,
                preview.FilesToRestore.Count,
                preview.FilesToAdd.Count,
                preview.FilesToModify.Count,
                deleted.Count,
                warnings,
                preview);
        }
        catch (Exception ex) when (ex is WorkflowImportException or InvalidOperationException)
        {
            await RecordAuditAsync(context, "environment", commitSha, null, null, "failed", [], [ex.Message], cancellationToken);
            throw;
        }
    }

    public async Task<BackupCreateResult> CreateBackupFromCommitAsync(string environmentKey, BackupFromCommitRequest request, string appDataPath, CancellationToken cancellationToken)
    {
        var commitSha = RequireValue(request.CommitSha, "Commit SHA is required.");
        var context = await _environmentService.GetByKeyAsync(environmentKey, cancellationToken);
        var commit = EnsureCommitExists(context.Workspace.RepoPath, commitSha);
        var files = _gitRepositoryService.ReadWorkflowFilesFromCommit(context.Workspace.RepoPath, commitSha, parent: false);
        if (files.Count == 0)
        {
            throw new WorkflowImportException($"Commit '{Short(commitSha)}' has no workflow files to back up.");
        }

        var warnings = new List<string>();
        var backupRoot = Path.Combine(appDataPath, "backups");
        Directory.CreateDirectory(backupRoot);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{context.Environment.Key}-{Short(commitSha)}";
        var fileName = $"{id}.zip";
        var outputPath = Path.Combine(backupRoot, fileName);
        using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
        {
            foreach (var file in files.OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(file.Value);
            }

            if (request.IncludeMetadata)
            {
                var manifest = JsonSerializer.Serialize(new
                {
                    environmentKey = context.Environment.Key,
                    commitSha = commit.Sha,
                    commitDate = commit.When,
                    generatedAt = DateTimeOffset.UtcNow,
                    appVersion = typeof(BackupRestoreService).Assembly.GetName().Version?.ToString()
                }, new JsonSerializerOptions { WriteIndented = true });
                var entry = archive.CreateEntry("backup-manifest.json", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(manifest);
            }
        }

        if (request.IncludeDatabaseSnapshot)
        {
            warnings.Add("Database snapshot was requested but is not included because live SQLite backup is not configured for this operation.");
        }

        var info = new FileInfo(outputPath);
        return new BackupCreateResult(id, fileName, outputPath, $"/api/backups/{id}/download", info.Length, warnings);
    }

    public IReadOnlyList<BackupDto> ListBackups(string appDataPath)
    {
        var root = Path.Combine(appDataPath, "backups");
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*.zip")
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new BackupDto(Path.GetFileNameWithoutExtension(path), info.Name, path, info.Length, info.CreationTimeUtc);
            })
            .OrderByDescending(backup => backup.CreatedAt)
            .ToArray();
    }

    public string GetBackupPath(string appDataPath, string backupId)
    {
        var safeId = Path.GetFileNameWithoutExtension(backupId);
        var path = Path.Combine(appDataPath, "backups", $"{safeId}.zip");
        if (!File.Exists(path))
        {
            throw new WorkflowImportException($"Backup '{backupId}' was not found.");
        }

        return path;
    }

    public void DeleteBackup(string appDataPath, string backupId)
    {
        var path = GetBackupPath(appDataPath, backupId);
        File.Delete(path);
    }

    private static IReadOnlyList<string> BuildRestoreWarnings(IReadOnlyList<string> deletable) =>
        deletable.Count == 0
            ? ["Restores create a new commit and do not rewrite Git history.", "Credential references are scanned, but decrypted credential secrets are not restored."]
            : [
                "Restores create a new commit and do not rewrite Git history.",
                "Credential references are scanned, but decrypted credential secrets are not restored.",
                $"{deletable.Count} current workflow file(s) are absent from the selected commit and will only be deleted when deletion is explicitly enabled."
            ];

    private GitCommitDto EnsureCommitExists(string repoPath, string commitSha) =>
        _gitRepositoryService.GetCommit(repoPath, commitSha)
        ?? throw new WorkflowImportException($"Commit '{Short(commitSha)}' was not found.");

    private async Task UpsertMetadataAndCredentialsAsync(EnvironmentContext context, string filePath, string content, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(content);
        var workflow = document.RootElement;
        var name = GetString(workflow, "name") ?? GetString(workflow, "id") ?? Path.GetFileNameWithoutExtension(filePath);
        var nodesCount = workflow.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array ? nodes.GetArrayLength() : 0;
        var update = new WorkflowMetadataUpdate(
            context.Workspace.Id,
            context.Environment.Id,
            context.Environment.Key,
            GetString(workflow, "id"),
            name,
            workflow.TryGetProperty("active", out var active) && active.ValueKind is JsonValueKind.True or JsonValueKind.False && active.GetBoolean(),
            nodesCount,
            GetDate(workflow, "createdAt"),
            GetDate(workflow, "updatedAt"),
            filePath,
            DateTimeOffset.UtcNow);
        await _metadataService.UpsertAsync(update, cancellationToken);
        await _credentialInventoryService.ReplaceWorkflowReferencesAsync(
            context.Workspace.Id,
            context.Environment.Id,
            context.Environment.Key,
            filePath,
            _credentialScanner.Scan(workflow, filePath, update.ExternalId, update.Name),
            cancellationToken);
    }

    private string NormalizeWorkflowContent(string path, string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            throw new WorkflowImportException($"Workflow file '{path}' is not a valid n8n workflow JSON file.");
        }

        return _normalizer.Normalize(root);
    }

    private Task RecordAuditAsync(
        EnvironmentContext context,
        string restoreType,
        string sourceCommitSha,
        string? newCommitSha,
        string? filePath,
        string status,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors,
        CancellationToken cancellationToken) =>
        _auditService.RecordAsync(new RestoreAuditCreate(
            context.Workspace.Id,
            context.Environment.Id,
            context.Environment.Key,
            restoreType,
            sourceCommitSha,
            newCommitSha,
            filePath,
            status,
            warnings,
            errors), cancellationToken);

    private static string NormalizeWorkflowPath(string filePath)
    {
        var path = filePath.Trim().Replace('\\', '/');
        if (!path.StartsWith("workflows/", StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || path.Split('/').Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment)))
        {
            throw new WorkflowImportException("Only workflow JSON paths under workflows/ can be restored or downloaded.");
        }

        return path;
    }

    private static string RequireValue(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new WorkflowImportException(message) : value.Trim();

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);

    private static string Short(string sha) => sha[..Math.Min(10, sha.Length)];

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetDate(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
