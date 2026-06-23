using System.Text.Json;
using System.Text.Json.Nodes;
using Application.Contracts;
using Application.Models;

namespace Application;

public sealed class PromotionService
{
    private readonly IEnvironmentService _environmentService;
    private readonly IGitRepositoryService _gitRepositoryService;
    private readonly ICredentialMappingReader _mappingReader;
    private readonly IPromotionAuditService _auditService;
    private readonly IPromotionBaselineService _baselineService;
    private readonly IWorkflowMetadataService _metadataService;
    private readonly ICredentialInventoryService _credentialInventoryService;
    private readonly WorkflowCredentialScanner _scanner;
    private readonly WorkflowNormalizer _normalizer;
    private readonly WorkflowSemanticDiffService _semanticDiffService;

    public PromotionService(
        IEnvironmentService environmentService,
        IGitRepositoryService gitRepositoryService,
        ICredentialMappingReader mappingReader,
        IPromotionAuditService auditService,
        IPromotionBaselineService baselineService,
        IWorkflowMetadataService metadataService,
        ICredentialInventoryService credentialInventoryService,
        WorkflowCredentialScanner scanner,
        WorkflowNormalizer normalizer,
        WorkflowSemanticDiffService semanticDiffService)
    {
        _environmentService = environmentService;
        _gitRepositoryService = gitRepositoryService;
        _mappingReader = mappingReader;
        _auditService = auditService;
        _baselineService = baselineService;
        _metadataService = metadataService;
        _credentialInventoryService = credentialInventoryService;
        _scanner = scanner;
        _normalizer = normalizer;
        _semanticDiffService = semanticDiffService;
    }

    public async Task<PromotionPlanDto> GeneratePlanAsync(
        string sourceKey,
        string targetKey,
        IReadOnlyCollection<string>? selectedWorkflowFiles,
        bool includeDeletions,
        bool recordAudit,
        CancellationToken cancellationToken)
    {
        var source = await _environmentService.GetByKeyAsync(sourceKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(targetKey, cancellationToken);
        if (source.Environment.Id == target.Environment.Id)
        {
            throw new WorkflowImportException("Source and target environments must be different.");
        }

        var sourceFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(source.Workspace.RepoPath, source.Environment.GitBranch);
        var targetFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(target.Workspace.RepoPath, target.Environment.GitBranch);
        var mergeInfo = _gitRepositoryService.GetMergeBaseInfo(source.Workspace.RepoPath, source.Environment.GitBranch, target.Environment.GitBranch);
        var baseline = await _baselineService.GetAsync(sourceKey, targetKey, cancellationToken);
        var baseFiles = baseline is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : _gitRepositoryService.ReadWorkflowFilesFromCommit(source.Workspace.RepoPath, baseline.CommitSha, parent: false);
        var mappings = await _mappingReader.GetMappingsAsync(source.Environment.Id, target.Environment.Id, cancellationToken);
        var changes = BuildChanges(
            source.Environment.Key,
            target.Environment.Key,
            mergeInfo,
            sourceFiles,
            targetFiles,
            baseFiles,
            _semanticDiffService,
            baseline is not null);
        var appliedManualMerges = await _auditService.ListAppliedManualMergesAsync(
            source.Workspace.Id,
            source.Environment.Id,
            target.Environment.Id,
            cancellationToken);
        changes = ApplyManualMergeStatus(changes, appliedManualMerges);
        var resolutions = BuildResolutionMap(changes, null, selectedWorkflowFiles, includeDeletions);

        var warnings = new List<string>();
        var blockingErrors = new List<string>();
        var missingMappings = new List<PromotionMissingMappingDto>();
        var validation = ValidateResolutions(changes, resolutions, sourceFiles, mappings, requireResolvedConflicts: false, confirmDeletions: false);
        warnings.AddRange(validation.Warnings);
        blockingErrors.AddRange(validation.BlockingErrors);
        missingMappings.AddRange(validation.MissingMappings);

        foreach (var change in changes)
        {
            if (change.ChangeType == "deleted")
            {
                warnings.Add(includeDeletions
                    ? $"Deleted workflow '{change.WorkflowName}' will be removed from target because include deletions is enabled."
                    : $"Deleted workflow '{change.WorkflowName}' is not selected by default. Enable include deletions to remove it from target.");
            }

            if (change.ChangeType == "conflict")
            {
                warnings.Add($"Workflow '{change.WorkflowName}' has changed independently in target and must be skipped or resolved manually.");
            }
        }

        if (recordAudit)
        {
            await _auditService.RecordAsync(new PromotionAuditCreate(
                source.Workspace.Id,
                source.Environment.Id,
                source.Environment.Key,
                target.Environment.Id,
                target.Environment.Key,
                "planned",
                null,
                BuildAuditSummary(warnings, blockingErrors),
                null), cancellationToken);
        }

        if (blockingErrors.Count == 0)
        {
            PrepareRemappedFiles(source.Workspace.RepoPath, source.Environment.Key, target.Environment.Key, sourceFiles, changes, resolutions, mappings);
        }

        var conflictCount = changes.Count(change => change.IsConflict);
        return new PromotionPlanDto(
            new PromotionEnvironmentDto(source.Environment.Id, source.Environment.Name, source.Environment.Key, source.Environment.GitBranch),
            new PromotionEnvironmentDto(target.Environment.Id, target.Environment.Name, target.Environment.Key, target.Environment.GitBranch),
            DateTimeOffset.UtcNow,
            mergeInfo.SourceCommitSha,
            mergeInfo.TargetCommitSha,
            baseline?.CommitSha,
            changes,
            validation.Required,
            validation.Found,
            conflictCount,
            conflictCount,
            missingMappings,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blockingErrors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            baseline);
    }

    public async Task<PromotionApplyResult> ApplyAsync(PromotionApplyRequest request, CancellationToken cancellationToken)
    {
        if (!request.Confirmation)
        {
            throw new WorkflowImportException("Promotion confirmation is required.");
        }

        var source = await _environmentService.GetByKeyAsync(request.SourceEnvironmentKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(request.TargetEnvironmentKey, cancellationToken);
        var plan = await GeneratePlanAsync(
            request.SourceEnvironmentKey,
            request.TargetEnvironmentKey,
            request.SelectedWorkflowFiles,
            request.IncludeDeletions,
            recordAudit: false,
            cancellationToken);

        var sourceFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(source.Workspace.RepoPath, source.Environment.GitBranch);
        var targetFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(target.Workspace.RepoPath, target.Environment.GitBranch);
        var mappings = await _mappingReader.GetMappingsAsync(source.Environment.Id, target.Environment.Id, cancellationToken);
        var resolutions = BuildResolutionMap(plan.WorkflowChanges, request.WorkflowResolutions, request.SelectedWorkflowFiles, request.IncludeDeletions);
        var validation = ValidateResolutions(plan.WorkflowChanges, resolutions, sourceFiles, mappings, requireResolvedConflicts: true, request.ConfirmDeletions);
        if (validation.BlockingErrors.Count > 0)
        {
            await RecordFailedAsync(source, target, validation.BlockingErrors, cancellationToken);
            throw new WorkflowImportException(string.Join(Environment.NewLine, validation.BlockingErrors));
        }

        var prepared = new List<PromotionPreparedFile>();
        var appliedFiles = new List<string>();
        var skippedFiles = new List<string>();
        var deletedFiles = new List<string>();

        foreach (var change in plan.WorkflowChanges)
        {
            var resolution = ResolveAction(change, resolutions);
            if (resolution is "skip")
            {
                skippedFiles.Add(change.WorkflowFilePath);
                continue;
            }

            if (resolution is "keep-target")
            {
                continue;
            }

            if (resolution is "delete-target")
            {
                deletedFiles.Add(change.WorkflowFilePath);
                appliedFiles.Add(change.WorkflowFilePath);
                continue;
            }

            if (resolution is "use-source")
            {
                var content = RemapAndNormalize(change.WorkflowFilePath, sourceFiles[change.WorkflowFilePath], mappings);
                prepared.Add(new PromotionPreparedFile(change.WorkflowFilePath, content));
                appliedFiles.Add(change.WorkflowFilePath);
            }
        }

        if (appliedFiles.Count == 0)
        {
            throw new WorkflowImportException("No promotable workflow files were selected.");
        }

        _gitRepositoryService.EnsureBranch(target.Workspace.RepoPath, target.Environment.GitBranch);
        foreach (var file in prepared)
        {
            var targetPath = Path.Combine(target.Workspace.RepoPath, file.WorkflowFilePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllTextAsync(targetPath, file.Content, cancellationToken);
            await UpsertTargetMetadataAsync(target.Workspace.Id, target.Environment.Id, target.Environment.Key, file.WorkflowFilePath, file.Content, cancellationToken);
        }

        foreach (var deletedPath in deletedFiles)
        {
            var targetPath = Path.Combine(target.Workspace.RepoPath, deletedPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
        }

        var commit = await _gitRepositoryService.CommitPromotionChangesAsync(
            target.Workspace.RepoPath,
            source.Environment.Key,
            target.Environment.Key,
            appliedFiles,
            cancellationToken);

        await _auditService.RecordAsync(new PromotionAuditCreate(
            target.Workspace.Id,
            source.Environment.Id,
            source.Environment.Key,
            target.Environment.Id,
            target.Environment.Key,
            "applied",
            commit.CommitSha,
            BuildAuditSummary(
                validation.Warnings,
                validation.BlockingErrors,
                resolutions,
                plan.ConflictCount,
                validation.UnresolvedConflictCount,
                "not-requested",
                appliedFiles.Count,
                deletedFiles.Count,
                skippedFiles.Count),
            DateTimeOffset.UtcNow), cancellationToken);

        if (!string.IsNullOrWhiteSpace(commit.CommitSha))
        {
            await _baselineService.SetAsync(new PromotionComparisonBaselineRequest(
                source.Environment.Key,
                target.Environment.Key,
                commit.CommitSha,
                $"Promotion {commit.CommitSha[..Math.Min(8, commit.CommitSha.Length)]}"), cancellationToken);
        }

        return new PromotionApplyResult(
            commit.CommitCreated,
            commit.CommitSha,
            commit.CommitMessage,
            appliedFiles.Count,
            appliedFiles,
            skippedFiles,
            deletedFiles,
            validation.Warnings,
            commit.Message);
    }

    public async Task<PromotionMergePreviewDto> PreviewMergeAsync(PromotionMergePreviewRequest request, CancellationToken cancellationToken)
    {
        var source = await _environmentService.GetByKeyAsync(request.SourceEnvironmentKey, cancellationToken);
        var target = await _environmentService.GetByKeyAsync(request.TargetEnvironmentKey, cancellationToken);
        var plan = await GeneratePlanAsync(
            request.SourceEnvironmentKey,
            request.TargetEnvironmentKey,
            selectedWorkflowFiles: null,
            request.IncludeDeletions,
            recordAudit: false,
            cancellationToken);
        var sourceFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(source.Workspace.RepoPath, source.Environment.GitBranch);
        var targetFiles = _gitRepositoryService.ReadWorkflowFilesFromBranch(target.Workspace.RepoPath, target.Environment.GitBranch);
        var mappings = await _mappingReader.GetMappingsAsync(source.Environment.Id, target.Environment.Id, cancellationToken);
        var resolutions = BuildResolutionMap(plan.WorkflowChanges, request.WorkflowResolutions, null, request.IncludeDeletions);
        var validation = ValidateResolutions(plan.WorkflowChanges, resolutions, sourceFiles, mappings, requireResolvedConflicts: true, request.ConfirmDeletions);

        var finalFiles = targetFiles.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var write = new List<string>();
        var keep = new List<string>();
        var skip = new List<string>();
        var delete = new List<string>();

        foreach (var change in plan.WorkflowChanges)
        {
            var resolution = ResolveAction(change, resolutions);
            switch (resolution)
            {
                case "use-source":
                    write.Add(change.WorkflowFilePath);
                    if (sourceFiles.TryGetValue(change.WorkflowFilePath, out var sourceContent))
                    {
                        finalFiles[change.WorkflowFilePath] = validation.BlockingErrors.Count == 0
                            ? RemapAndNormalize(change.WorkflowFilePath, sourceContent, mappings)
                            : sourceContent;
                    }
                    break;
                case "delete-target":
                    delete.Add(change.WorkflowFilePath);
                    finalFiles.Remove(change.WorkflowFilePath);
                    break;
                case "skip":
                    skip.Add(change.WorkflowFilePath);
                    break;
                case "keep-target":
                    keep.Add(change.WorkflowFilePath);
                    break;
            }
        }

        return new PromotionMergePreviewDto(
            write,
            keep,
            skip,
            delete,
            validation.Warnings,
            validation.BlockingErrors,
            _semanticDiffService.CompareWorkflowFiles(targetFiles, finalFiles, target.Environment.Key, "resolved-preview"),
            new PromotionCredentialMappingSummaryDto(validation.Required, validation.Found, validation.MissingMappings),
            write.Count + delete.Count);
    }

    private static IReadOnlyList<PromotionWorkflowChangeDto> BuildChanges(
        string sourceEnvironmentKey,
        string targetEnvironmentKey,
        GitMergeBaseInfo mergeInfo,
        IReadOnlyDictionary<string, string> sourceFiles,
        IReadOnlyDictionary<string, string> targetFiles,
        IReadOnlyDictionary<string, string> baseFiles,
        WorkflowSemanticDiffService semanticDiffService,
        bool hasBaseline)
    {
        var allPaths = sourceFiles.Keys
            .Concat(targetFiles.Keys)
            .Concat(baseFiles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        var changes = new List<PromotionWorkflowChangeDto>();

        foreach (var path in allPaths)
        {
            var sourceExists = sourceFiles.TryGetValue(path, out var sourceContent);
            var targetExists = targetFiles.TryGetValue(path, out var targetContent);
            var baseExists = baseFiles.TryGetValue(path, out var baseContent);
            var sourceChanged = sourceExists && (!baseExists || !Same(sourceContent, baseContent));
            var targetChanged = targetExists && (!baseExists || !Same(targetContent, baseContent));
            var changeType = "unchanged";
            var summary = hasBaseline ? "No source changes since the selected baseline." : "Source and target workflow files are identical.";

            if (!hasBaseline)
            {
                if (sourceExists && !targetExists)
                {
                    changeType = "source-only";
                    summary = "Workflow exists only in source.";
                }
                else if (!sourceExists && targetExists)
                {
                    changeType = "target-only";
                    summary = "Workflow exists only in target.";
                }
                else if (sourceExists && targetExists && !Same(sourceContent, targetContent))
                {
                    changeType = "different";
                    summary = "Source and target workflow files differ. Choose the result to keep.";
                }
            }
            else if (sourceExists && !targetExists && !baseExists)
            {
                changeType = "added";
                summary = "Workflow exists only in source.";
            }
            else if (sourceExists && targetExists && Same(sourceContent, targetContent))
            {
                changeType = "unchanged";
            }
            else if (!sourceExists && targetExists && baseExists)
            {
                changeType = targetChanged ? "conflict" : "deleted";
                summary = targetChanged ? "Source deleted the workflow, but target changed it independently." : "Workflow was deleted from source.";
            }
            else if (sourceExists && targetExists && sourceChanged && targetChanged)
            {
                changeType = "conflict";
                summary = "Source and target changed this workflow after their common ancestor.";
            }
            else if (sourceExists && sourceChanged)
            {
                changeType = targetExists ? "modified" : "added";
                summary = targetExists ? "Source workflow changed." : "Workflow exists only in source.";
            }
            else if (!sourceExists && targetExists)
            {
                changeType = "unchanged";
                summary = "Workflow exists only in target and is not part of source promotion.";
            }

            var sourceVsTarget = sourceExists || targetExists
                ? semanticDiffService.CompareWorkflowContent(
                    targetExists ? targetContent : null,
                    sourceExists ? sourceContent : null,
                    targetExists ? path : null,
                    sourceExists ? path : null)
                : null;
            var semanticSummary = sourceVsTarget is null
                ? null
                : new PromotionSemanticSummaryDto(
                    sourceVsTarget.Summary.AddedNodes,
                    sourceVsTarget.Summary.RemovedNodes,
                    sourceVsTarget.Summary.ModifiedNodes,
                    sourceVsTarget.Summary.ChangedCredentials,
                    sourceVsTarget.Summary.ChangedConnections,
                    sourceVsTarget.Summary.ChangedWorkflowSettings);
            var isConflict = hasBaseline && changeType == "conflict";
            var conflictReason = isConflict ? summary : null;
            var conflictDetails = isConflict
                ? new PromotionConflictDetailsDto(
                    path,
                    sourceExists ? ReadWorkflowString(sourceContent!, "id") : targetExists ? ReadWorkflowString(targetContent!, "id") : null,
                    sourceExists
                        ? ReadWorkflowString(sourceContent!, "name") ?? Path.GetFileNameWithoutExtension(path)
                        : targetExists
                            ? ReadWorkflowString(targetContent!, "name") ?? Path.GetFileNameWithoutExtension(path)
                            : Path.GetFileNameWithoutExtension(path),
                    sourceEnvironmentKey,
                    targetEnvironmentKey,
                    mergeInfo.SourceCommitSha,
                    mergeInfo.TargetCommitSha,
                    mergeInfo.BaseCommitSha,
                    semanticDiffService.CompareWorkflowContent(baseExists ? baseContent : null, sourceExists ? sourceContent : null, baseExists ? path : null, sourceExists ? path : null),
                    semanticDiffService.CompareWorkflowContent(baseExists ? baseContent : null, targetExists ? targetContent : null, baseExists ? path : null, targetExists ? path : null),
                    sourceVsTarget,
                    summary)
                : null;
            var resolution = DefaultResolution(changeType);
            var availableResolutions = AvailableResolutions(changeType);

            changes.Add(new PromotionWorkflowChangeDto(
                path,
                sourceExists ? ReadWorkflowString(sourceContent!, "id") : null,
                sourceExists ? ReadWorkflowString(sourceContent!, "name") ?? Path.GetFileNameWithoutExtension(path) : Path.GetFileNameWithoutExtension(path),
                changeType,
                summary,
                changeType is "added" or "modified" or "source-only" or "different",
                semanticSummary,
                sourceVsTarget,
                resolution,
                availableResolutions,
                isConflict,
                conflictReason,
                conflictDetails));
        }

        return changes;
    }

    private static IReadOnlyList<PromotionWorkflowChangeDto> ApplyManualMergeStatus(
        IReadOnlyList<PromotionWorkflowChangeDto> changes,
        IReadOnlyList<AppliedManualMergeAuditEntry> appliedManualMerges)
    {
        var entries = appliedManualMerges.ToDictionary(entry => entry.WorkflowFilePath, StringComparer.OrdinalIgnoreCase);
        return changes.Select(change =>
        {
            if (!entries.TryGetValue(change.WorkflowFilePath, out var entry))
            {
                return change;
            }

            var commit = string.IsNullOrWhiteSpace(entry.CommitSha) ? string.Empty : $" Commit {entry.CommitSha[..Math.Min(8, entry.CommitSha.Length)]}.";
            return change with
            {
                ChangeType = "manual-merge",
                Summary = $"Manual merge applied to target on {entry.AppliedAt:yyyy-MM-dd HH:mm 'UTC'}.{commit}",
                IsSelectedByDefault = false,
                Resolution = "keep-target",
                AvailableResolutions = ["keep-target"],
                IsConflict = false,
                ConflictReason = null,
                ConflictDetails = null
            };
        }).ToArray();
    }

    private ResolutionValidationResult ValidateResolutions(
        IReadOnlyList<PromotionWorkflowChangeDto> changes,
        IReadOnlyDictionary<string, string> resolutions,
        IReadOnlyDictionary<string, string> sourceFiles,
        IReadOnlyList<CredentialEnvironmentPair> mappings,
        bool requireResolvedConflicts,
        bool confirmDeletions)
    {
        var warnings = new List<string>();
        var blockingErrors = new List<string>();
        var missingMappings = new List<PromotionMissingMappingDto>();
        var required = 0;
        var found = 0;
        var unresolvedConflictCount = 0;

        foreach (var change in changes)
        {
            var resolution = ResolveAction(change, resolutions);
            if (change.IsConflict && string.IsNullOrWhiteSpace(resolution))
            {
                unresolvedConflictCount++;
                if (requireResolvedConflicts)
                {
                    blockingErrors.Add($"Workflow '{change.WorkflowName}' has an unresolved conflict.");
                }
                continue;
            }

            if (resolution is null)
            {
                continue;
            }

            if (!change.AvailableResolutions.Contains(resolution, StringComparer.OrdinalIgnoreCase))
            {
                blockingErrors.Add($"Resolution '{resolution}' is not valid for workflow '{change.WorkflowName}'.");
                continue;
            }

            if (resolution == "delete-target" && !confirmDeletions)
            {
                blockingErrors.Add($"Deleting target workflow '{change.WorkflowName}' requires explicit deletion confirmation.");
            }

            if (resolution != "use-source" || !sourceFiles.TryGetValue(change.WorkflowFilePath, out var content))
            {
                continue;
            }

            foreach (var reference in ScanFile(change.WorkflowFilePath, content))
            {
                required++;
                var mapping = mappings.FirstOrDefault(item => Matches(item.Source, reference));
                if (mapping is null)
                {
                    missingMappings.Add(new PromotionMissingMappingDto(
                        change.WorkflowFilePath,
                        reference.WorkflowName,
                        reference.CredentialType,
                        reference.CredentialId,
                        reference.CredentialName));
                    blockingErrors.Add($"Missing mapping for credential '{reference.CredentialName ?? reference.CredentialId}' ({reference.CredentialType}) in workflow '{reference.WorkflowName}'.");
                    continue;
                }

                if (!string.Equals(mapping.Source.CredentialType, mapping.Target.CredentialType, StringComparison.OrdinalIgnoreCase))
                {
                    blockingErrors.Add($"Mapped credential '{mapping.Source.CredentialName ?? mapping.Source.CredentialId}' has type '{mapping.Source.CredentialType}', but target mapping uses '{mapping.Target.CredentialType}'.");
                    continue;
                }

                found++;
                if (string.IsNullOrWhiteSpace(mapping.Target.CredentialId) && string.IsNullOrWhiteSpace(mapping.Target.CredentialName))
                {
                    warnings.Add($"Target credential for logical mapping '{mapping.LogicalKey}' is missing both id and name.");
                }
            }
        }

        return new ResolutionValidationResult(
            required,
            found,
            unresolvedConflictCount,
            missingMappings,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blockingErrors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyDictionary<string, string> BuildResolutionMap(
        IReadOnlyList<PromotionWorkflowChangeDto> changes,
        IReadOnlyList<PromotionWorkflowResolutionDto>? requested,
        IReadOnlyCollection<string>? selectedWorkflowFiles,
        bool includeDeletions)
    {
        var map = changes
            .Where(change => !string.IsNullOrWhiteSpace(change.Resolution))
            .ToDictionary(change => change.WorkflowFilePath, change => change.Resolution!, StringComparer.OrdinalIgnoreCase);

        if (selectedWorkflowFiles is not null && selectedWorkflowFiles.Count > 0)
        {
            var selected = selectedWorkflowFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes)
            {
                if (change.IsConflict)
                {
                    map.Remove(change.WorkflowFilePath);
                    continue;
                }

                map[change.WorkflowFilePath] = selected.Contains(change.WorkflowFilePath)
                    ? change.ChangeType == "deleted" && includeDeletions ? "delete-target" : "use-source"
                    : "skip";
            }
        }

        if (requested is not null)
        {
            foreach (var item in requested.Where(item => !string.IsNullOrWhiteSpace(item.WorkflowFilePath) && !string.IsNullOrWhiteSpace(item.Resolution)))
            {
                map[item.WorkflowFilePath] = item.Resolution.Trim();
            }
        }

        return map;
    }

    private static string? ResolveAction(PromotionWorkflowChangeDto change, IReadOnlyDictionary<string, string> resolutions)
    {
        return resolutions.TryGetValue(change.WorkflowFilePath, out var resolution)
            ? NormalizeResolution(resolution)
            : change.Resolution;
    }

    private static string? DefaultResolution(string changeType)
    {
        return changeType switch
        {
            "added" or "modified" or "source-only" or "different" => "use-source",
            "deleted" or "target-only" => "keep-target",
            "unchanged" => "keep-target",
            _ => null
        };
    }

    private static IReadOnlyList<string> AvailableResolutions(string changeType)
    {
        return changeType is "deleted" or "target-only"
            ? ["delete-target", "keep-target", "skip"]
            : changeType == "unchanged"
                ? ["keep-target", "skip"]
                : ["use-source", "keep-target", "skip"];
    }

    private static string? NormalizeResolution(string? resolution)
    {
        return resolution?.Trim().ToLowerInvariant() switch
        {
            "use-source" => "use-source",
            "keep-target" => "keep-target",
            "skip" => "skip",
            "delete-target" => "delete-target",
            _ => resolution
        };
    }

    private IEnumerable<CredentialScanItem> ScanFile(string path, string content)
    {
        using var document = JsonDocument.Parse(content);
        foreach (var workflow in EnumerateWorkflowObjects(document.RootElement))
        {
            var workflowName = GetString(workflow, "name") ?? GetString(workflow, "id") ?? Path.GetFileNameWithoutExtension(path);
            foreach (var reference in _scanner.Scan(workflow, path, GetString(workflow, "id"), workflowName))
            {
                yield return reference;
            }
        }
    }

    private string RemapAndNormalize(string path, string content, IReadOnlyList<CredentialEnvironmentPair> mappings)
    {
        var root = JsonNode.Parse(content) ?? throw new WorkflowImportException($"Workflow file '{path}' could not be parsed.");
        RemapCredentials(root, mappings);
        using var document = JsonDocument.Parse(root.ToJsonString());
        return _normalizer.Normalize(document.RootElement);
    }

    private void PrepareRemappedFiles(
        string repoPath,
        string sourceKey,
        string targetKey,
        IReadOnlyDictionary<string, string> sourceFiles,
        IReadOnlyList<PromotionWorkflowChangeDto> changes,
        IReadOnlyDictionary<string, string> resolutions,
        IReadOnlyList<CredentialEnvironmentPair> mappings)
    {
        var root = Path.Combine(Path.GetTempPath(), "n8n-move-manager-promotions", $"{sourceKey}-to-{targetKey}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
        Directory.CreateDirectory(root);
        foreach (var change in changes.Where(change => ResolveAction(change, resolutions) == "use-source"))
        {
            var outputPath = Path.Combine(root, change.WorkflowFilePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, RemapAndNormalize(change.WorkflowFilePath, sourceFiles[change.WorkflowFilePath], mappings));
        }
    }

    private async Task UpsertTargetMetadataAsync(Guid workspaceId, Guid environmentId, string environmentKey, string filePath, string content, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(content);
        var workflow = document.RootElement;
        var name = GetString(workflow, "name") ?? GetString(workflow, "id") ?? Path.GetFileNameWithoutExtension(filePath);
        var nodesCount = workflow.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array ? nodes.GetArrayLength() : 0;
        var update = new WorkflowMetadataUpdate(
            workspaceId,
            environmentId,
            environmentKey,
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
            workspaceId,
            environmentId,
            environmentKey,
            filePath,
            _scanner.Scan(workflow, filePath, update.ExternalId, update.Name),
            cancellationToken);
    }

    private async Task RecordFailedAsync(EnvironmentContext source, EnvironmentContext target, IReadOnlyList<string> errors, CancellationToken cancellationToken)
    {
        await _auditService.RecordAsync(new PromotionAuditCreate(
            source.Workspace.Id,
            source.Environment.Id,
            source.Environment.Key,
            target.Environment.Id,
            target.Environment.Key,
            "failed",
            null,
            string.Join(Environment.NewLine, errors),
            null), cancellationToken);
    }

    private static void RemapCredentials(JsonNode node, IReadOnlyList<CredentialEnvironmentPair> mappings)
    {
        if (node is JsonObject obj)
        {
            if (obj["credentials"] is JsonObject credentials)
            {
                foreach (var property in credentials.ToArray())
                {
                    if (property.Value is JsonObject credential)
                    {
                        RemapCredentialObject(property.Key, credential, mappings);
                    }
                    else if (property.Value is JsonArray array)
                    {
                        foreach (var item in array.OfType<JsonObject>())
                        {
                            RemapCredentialObject(property.Key, item, mappings);
                        }
                    }
                }
            }

            foreach (var child in obj.Select(property => property.Value).OfType<JsonNode>())
            {
                RemapCredentials(child, mappings);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
            {
                RemapCredentials(child, mappings);
            }
        }
    }

    private static void RemapCredentialObject(string fallbackType, JsonObject credential, IReadOnlyList<CredentialEnvironmentPair> mappings)
    {
        var sourceType = credential["type"]?.GetValue<string>() ?? fallbackType;
        var sourceId = credential["id"]?.GetValue<string>();
        var sourceName = credential["name"]?.GetValue<string>();
        var mapping = mappings.FirstOrDefault(item =>
            string.Equals(item.Source.CredentialType, sourceType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Source.CredentialId ?? string.Empty, sourceId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Source.CredentialName ?? string.Empty, sourceName ?? string.Empty, StringComparison.OrdinalIgnoreCase));

        if (mapping is null)
        {
            return;
        }

        credential["id"] = mapping.Target.CredentialId;
        credential["name"] = mapping.Target.CredentialName;
        credential["type"] = mapping.Target.CredentialType;
    }

    private static bool Matches(EnvironmentCredentialSnapshot credential, CredentialScanItem reference)
    {
        return string.Equals(credential.CredentialType, reference.CredentialType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(credential.CredentialId ?? string.Empty, reference.CredentialId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(credential.CredentialName ?? string.Empty, reference.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<JsonElement> EnumerateWorkflowObjects(JsonElement root)
    {
        return root.ValueKind switch
        {
            JsonValueKind.Object => [root],
            JsonValueKind.Array => root.EnumerateArray(),
            _ => []
        };
    }

    private static bool Same(string? left, string? right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
    }

    private static string? ReadWorkflowString(string content, string propertyName)
    {
        using var document = JsonDocument.Parse(content);
        return GetString(document.RootElement, propertyName);
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? GetDate(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
                ? parsed
                : null;
    }

    private static string BuildAuditSummary(
        IReadOnlyCollection<string> warnings,
        IReadOnlyCollection<string> errors,
        IReadOnlyDictionary<string, string>? resolutions = null,
        int conflictCount = 0,
        int unresolvedConflictCount = 0,
        string? mergePreviewStatus = null,
        int appliedCount = 0,
        int deletedCount = 0,
        int skippedCount = 0)
    {
        var resolutionSummary = resolutions?
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new
        {
            warnings,
            errors,
            workflowResolutions = resolutions,
            conflictCount,
            unresolvedConflictCount,
            mergePreviewStatus,
            appliedResolutionSummary = resolutionSummary,
            appliedWorkflowCount = appliedCount,
            deletedWorkflowCount = deletedCount,
            skippedWorkflowCount = skippedCount
        });
    }

    private sealed record ResolutionValidationResult(
        int Required,
        int Found,
        int UnresolvedConflictCount,
        IReadOnlyList<PromotionMissingMappingDto> MissingMappings,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> BlockingErrors);
}
