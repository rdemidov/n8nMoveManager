import { HttpClient } from '@angular/common/http';
import { computed, Injectable, signal } from '@angular/core';
import { Observable } from 'rxjs';

export interface EnvironmentItem {
  id: string;
  name: string;
  key: string;
  description: string | null;
  gitBranch: string;
  gitBranchName: string;
  createdAt: string;
  updatedAt: string;
  isDefault: boolean;
}

export interface EnvironmentRequest {
  name: string;
  key?: string | null;
  description?: string | null;
  gitBranchName?: string | null;
}

export interface EnvironmentClearResult {
  message: string;
  environmentKey: string;
  removedWorkflowFilesCount: number;
  removedWorkflowMetadataCount: number;
  removedCredentialReferencesCount: number;
  removedEnvironmentCredentialsCount: number;
  removedLogicalCredentialMappingsCount: number;
  changedFilesCount: number;
  commitSha: string | null;
  skippedCommit: boolean;
}

export interface WorkflowListItem {
  id: string | null;
  name: string;
  active: boolean;
  nodesCount: number;
  createdAt: string | null;
  updatedAt: string | null;
  filePath: string;
  lastImportedAt: string;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface N8nApiConfig {
  environmentId: string;
  environmentKey: string;
  enabled: boolean;
  baseUrl: string;
  dataTablesPath: string;
  dataTablesWritePathTemplate: string | null;
  workflowApiPath: string;
  hasApiKey: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface N8nApiConfigRequest {
  enabled: boolean;
  baseUrl?: string | null;
  dataTablesPath?: string | null;
  dataTablesWritePathTemplate?: string | null;
  workflowApiPath?: string | null;
  apiKey?: string | null;
}

export interface DataTableItem {
  id: string;
  name: string;
  columnsJson: string;
  rowCount: number | null;
  environmentKey: string;
  lastSyncedAt: string;
}

export interface DataTableSyncResult {
  environmentKey: string;
  syncedCount: number;
  changedCount: number;
  commitSha: string | null;
  skippedCommit: boolean;
  warnings: string[];
}

export interface WorkflowApiSyncResult {
  environmentKey: string;
  fetchedWorkflowsCount: number;
  importedWorkflowsCount: number;
  changedFilesCount: number;
  commitSha: string | null;
  skippedCommit: boolean;
  credentialReferencesScanned: number;
  warnings: string[];
}

export interface WorkflowApiReconciliationItem {
  workflowId: string | null;
  name: string;
  filePath: string | null;
  status: 'new-remote' | 'changed-remote' | 'in-sync' | 'local-only';
  canSync: boolean;
}

export interface WorkflowApiReconciliationPreview {
  environmentKey: string;
  items: WorkflowApiReconciliationItem[];
  remoteWorkflowCount: number;
  localOnlyCount: number;
}

export interface DataTableComparison {
  sourceEnvironmentKey: string;
  targetEnvironmentKey: string;
  items: { id: string; name: string; status: string; sourceRowCount: number | null; targetRowCount: number | null }[];
}

export interface DataTablePromotionPlan {
  sourceEnvironmentKey: string;
  targetEnvironmentKey: string;
  changes: { id: string; name: string; status: string; sourceRowCount: number | null; targetRowCount: number | null }[];
  changeCount: number;
  safetyNotice: string;
}

export interface DataTablePromotionApplyResult {
  sourceEnvironmentKey: string;
  targetEnvironmentKey: string;
  stagedTablesCount: number;
  commitSha: string | null;
  skippedCommit: boolean;
}

export interface WorkflowImportItem {
  id: string | null;
  name: string;
  active: boolean;
  nodesCount: number;
  filePath: string;
}

export interface UploadResult {
  importedWorkflowsCount: number;
  changedFilesCount: number;
  commitSha: string | null;
  commitMessage: string | null;
  message: string;
  workflows: WorkflowImportItem[];
  credentialReferencesScanned: number;
}

export interface DockerStatus {
  available: boolean;
  message: string;
  version: string | null;
  logs: string[];
  duration: string;
}

export interface EnvironmentDockerConfig {
  environmentId: string;
  environmentKey: string;
  dockerEnabled: boolean;
  containerName: string;
  n8nCliCommand: string;
  tempContainerPath: string;
  tempHostImportPath: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface EnvironmentDockerConfigRequest {
  dockerEnabled: boolean;
  containerName?: string | null;
  n8nCliCommand?: string | null;
  tempContainerPath?: string | null;
  tempHostImportPath?: string | null;
}

export interface DockerExportResult {
  status: string;
  environmentKey: string;
  containerName: string;
  exportedWorkflowsCount: number;
  importedWorkflowsCount: number;
  changedFilesCount: number;
  commitSha: string | null;
  skippedCommit: boolean;
  credentialReferencesScanned: number;
  warnings: string[];
  logs: string[];
  duration: string;
}

export interface ScheduledJob {
  id: string;
  name: string;
  jobType: string;
  environmentId: string;
  environmentKey: string;
  environmentName: string;
  cronExpression: string;
  timezone: string;
  isEnabled: boolean;
  configJson: string;
  createdAt: string;
  updatedAt: string;
  lastRunAt: string | null;
  nextRunAt: string | null;
  lastRunStatus: string | null;
}

export interface ScheduledJobRequest {
  name: string;
  jobType: string;
  environmentId: string;
  cronExpression: string;
  timezone: string;
  isEnabled: boolean;
  configJson: string;
}

export interface ScheduledJobRunSummary {
  id: string;
  scheduledJobId: string;
  startedAt: string;
  finishedAt: string | null;
  status: string;
  errorMessage: string | null;
  commitSha: string | null;
}

export interface ScheduledJobRun extends ScheduledJobRunSummary {
  logs: string[];
  resultJson: string | null;
}

export interface ScheduledJobRunNowResult {
  runId: string;
  message: string;
}

export interface GitCommit {
  sha: string;
  shortSha: string;
  message: string;
  authorName: string;
  authorEmail: string;
  when: string;
}

export interface GitDiffFile {
  filePath: string;
  status: string;
  linesAdded: number;
  linesDeleted: number;
  patch: string;
}

export interface CommitFileItem {
  filePath: string;
  fileName: string;
  sizeBytes: number;
}

export interface CommitFileContent {
  commitSha: string;
  filePath: string;
  content: string;
}

export interface RestorePreview {
  selectedCommit: GitCommit;
  currentCommit: GitCommit | null;
  filesToRestore: string[];
  filesToAdd: string[];
  filesToModify: string[];
  filesThatWouldBeDeleted: string[];
  warnings: string[];
  semanticDiffSummary: WorkflowSemanticDiffCollection;
}

export interface RestoreWorkflowResult {
  environmentKey: string;
  sourceCommitSha: string;
  filePath: string;
  commitCreated: boolean;
  newCommitSha: string | null;
  commitMessage: string | null;
  warnings: string[];
  semanticDiff: WorkflowSemanticDiff;
}

export interface RestoreEnvironmentResult {
  environmentKey: string;
  sourceCommitSha: string;
  commitCreated: boolean;
  newCommitSha: string | null;
  commitMessage: string | null;
  restoredFilesCount: number;
  addedFilesCount: number;
  modifiedFilesCount: number;
  deletedFilesCount: number;
  warnings: string[];
  preview: RestorePreview;
}

export interface BackupItem {
  id: string;
  fileName: string;
  filePath: string;
  sizeBytes: number;
  createdAt: string;
}

export interface BackupCreateResult extends BackupItem {
  downloadUrl: string;
  warnings: string[];
}

export interface EnvironmentCompare {
  source: string;
  target: string;
  files: GitDiffFile[];
}

export interface WorkflowSemanticDiffCollection {
  source: string | null;
  target: string | null;
  generatedAt: string;
  workflows: WorkflowSemanticDiff[];
}

export interface WorkflowSemanticDiff {
  workflowId: string | null;
  workflowName: string;
  changeType: string;
  summary: WorkflowSemanticDiffSummary;
  nodeChanges: NodeSemanticDiff[];
  connectionChanges: ConnectionSemanticDiff[];
  credentialChanges: CredentialSemanticDiff[];
  workflowSettingsChanges: ParameterSemanticDiff[];
  oldFilePath: string | null;
  newFilePath: string | null;
  warnings: string[];
}

export interface WorkflowSemanticDiffSummary {
  addedNodes: number;
  removedNodes: number;
  modifiedNodes: number;
  unchangedNodes: number;
  changedConnections: number;
  changedCredentials: number;
  changedWorkflowSettings: number;
}

export interface NodeSemanticDiff {
  nodeId: string | null;
  nodeName: string;
  nodeType: string;
  changeType: string;
  parameterChanges: ParameterSemanticDiff[];
  credentialChanges: CredentialSemanticDiff[];
  metadataChanges: ParameterSemanticDiff[];
}

export interface ParameterSemanticDiff {
  path: string;
  oldValuePreview: string | null;
  newValuePreview: string | null;
  valueType: string;
  importance: string;
}

export interface CredentialSemanticDiff {
  nodeName: string;
  credentialKey: string;
  credentialType: string;
  oldCredentialId: string | null;
  oldCredentialName: string | null;
  newCredentialId: string | null;
  newCredentialName: string | null;
}

export interface ConnectionSemanticDiff {
  sourceNodeName: string;
  targetNodeName: string;
  outputIndex: number | null;
  inputIndex: number | null;
  changeType: string;
}

export interface EnvironmentCredential {
  id: string;
  environmentKey: string;
  credentialType: string;
  credentialId: string | null;
  credentialName: string | null;
  referenceCount: number;
  lastDetectedAt: string;
}

export interface CredentialReference {
  id: string;
  environmentKey: string;
  workflowId: string | null;
  workflowName: string;
  workflowFilePath: string;
  nodeId: string | null;
  nodeName: string;
  nodeType: string;
  credentialType: string;
  credentialId: string | null;
  credentialName: string | null;
  detectedAt: string;
}

export interface LogicalCredentialMapping {
  id: string;
  environmentId: string;
  environmentKey: string;
  environmentName: string;
  environmentCredentialId: string;
  credentialType: string;
  credentialId: string | null;
  credentialName: string | null;
  referenceCount: number;
  lastDetectedAt: string;
}

export interface LogicalCredential {
  id: string;
  key: string;
  displayName: string;
  mappings: LogicalCredentialMapping[];
}

export interface LogicalCredentialRequest {
  key: string;
  displayName: string;
}

export interface ExportValidationIssue {
  severity: string;
  message: string;
  workflowName: string | null;
  workflowFilePath: string | null;
  nodeName: string | null;
  credentialType: string | null;
  credentialId: string | null;
  credentialName: string | null;
}

export interface ExportValidationResult {
  canExport: boolean;
  issues: ExportValidationIssue[];
}

export interface RemapPreviewItem {
  workflowName: string;
  workflowFilePath: string;
  nodeName: string;
  credentialType: string;
  sourceCredentialId: string | null;
  sourceCredentialName: string | null;
  targetCredentialId: string | null;
  targetCredentialName: string | null;
  logicalKey: string | null;
  status: string;
}

export interface RemapPreviewResult {
  items: RemapPreviewItem[];
}

export interface AiCredentialMappingResult {
  sourceEnvironmentKey: string;
  targetEnvironmentKey: string;
  suggestedMappingsCount: number;
  appliedMappingsCount: number;
  createdLogicalCredentialsCount: number;
  items: AiCredentialMappingAppliedItem[];
  warnings: string[];
}

export interface AiCredentialMappingAppliedItem {
  logicalKey: string;
  displayName: string;
  logicalCredentialId: string | null;
  sourceEnvironmentCredentialId: string;
  targetEnvironmentCredentialId: string;
  sourceCredentialLabel: string;
  targetCredentialLabel: string;
  reason: string;
  confidence: string;
  applied: boolean;
  skippedReason: string | null;
}

export interface PromotionEnvironment {
  id: string;
  name: string;
  key: string;
  gitBranchName: string;
}

export interface PromotionWorkflowChange {
  workflowFilePath: string;
  workflowId: string | null;
  workflowName: string;
  changeType: string;
  summary: string | null;
  isSelectedByDefault: boolean;
  semanticSummary: PromotionSemanticSummary | null;
  semanticDiff: WorkflowSemanticDiff | null;
  resolution: string | null;
  availableResolutions: string[];
  isConflict: boolean;
  conflictReason: string | null;
  conflictDetails: PromotionConflictDetails | null;
}

export interface PromotionConflictDetails {
  workflowFilePath: string;
  workflowId: string | null;
  workflowName: string;
  sourceEnvironmentKey: string;
  targetEnvironmentKey: string;
  sourceCommitSha: string | null;
  targetCommitSha: string | null;
  baseCommitSha: string | null;
  sourceVsBase: WorkflowSemanticDiff | null;
  targetVsBase: WorkflowSemanticDiff | null;
  sourceVsTarget: WorkflowSemanticDiff | null;
  conflictReason: string;
}

export interface PromotionSemanticSummary {
  addedNodes: number;
  removedNodes: number;
  modifiedNodes: number;
  changedCredentials: number;
  changedConnections: number;
  changedWorkflowSettings: number;
}

export interface PromotionMissingMapping {
  workflowFilePath: string;
  workflowName: string;
  credentialType: string;
  credentialId: string | null;
  credentialName: string | null;
}

export interface PromotionPlan {
  sourceEnvironment: PromotionEnvironment;
  targetEnvironment: PromotionEnvironment;
  generatedAt: string;
  sourceCommitSha: string | null;
  targetCommitSha: string | null;
  baseCommitSha: string | null;
  workflowChanges: PromotionWorkflowChange[];
  credentialMappingsRequired: number;
  credentialMappingsFound: number;
  conflictCount: number;
  unresolvedConflictCount: number;
  missingMappings: PromotionMissingMapping[];
  warnings: string[];
  blockingErrors: string[];
  baseline: PromotionComparisonBaseline | null;
}

export interface PromotionComparisonBaseline {
  commitSha: string;
  label: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface PromotionWorkflowResolution {
  workflowFilePath: string;
  resolution: string;
}

export interface PromotionApplyRequest {
  sourceEnvironmentKey: string;
  targetEnvironmentKey: string;
  selectedWorkflowFiles: string[];
  workflowResolutions?: PromotionWorkflowResolution[] | null;
  confirmation: boolean;
  includeDeletions: boolean;
  confirmDeletions: boolean;
}

export interface PromotionApplyResult {
  commitCreated: boolean;
  commitSha: string | null;
  commitMessage: string | null;
  appliedFilesCount: number;
  appliedFiles: string[];
  skippedFiles: string[];
  deletedFiles: string[];
  warnings: string[];
  message: string;
}

export interface PromotionMergePreview {
  workflowsToWrite: string[];
  workflowsToKeep: string[];
  workflowsToSkip: string[];
  workflowsToDelete: string[];
  warnings: string[];
  blockingErrors: string[];
  semanticDiff: WorkflowSemanticDiffCollection;
  credentialMappingSummary: {
    required: number;
    found: number;
    missingMappings: PromotionMissingMapping[];
  };
  affectedWorkflowCount: number;
}

export interface ManualMergeCreateRequest {
  sourceEnvironmentKey: string;
  targetEnvironmentKey: string;
  workflowFilePath: string;
  sourceCommitSha?: string | null;
  targetCommitSha?: string | null;
}

export interface ManualMergeSession {
  id: string;
  sourceEnvironmentKey: string;
  targetEnvironmentKey: string;
  workflowFilePath: string;
  sourceCommitSha: string | null;
  targetCommitSha: string | null;
  baseCommitSha: string | null;
  createdAt: string;
  updatedAt: string;
  sourceWorkflow: ManualMergeWorkflowSummary;
  targetWorkflow: ManualMergeWorkflowSummary;
  semanticDiff: WorkflowSemanticDiff;
  selection: ManualMergeSelection;
}

export interface ManualMergeWorkflowSummary {
  workflowId: string | null;
  workflowName: string;
  active: boolean;
  nodesCount: number;
  credentialReferences: ManualMergeCredentialReference[];
}

export interface ManualMergeCredentialReference {
  nodeName: string;
  credentialKey: string;
  credentialType: string;
  credentialId: string | null;
  credentialName: string | null;
  isMapped: boolean;
  mappedCredentialId: string | null;
  mappedCredentialName: string | null;
}

export interface ManualMergeSelection {
  workflowSettingsSelections: WorkflowSettingMergeSelection[];
  nodeSelections: NodeMergeSelection[];
  parameterSelections: ParameterMergeSelection[];
  connectionSelection: string;
  credentialMappingMode: string;
}

export interface WorkflowSettingMergeSelection {
  propertyName: string;
  sourceValuePreview: string | null;
  targetValuePreview: string | null;
  selectedSide: string;
}

export interface NodeMergeSelection {
  nodeMatchKey: string;
  sourceNodeId: string | null;
  targetNodeId: string | null;
  nodeName: string;
  nodeType: string;
  changeType: string;
  resolution: string;
}

export interface ParameterMergeSelection {
  nodeMatchKey: string;
  parameterPath: string;
  sourceValuePreview: string | null;
  targetValuePreview: string | null;
  selectedSide: string;
}

export interface ManualMergeResult {
  resultWorkflowJson: string;
  validationStatus: string;
  warnings: string[];
  blockingErrors: string[];
  infoMessages: string[];
  semanticDiffResultVsTarget: WorkflowSemanticDiff;
  semanticDiffResultVsSource: WorkflowSemanticDiff;
}

export interface ManualMergeApplyResult {
  commitCreated: boolean;
  commitSha: string | null;
  commitMessage: string | null;
  workflowFilePath: string;
  warnings: string[];
  message: string;
}

export interface AiSettings {
  enabled: boolean;
  endpoint: string;
  modelName: string;
  hasApiKey: boolean;
  storageWarning: string;
}

export interface AiSettingsRequest {
  enabled: boolean;
  endpoint: string;
  modelName: string;
  apiKey?: string | null;
}

export interface AiAssistantResponse {
  answer: string;
  shortSummary: string | null;
  detailedSummary: string | null;
  importantChanges: string[];
  risks: string[];
  warnings: string[];
  blockingIssues: string[];
  recommendedNextSteps: string[];
  citedContextItems: string[];
  suggestedResolution: string | null;
  reasoning: string | null;
  confidence: string;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly baseUrl = 'http://localhost:5107/api';
  readonly environments = signal<EnvironmentItem[]>([]);
  readonly selectedEnvironmentKey = signal('local');
  readonly selectedEnvironment = computed(() =>
    this.environments().find((environment) => environment.key === this.selectedEnvironmentKey()) ?? null);

  constructor(private readonly http: HttpClient) {}

  getEnvironments(): Observable<EnvironmentItem[]> {
    return this.http.get<EnvironmentItem[]>(`${this.baseUrl}/environments`);
  }

  createEnvironment(request: EnvironmentRequest): Observable<EnvironmentItem> {
    return this.http.post<EnvironmentItem>(`${this.baseUrl}/environments`, request);
  }

  updateEnvironment(environmentKey: string, request: EnvironmentRequest): Observable<EnvironmentItem> {
    return this.http.put<EnvironmentItem>(`${this.baseUrl}/environments/${environmentKey}`, request);
  }

  deleteEnvironment(environmentKey: string, force = false): Observable<{ message: string }> {
    return this.http.delete<{ message: string }>(`${this.baseUrl}/environments/${environmentKey}?force=${force}`);
  }

  clearEnvironment(environmentKey: string, commitMessage?: string | null): Observable<EnvironmentClearResult> {
    return this.http.post<EnvironmentClearResult>(`${this.baseUrl}/environments/${environmentKey}/clear`, {
      confirmation: true,
      commitMessage: commitMessage?.trim() || null,
    });
  }

  selectEnvironment(environmentKey: string): void {
    this.selectedEnvironmentKey.set(this.resolveEnvironmentKey(environmentKey));
  }

  setEnvironments(environments: EnvironmentItem[]): void {
    this.environments.set(environments);
    if (environments.length > 0) {
      this.selectEnvironment(this.selectedEnvironmentKey());
    }
  }

  private resolveEnvironmentKey(environmentKey: string): string {
    const environments = this.environments();
    const normalizedKey = environmentKey.trim().toLowerCase();
    const exact = environments.find((environment) => environment.key === normalizedKey);
    if (exact) {
      return exact.key;
    }

    const alias = normalizedKey === 'production' ? 'prod' : normalizedKey === 'development' ? 'dev' : normalizedKey;
    const aliased = environments.find((environment) => environment.key === alias);
    if (aliased) {
      return aliased.key;
    }

    return environments[0]?.key ?? normalizedKey;
  }

  getWorkflows(environmentKey = this.selectedEnvironmentKey()): Observable<WorkflowListItem[]> {
    return this.http.get<WorkflowListItem[]>(`${this.baseUrl}/environments/${environmentKey}/workflows`);
  }

  getWorkflowPage(environmentKey = this.selectedEnvironmentKey(), page = 1, pageSize = 25, search = '', sort = 'name', direction = 'asc'): Observable<PagedResult<WorkflowListItem>> {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize), sort, direction });
    if (search.trim()) params.set('search', search.trim());
    return this.http.get<PagedResult<WorkflowListItem>>(`${this.baseUrl}/environments/${environmentKey}/workflows/page?${params}`);
  }

  getN8nApiConfig(environmentKey = this.selectedEnvironmentKey()): Observable<N8nApiConfig> {
    return this.http.get<N8nApiConfig>(`${this.baseUrl}/environments/${environmentKey}/n8n-api/config`);
  }

  saveN8nApiConfig(environmentKey: string, request: N8nApiConfigRequest): Observable<N8nApiConfig> {
    return this.http.put<N8nApiConfig>(`${this.baseUrl}/environments/${environmentKey}/n8n-api/config`, request);
  }

  syncWorkflowsFromN8nApi(environmentKey = this.selectedEnvironmentKey()): Observable<WorkflowApiSyncResult> {
    return this.http.post<WorkflowApiSyncResult>(`${this.baseUrl}/environments/${environmentKey}/n8n-api/sync-workflows`, {});
  }

  previewWorkflowReconciliation(environmentKey = this.selectedEnvironmentKey()): Observable<WorkflowApiReconciliationPreview> {
    return this.http.get<WorkflowApiReconciliationPreview>(`${this.baseUrl}/environments/${environmentKey}/n8n-api/workflow-reconciliation`);
  }

  syncSelectedWorkflowsFromN8nApi(workflowIds: string[], environmentKey = this.selectedEnvironmentKey()): Observable<WorkflowApiSyncResult> {
    return this.http.post<WorkflowApiSyncResult>(`${this.baseUrl}/environments/${environmentKey}/n8n-api/sync-workflows/selected`, { workflowIds });
  }

  getDataTables(environmentKey = this.selectedEnvironmentKey(), page = 1, pageSize = 25, search = '', sort = 'name', direction = 'asc'): Observable<PagedResult<DataTableItem>> {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize), sort, direction });
    if (search.trim()) params.set('search', search.trim());
    return this.http.get<PagedResult<DataTableItem>>(`${this.baseUrl}/environments/${environmentKey}/data-tables?${params}`);
  }

  syncDataTables(environmentKey = this.selectedEnvironmentKey()): Observable<DataTableSyncResult> {
    return this.http.post<DataTableSyncResult>(`${this.baseUrl}/environments/${environmentKey}/data-tables/sync`, {});
  }

  compareDataTables(source: string, target: string): Observable<DataTableComparison> {
    return this.http.get<DataTableComparison>(`${this.baseUrl}/data-tables/compare?source=${encodeURIComponent(source)}&target=${encodeURIComponent(target)}`);
  }

  getDataTablePromotionPlan(source: string, target: string): Observable<DataTablePromotionPlan> {
    return this.http.get<DataTablePromotionPlan>(`${this.baseUrl}/data-tables/promotion-plan?source=${encodeURIComponent(source)}&target=${encodeURIComponent(target)}`);
  }

  stageDataTablePromotion(sourceEnvironmentKey: string, targetEnvironmentKey: string, tableIds: string[]): Observable<DataTablePromotionApplyResult> {
    return this.http.post<DataTablePromotionApplyResult>(`${this.baseUrl}/data-tables/promotions/stage`, { sourceEnvironmentKey, targetEnvironmentKey, tableIds, confirmation: true });
  }

  uploadFiles(files: FileList, environmentKey = this.selectedEnvironmentKey(), commitMessage?: string | null): Observable<UploadResult> {
    const formData = new FormData();
    Array.from(files).forEach((file) => formData.append('files', file, file.name));
    if (commitMessage?.trim()) {
      formData.append('commitMessage', commitMessage.trim());
    }
    return this.http.post<UploadResult>(`${this.baseUrl}/environments/${environmentKey}/workflows/upload`, formData);
  }

  getDockerStatus(): Observable<DockerStatus> {
    return this.http.get<DockerStatus>(`${this.baseUrl}/docker/status`);
  }

  getEnvironmentDockerConfig(environmentKey = this.selectedEnvironmentKey()): Observable<EnvironmentDockerConfig> {
    return this.http.get<EnvironmentDockerConfig>(`${this.baseUrl}/environments/${environmentKey}/docker/config`);
  }

  saveEnvironmentDockerConfig(environmentKey: string, request: EnvironmentDockerConfigRequest): Observable<EnvironmentDockerConfig> {
    return this.http.post<EnvironmentDockerConfig>(`${this.baseUrl}/environments/${environmentKey}/docker/config`, request);
  }

  testEnvironmentDocker(environmentKey = this.selectedEnvironmentKey()): Observable<DockerExportResult> {
    return this.http.post<DockerExportResult>(`${this.baseUrl}/environments/${environmentKey}/docker/test`, {});
  }

  exportWorkflowsFromDocker(environmentKey = this.selectedEnvironmentKey()): Observable<DockerExportResult> {
    return this.http.post<DockerExportResult>(`${this.baseUrl}/environments/${environmentKey}/docker/export-workflows`, {});
  }

  getScheduledJobs(): Observable<ScheduledJob[]> {
    return this.http.get<ScheduledJob[]>(`${this.baseUrl}/scheduled-jobs`);
  }

  getScheduledJob(id: string): Observable<ScheduledJob> {
    return this.http.get<ScheduledJob>(`${this.baseUrl}/scheduled-jobs/${id}`);
  }

  createScheduledJob(request: ScheduledJobRequest): Observable<ScheduledJob> {
    return this.http.post<ScheduledJob>(`${this.baseUrl}/scheduled-jobs`, request);
  }

  updateScheduledJob(id: string, request: ScheduledJobRequest): Observable<ScheduledJob> {
    return this.http.put<ScheduledJob>(`${this.baseUrl}/scheduled-jobs/${id}`, request);
  }

  deleteScheduledJob(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/scheduled-jobs/${id}`);
  }

  enableScheduledJob(id: string): Observable<ScheduledJob> {
    return this.http.post<ScheduledJob>(`${this.baseUrl}/scheduled-jobs/${id}/enable`, {});
  }

  disableScheduledJob(id: string): Observable<ScheduledJob> {
    return this.http.post<ScheduledJob>(`${this.baseUrl}/scheduled-jobs/${id}/disable`, {});
  }

  runScheduledJobNow(id: string): Observable<ScheduledJobRunNowResult> {
    return this.http.post<ScheduledJobRunNowResult>(`${this.baseUrl}/scheduled-jobs/${id}/run-now`, {});
  }

  getScheduledJobRuns(id: string): Observable<ScheduledJobRunSummary[]> {
    return this.http.get<ScheduledJobRunSummary[]>(`${this.baseUrl}/scheduled-jobs/${id}/runs`);
  }

  getScheduledJobRun(id: string, runId: string): Observable<ScheduledJobRun> {
    return this.http.get<ScheduledJobRun>(`${this.baseUrl}/scheduled-jobs/${id}/runs/${runId}`);
  }

  updateCommitMessage(environmentKey: string, commitSha: string, message: string): Observable<GitCommit> {
    return this.http.put<GitCommit>(`${this.baseUrl}/environments/${environmentKey}/git/commits/${commitSha}/message`, { message });
  }

  getCommits(limit = 25, environmentKey = this.selectedEnvironmentKey()): Observable<GitCommit[]> {
    return this.http.get<GitCommit[]>(`${this.baseUrl}/environments/${environmentKey}/git/commits?limit=${limit}`);
  }

  getLatestDiff(environmentKey = this.selectedEnvironmentKey()): Observable<GitDiffFile[]> {
    return this.http.get<GitDiffFile[]>(`${this.baseUrl}/environments/${environmentKey}/git/diff/latest`);
  }

  getCommitDiff(sha: string, environmentKey = this.selectedEnvironmentKey()): Observable<GitDiffFile[]> {
    return this.http.get<GitDiffFile[]>(`${this.baseUrl}/environments/${environmentKey}/git/diff/${sha}`);
  }

  getCommitSemanticDiff(sha: string, environmentKey = this.selectedEnvironmentKey()): Observable<WorkflowSemanticDiffCollection> {
    return this.http.get<WorkflowSemanticDiffCollection>(`${this.baseUrl}/environments/${environmentKey}/semantic-diff/${sha}`);
  }

  getCommitFiles(commitSha: string, environmentKey = this.selectedEnvironmentKey()): Observable<CommitFileItem[]> {
    return this.http.get<CommitFileItem[]>(`${this.baseUrl}/environments/${environmentKey}/commits/${commitSha}/files`);
  }

  getCommitFileContent(commitSha: string, filePath: string, environmentKey = this.selectedEnvironmentKey()): Observable<CommitFileContent> {
    return this.http.get<CommitFileContent>(`${this.baseUrl}/environments/${environmentKey}/commits/${commitSha}/files/content?path=${encodeURIComponent(filePath)}`);
  }

  getCommitFileDownloadUrl(commitSha: string, filePath: string, environmentKey = this.selectedEnvironmentKey()): string {
    return `${this.baseUrl}/environments/${environmentKey}/commits/${commitSha}/files/download?path=${encodeURIComponent(filePath)}`;
  }

  getRestorePreview(commitSha: string, environmentKey = this.selectedEnvironmentKey()): Observable<RestorePreview> {
    return this.http.get<RestorePreview>(`${this.baseUrl}/environments/${environmentKey}/restore/preview?commitSha=${encodeURIComponent(commitSha)}`);
  }

  restoreWorkflow(commitSha: string, filePath: string, environmentKey = this.selectedEnvironmentKey()): Observable<RestoreWorkflowResult> {
    return this.http.post<RestoreWorkflowResult>(`${this.baseUrl}/environments/${environmentKey}/restore/workflow`, {
      commitSha,
      filePath,
      confirmation: true,
    });
  }

  restoreEnvironment(commitSha: string, includeDeletedFiles: boolean, environmentKey = this.selectedEnvironmentKey()): Observable<RestoreEnvironmentResult> {
    return this.http.post<RestoreEnvironmentResult>(`${this.baseUrl}/environments/${environmentKey}/restore/environment`, {
      commitSha,
      includeDeletedFiles,
      confirmation: true,
    });
  }

  createBackupFromCommit(commitSha: string, includeMetadata: boolean, includeDatabaseSnapshot: boolean, environmentKey = this.selectedEnvironmentKey()): Observable<BackupCreateResult> {
    return this.http.post<BackupCreateResult>(`${this.baseUrl}/environments/${environmentKey}/backups/from-commit`, {
      commitSha,
      includeMetadata,
      includeDatabaseSnapshot,
    });
  }

  getBackups(): Observable<BackupItem[]> {
    return this.http.get<BackupItem[]>(`${this.baseUrl}/backups`);
  }

  getBackupDownloadUrl(backupId: string): string {
    return `${this.baseUrl}/backups/${backupId}/download`;
  }

  deleteBackup(backupId: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/backups/${backupId}`);
  }

  compareEnvironments(source: string, target: string): Observable<EnvironmentCompare> {
    return this.http.get<EnvironmentCompare>(`${this.baseUrl}/environments/compare?source=${source}&target=${target}`);
  }

  semanticCompareEnvironments(source: string, target: string): Observable<WorkflowSemanticDiffCollection> {
    return this.http.get<WorkflowSemanticDiffCollection>(`${this.baseUrl}/environments/semantic-compare?source=${source}&target=${target}`);
  }

  getEnvironmentCredentials(environmentKey = this.selectedEnvironmentKey()): Observable<EnvironmentCredential[]> {
    return this.http.get<EnvironmentCredential[]>(`${this.baseUrl}/environments/${environmentKey}/credentials`);
  }

  getCredentialReferences(environmentKey = this.selectedEnvironmentKey()): Observable<CredentialReference[]> {
    return this.http.get<CredentialReference[]>(`${this.baseUrl}/environments/${environmentKey}/credential-references`);
  }

  getLogicalCredentials(): Observable<LogicalCredential[]> {
    return this.http.get<LogicalCredential[]>(`${this.baseUrl}/logical-credentials`);
  }

  createLogicalCredential(request: LogicalCredentialRequest): Observable<LogicalCredential> {
    return this.http.post<LogicalCredential>(`${this.baseUrl}/logical-credentials`, request);
  }

  updateLogicalCredential(id: string, request: LogicalCredentialRequest): Observable<LogicalCredential> {
    return this.http.put<LogicalCredential>(`${this.baseUrl}/logical-credentials/${id}`, request);
  }

  deleteLogicalCredential(id: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/logical-credentials/${id}`);
  }

  setLogicalCredentialMapping(logicalCredentialId: string, environmentKey: string, environmentCredentialId: string): Observable<LogicalCredential> {
    return this.http.post<LogicalCredential>(`${this.baseUrl}/logical-credentials/mappings`, {
      logicalCredentialId,
      environmentKey,
      environmentCredentialId,
    });
  }

  setLogicalCredentialPairMapping(request: {
    logicalCredentialId: string;
    sourceEnvironmentKey: string;
    sourceEnvironmentCredentialId: string;
    targetEnvironmentKey: string;
    targetEnvironmentCredentialId: string;
  }): Observable<LogicalCredential> {
    return this.http.post<LogicalCredential>(`${this.baseUrl}/logical-credentials/mappings/pair`, request);
  }

  deleteLogicalCredentialMapping(mappingId: string): Observable<void> {
    return this.http.delete<void>(`${this.baseUrl}/logical-credentials/mappings/${mappingId}`);
  }

  createMappingsWithAi(sourceEnvironmentKey: string, targetEnvironmentKey: string): Observable<AiCredentialMappingResult> {
    return this.http.post<AiCredentialMappingResult>(`${this.baseUrl}/logical-credentials/ai-create-mappings`, {
      sourceEnvironmentKey,
      targetEnvironmentKey,
    });
  }

  validateRemappedExport(source: string, target: string): Observable<ExportValidationResult> {
    return this.http.get<ExportValidationResult>(`${this.baseUrl}/environments/export-remapped/validate?source=${source}&target=${target}`);
  }

  previewRemappedExport(source: string, target: string): Observable<RemapPreviewResult> {
    return this.http.get<RemapPreviewResult>(`${this.baseUrl}/environments/export-remapped/preview?source=${source}&target=${target}`);
  }

  exportRemapped(source: string, target: string): Observable<Blob> {
    return this.http.post(`${this.baseUrl}/environments/export-remapped?source=${source}&target=${target}`, null, {
      responseType: 'blob',
    });
  }

  getPromotionPlan(source: string, target: string, includeDeletions = false): Observable<PromotionPlan> {
    return this.http.get<PromotionPlan>(`${this.baseUrl}/promotions/plan?source=${source}&target=${target}&includeDeletions=${includeDeletions}`);
  }

  getPromotionBaseline(source: string, target: string): Observable<PromotionComparisonBaseline | null> {
    return this.http.get<PromotionComparisonBaseline | null>(`${this.baseUrl}/promotions/baseline?source=${source}&target=${target}`);
  }

  setPromotionBaseline(request: { sourceEnvironmentKey: string; targetEnvironmentKey: string; commitSha: string | null; label: string | null }): Observable<PromotionComparisonBaseline | null> {
    return this.http.put<PromotionComparisonBaseline | null>(`${this.baseUrl}/promotions/baseline`, request);
  }

  applyPromotion(request: PromotionApplyRequest): Observable<PromotionApplyResult> {
    return this.http.post<PromotionApplyResult>(`${this.baseUrl}/promotions/apply`, request);
  }

  previewPromotionMerge(request: {
    sourceEnvironmentKey: string;
    targetEnvironmentKey: string;
    workflowResolutions: PromotionWorkflowResolution[];
    includeDeletions: boolean;
    confirmDeletions: boolean;
  }): Observable<PromotionMergePreview> {
    return this.http.post<PromotionMergePreview>(`${this.baseUrl}/promotions/merge-preview`, request);
  }

  createManualMergeSession(request: ManualMergeCreateRequest): Observable<ManualMergeSession> {
    return this.http.post<ManualMergeSession>(`${this.baseUrl}/manual-merge/session`, request);
  }

  getManualMergeSession(sessionId: string): Observable<ManualMergeSession> {
    return this.http.get<ManualMergeSession>(`${this.baseUrl}/manual-merge/session/${sessionId}`);
  }

  updateManualMergeSelection(sessionId: string, selection: ManualMergeSelection): Observable<ManualMergeSession> {
    return this.http.put<ManualMergeSession>(`${this.baseUrl}/manual-merge/session/${sessionId}/selection`, { selection });
  }

  previewManualMerge(sessionId: string): Observable<ManualMergeResult> {
    return this.http.post<ManualMergeResult>(`${this.baseUrl}/manual-merge/session/${sessionId}/preview`, {});
  }

  applyManualMerge(sessionId: string): Observable<ManualMergeApplyResult> {
    return this.http.post<ManualMergeApplyResult>(`${this.baseUrl}/manual-merge/session/${sessionId}/apply`, { confirmation: true });
  }

  getAiSettings(): Observable<AiSettings> {
    return this.http.get<AiSettings>(`${this.baseUrl}/ai/settings`);
  }

  saveAiSettings(request: AiSettingsRequest): Observable<AiSettings> {
    return this.http.put<AiSettings>(`${this.baseUrl}/ai/settings`, request);
  }

  testAi(): Observable<{ success: boolean; message: string }> {
    return this.http.post<{ success: boolean; message: string }>(`${this.baseUrl}/ai/test`, {});
  }

  summarizeWorkflowDiff(request: {
    environmentKey?: string | null;
    sourceEnvironmentKey?: string | null;
    targetEnvironmentKey?: string | null;
    workflowFilePath?: string | null;
    workflowId?: string | null;
    diffContext: WorkflowSemanticDiffCollection;
  }): Observable<AiAssistantResponse> {
    return this.http.post<AiAssistantResponse>(`${this.baseUrl}/ai/summarize-workflow-diff`, request);
  }

  summarizePromotionPlan(promotionPlan: PromotionPlan, saveToAuditLog = false): Observable<AiAssistantResponse> {
    return this.http.post<AiAssistantResponse>(`${this.baseUrl}/ai/summarize-promotion-plan`, {
      promotionPlan,
      saveToAuditLog,
    });
  }

  explainConflict(promotionPlan: PromotionPlan, workflowChange: PromotionWorkflowChange): Observable<AiAssistantResponse> {
    return this.http.post<AiAssistantResponse>(`${this.baseUrl}/ai/explain-conflict`, {
      promotionPlan,
      workflowChange,
      sourceEnvironmentKey: promotionPlan.sourceEnvironment.key,
      targetEnvironmentKey: promotionPlan.targetEnvironment.key,
    });
  }

  askAi(request: {
    question: string;
    scope: string;
    environmentKey?: string | null;
    sourceEnvironmentKey?: string | null;
    targetEnvironmentKey?: string | null;
    workflowFilePath?: string | null;
    workflowId?: string | null;
    diffContext?: WorkflowSemanticDiffCollection | null;
    promotionPlan?: PromotionPlan | null;
  }): Observable<AiAssistantResponse> {
    return this.http.post<AiAssistantResponse>(`${this.baseUrl}/ai/ask`, request);
  }
}
