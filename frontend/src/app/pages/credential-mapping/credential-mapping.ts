import { Component, computed, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  AiAssistantResponse,
  AiCredentialMappingResult,
  AiSettings,
  ApiService,
  EnvironmentCredential,
  EnvironmentItem,
  ExportValidationResult,
  GitDiffFile,
  LogicalCredential,
  ExportValidationIssue,
  RemapPreviewResult,
  WorkflowSemanticDiffCollection,
} from '../../services/api';
import { ConfirmationService } from '../../services/confirmation';
import { ToastService } from '../../services/toast';

@Component({
  selector: 'app-credential-mapping',
  imports: [FormsModule],
  templateUrl: './credential-mapping.html',
})
export class CredentialMappingComponent implements OnInit {
  environments = signal<EnvironmentItem[]>([]);
  sourceCredentials = signal<EnvironmentCredential[]>([]);
  targetCredentials = signal<EnvironmentCredential[]>([]);
  logicalCredentials = signal<LogicalCredential[]>([]);
  compareFiles = signal<GitDiffFile[]>([]);
  semanticCompare = signal<WorkflowSemanticDiffCollection | null>(null);
  validation = signal<ExportValidationResult | null>(null);
  remapPreview = signal<RemapPreviewResult | null>(null);
  error = signal<string | null>(null);
  message = signal<string | null>(null);
  aiSettings = signal<AiSettings | null>(null);
  aiAnswer = signal<AiAssistantResponse | null>(null);
  aiMappingResult = signal<AiCredentialMappingResult | null>(null);
  aiQuestion = signal('');
  aiLoading = signal(false);
  aiMappingLoading = signal(false);
  aiError = signal<string | null>(null);
  diffAiAnswer = signal<AiAssistantResponse | null>(null);
  diffAiQuestion = signal('');
  diffAiLoading = signal(false);
  diffAiError = signal<string | null>(null);
  sourceKey = signal('local');
  targetKey = signal('local');
  logicalKey = '';
  displayName = '';
  selectedLogicalId = '';
  selectedSourceCredentialId = '';
  selectedTargetCredentialId = '';
  private sourceSelectionVersion = signal(0);
  selectedWorkflowDiffKey = '';
  editingLogicalId = '';
  editingLogicalKey = '';
  editingDisplayName = '';

  readonly unmappedIssues = computed(() =>
    (this.validation()?.issues ?? []).filter((issue) => issue.severity.toLowerCase() === 'blocking' && !!issue.credentialType));
  readonly targetMappingCandidates = computed(() => {
    this.sourceSelectionVersion();
    const source = this.selectedSourceCredential();
    return this.targetCredentials()
      .filter((credential) => !source || credential.credentialType === source.credentialType)
      .sort((left, right) => this.credentialSuggestionScore(right, source) - this.credentialSuggestionScore(left, source));
  });
  readonly suggestedTarget = computed(() => this.targetMappingCandidates()[0] ?? null);

  constructor(private readonly api: ApiService, private readonly confirmation: ConfirmationService, private readonly toast: ToastService) {}

  ngOnInit(): void {
    this.api.getEnvironments().subscribe({
      next: (environments) => {
        this.environments.set(environments);
        this.sourceKey.set(environments[0]?.key ?? 'local');
        this.targetKey.set(environments.find((item) => item.key !== this.sourceKey())?.key ?? environments[0]?.key ?? 'local');
        this.loadAll();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load environments.'),
    });
    this.loadLogicalCredentials();
    this.loadAiSettings();
  }

  loadAll(): void {
    this.selectedSourceCredentialId = '';
    this.selectedTargetCredentialId = '';
    this.loadCredentials();
    this.compare();
    this.validate();
    this.previewRemap();
  }

  loadCredentials(): void {
    this.api.getEnvironmentCredentials(this.sourceKey()).subscribe({
      next: (credentials) => this.sourceCredentials.set(credentials),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load source credentials.'),
    });
    this.api.getEnvironmentCredentials(this.targetKey()).subscribe({
      next: (credentials) => this.targetCredentials.set(credentials),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load target credentials.'),
    });
  }

  loadLogicalCredentials(): void {
    this.api.getLogicalCredentials().subscribe({
      next: (credentials) => this.logicalCredentials.set(credentials),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load logical credentials.'),
    });
  }

  createLogicalCredential(): void {
    this.api.createLogicalCredential({ key: this.logicalKey, displayName: this.displayName }).subscribe({
      next: (credential) => {
        this.message.set(`${credential.displayName} created.`);
        this.selectedLogicalId = credential.id;
        this.logicalKey = '';
        this.displayName = '';
        this.loadLogicalCredentials();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Logical credential could not be created.'),
    });
  }

  setMappings(): void {
    if (!this.canMap()) {
      this.error.set(this.mappingBlockers()[0] ?? 'Choose a logical credential, source credential, and target credential first.');
      return;
    }

    this.error.set(null);
    this.message.set(null);
    this.api.setLogicalCredentialPairMapping({
      logicalCredentialId: this.selectedLogicalId,
      sourceEnvironmentKey: this.sourceKey(),
      sourceEnvironmentCredentialId: this.selectedSourceCredentialId,
      targetEnvironmentKey: this.targetKey(),
      targetEnvironmentCredentialId: this.selectedTargetCredentialId,
    }).subscribe({
      next: () => {
        this.message.set('Mapping saved.');
        this.loadLogicalCredentials();
        this.validate();
        this.previewRemap();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Mapping could not be saved.'),
    });
  }

  compare(): void {
    this.api.compareEnvironments(this.sourceKey(), this.targetKey()).subscribe({
      next: (result) => this.compareFiles.set(result.files),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not compare environments.'),
    });
    this.api.semanticCompareEnvironments(this.sourceKey(), this.targetKey()).subscribe({
      next: (result) => {
        this.semanticCompare.set(result);
        this.selectedWorkflowDiffKey = '';
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load semantic compare.'),
    });
  }

  validate(): void {
    this.api.validateRemappedExport(this.sourceKey(), this.targetKey()).subscribe({
      next: (result) => this.validation.set(result),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not validate export.'),
    });
  }

  previewRemap(): void {
    this.api.previewRemappedExport(this.sourceKey(), this.targetKey()).subscribe({
      next: (result) => this.remapPreview.set(result),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load remap preview.'),
    });
  }

  export(): void {
    this.api.exportRemapped(this.sourceKey(), this.targetKey()).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = `workflows-${this.sourceKey()}-to-${this.targetKey()}-remapped.zip`;
        anchor.click();
        URL.revokeObjectURL(url);
      },
      error: () => this.error.set('Export failed. Run validation to see missing mappings.'),
    });
  }

  canMap(): boolean {
    return this.mappingBlockers().length === 0;
  }

  mappingBlockers(): string[] {
    const blockers: string[] = [];
    if (this.sourceKey() === this.targetKey()) {
      blockers.push('Choose different source and target environments.');
    }

    if (!this.selectedLogicalId) {
      blockers.push('Choose the logical credential this pair belongs to.');
    }

    if (!this.selectedSourceCredentialId) {
      blockers.push(`Choose the ${this.sourceEnvironmentLabel()} credential that appears in source workflows.`);
    }

    if (!this.selectedTargetCredentialId) {
      blockers.push(`Choose the ${this.targetEnvironmentLabel()} credential that should replace it in target workflows.`);
    }

    const source = this.selectedSourceCredential();
    const target = this.selectedTargetCredential();
    if (source && target && source.credentialType !== target.credentialType) {
      blockers.push(`Credential types differ: ${source.credentialType} vs ${target.credentialType}. Create a separate logical credential or choose matching types.`);
    }

    return blockers;
  }

  selectedLogicalCredential(): LogicalCredential | null {
    return this.logicalCredentials().find((credential) => credential.id === this.selectedLogicalId) ?? null;
  }

  selectedSourceCredential(): EnvironmentCredential | null {
    return this.sourceCredentials().find((credential) => credential.id === this.selectedSourceCredentialId) ?? null;
  }

  selectedTargetCredential(): EnvironmentCredential | null {
    return this.targetCredentials().find((credential) => credential.id === this.selectedTargetCredentialId) ?? null;
  }

  selectSourceCredential(id: string): void {
    this.selectedSourceCredentialId = id;
    this.sourceSelectionVersion.update((version) => version + 1);
    const selectedTarget = this.selectedTargetCredential();
    if (selectedTarget && selectedTarget.credentialType !== this.selectedSourceCredential()?.credentialType) {
      this.selectedTargetCredentialId = '';
    }
  }

  useSuggestedTarget(): void {
    this.selectedTargetCredentialId = this.suggestedTarget()?.id ?? '';
  }

  mapUnmappedIssue(issue: ExportValidationIssue): void {
    const source = this.sourceCredentials().find((credential) =>
      credential.credentialType === issue.credentialType
      && credential.credentialId === issue.credentialId
      && credential.credentialName === issue.credentialName);
    if (!source) {
      this.error.set('This source credential is no longer present in the selected environment. Refresh credentials and try again.');
      return;
    }

    this.selectedSourceCredentialId = source.id;
    this.sourceSelectionVersion.update((version) => version + 1);
    this.selectedTargetCredentialId = '';
    const existing = this.logicalCredentials().find((logical) =>
      logical.mappings.some((mapping) => mapping.environmentKey === this.sourceKey() && mapping.environmentCredentialId === source.id));
    this.selectedLogicalId = existing?.id ?? '';
    this.message.set(`Ready to map ${this.credentialLabel(source)} from ${issue.workflowName ?? 'the selected workflow'}.`);
  }

  credentialUsage(credential: EnvironmentCredential | null): string {
    if (!credential) {
      return 'No credential selected.';
    }

    return `${credential.referenceCount} workflow reference${credential.referenceCount === 1 ? '' : 's'} detected ${new Date(credential.lastDetectedAt).toLocaleDateString()}.`;
  }

  isStaleMapping(referenceCount: number): boolean {
    return referenceCount === 0;
  }

  beginEditLogicalCredential(logical: LogicalCredential): void {
    this.editingLogicalId = logical.id;
    this.editingLogicalKey = logical.key;
    this.editingDisplayName = logical.displayName;
  }

  saveLogicalCredential(): void {
    this.api.updateLogicalCredential(this.editingLogicalId, { key: this.editingLogicalKey, displayName: this.editingDisplayName }).subscribe({
      next: () => {
        this.editingLogicalId = '';
        this.message.set('Logical credential updated.');
        this.loadLogicalCredentials();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Logical credential could not be updated.'),
    });
  }

  async deleteLogicalCredential(logical: LogicalCredential): Promise<void> {
    if (!await this.confirmation.confirm({ title: `Delete ${logical.displayName}?`, message: 'This permanently removes the logical credential and all of its mappings.', confirmLabel: 'Delete credential', danger: true })) return;
    this.api.deleteLogicalCredential(logical.id).subscribe({
      next: () => {
        if (this.selectedLogicalId === logical.id) this.selectedLogicalId = '';
        this.message.set('Logical credential deleted.');
        this.toast.success('Logical credential deleted.');
        this.loadLogicalCredentials();
        this.validate();
        this.previewRemap();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Logical credential could not be deleted.'),
    });
  }

  async deleteMapping(mappingId: string): Promise<void> {
    if (!await this.confirmation.confirm({ title: 'Remove environment mapping?', message: 'Workflows that use this credential may no longer be valid for the target environment.', confirmLabel: 'Remove mapping', danger: true })) return;
    this.api.deleteLogicalCredentialMapping(mappingId).subscribe({
      next: () => {
        this.message.set('Environment mapping removed.');
        this.toast.success('Environment mapping removed.');
        this.loadLogicalCredentials();
        this.validate();
        this.previewRemap();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Environment mapping could not be removed.'),
    });
  }

  sourceEnvironmentLabel(): string {
    return this.environments().find((environment) => environment.key === this.sourceKey())?.name ?? this.sourceKey();
  }

  targetEnvironmentLabel(): string {
    return this.environments().find((environment) => environment.key === this.targetKey())?.name ?? this.targetKey();
  }

  credentialLabel(credential: EnvironmentCredential | null): string {
    if (!credential) {
      return 'Not selected';
    }

    return `${credential.credentialName ?? credential.credentialId ?? 'Unnamed'} / ${credential.credentialType}`;
  }

  workflowDiffKey(workflow: NonNullable<WorkflowSemanticDiffCollection['workflows']>[number]): string {
    return workflow.newFilePath ?? workflow.oldFilePath ?? workflow.workflowName;
  }

  selectWorkflowDiff(workflow: NonNullable<WorkflowSemanticDiffCollection['workflows']>[number]): void {
    this.selectedWorkflowDiffKey = this.workflowDiffKey(workflow);
    this.diffAiAnswer.set(null);
    this.diffAiError.set(null);
  }

  closeWorkflowDiff(): void {
    this.selectedWorkflowDiffKey = '';
    this.diffAiAnswer.set(null);
    this.diffAiError.set(null);
    this.diffAiQuestion.set('');
  }

  selectedWorkflowDiff(): NonNullable<WorkflowSemanticDiffCollection['workflows']>[number] | null {
    return this.semanticCompare()?.workflows.find((workflow) => this.workflowDiffKey(workflow) === this.selectedWorkflowDiffKey) ?? null;
  }

  workflowHasVisibleChanges(workflow: NonNullable<WorkflowSemanticDiffCollection['workflows']>[number]): boolean {
    return workflow.changeType !== 'unchanged'
      || workflow.summary.addedNodes > 0
      || workflow.summary.removedNodes > 0
      || workflow.summary.modifiedNodes > 0
      || workflow.summary.changedCredentials > 0
      || workflow.summary.changedConnections > 0
      || workflow.summary.changedWorkflowSettings > 0
      || workflow.warnings.length > 0;
  }

  nodeChangeCount(node: NonNullable<WorkflowSemanticDiffCollection['workflows']>[number]['nodeChanges'][number]): number {
    return node.parameterChanges.length + node.credentialChanges.length + node.metadataChanges.length;
  }

  askAiAboutMapping(defaultQuestion?: string): void {
    const question = (defaultQuestion ?? this.aiQuestion()).trim();
    if (!question) {
      this.aiError.set('Enter a question for the mapping assistant.');
      return;
    }

    this.aiLoading.set(true);
    this.aiError.set(null);
    this.aiAnswer.set(null);
    this.api.askAi({
      question: `${question}\n\nMapping context:\n${this.safeMappingContext()}`,
      scope: 'credential mapping',
      sourceEnvironmentKey: this.sourceKey(),
      targetEnvironmentKey: this.targetKey(),
      promotionPlan: null,
      diffContext: this.semanticCompare(),
    }).subscribe({
      next: (answer) => {
        this.aiAnswer.set(answer);
        this.aiLoading.set(false);
      },
      error: (response) => {
        this.aiError.set(response?.error?.error ?? 'AI mapping helper failed.');
        this.aiLoading.set(false);
      },
    });
  }

  createMappingsWithAi(): void {
    if (this.sourceKey() === this.targetKey()) {
      this.aiError.set('Choose different source and target environments.');
      return;
    }

    this.aiMappingLoading.set(true);
    this.aiError.set(null);
    this.aiAnswer.set(null);
    this.aiMappingResult.set(null);
    this.api.createMappingsWithAi(this.sourceKey(), this.targetKey()).subscribe({
      next: (result) => {
        this.aiMappingResult.set(result);
        this.message.set(`AI applied ${result.appliedMappingsCount} mapping pair(s) and created ${result.createdLogicalCredentialsCount} logical credential(s).`);
        this.aiMappingLoading.set(false);
        this.loadLogicalCredentials();
        this.loadCredentials();
        this.validate();
        this.compare();
      },
      error: (response) => {
        this.aiError.set(response?.error?.error ?? 'AI could not create credential mappings.');
        this.aiMappingLoading.set(false);
      },
    });
  }

  askAiAboutSelectedDiff(defaultQuestion?: string): void {
    const workflow = this.selectedWorkflowDiff();
    if (!workflow) {
      this.diffAiError.set('Open a workflow diff first.');
      return;
    }

    const question = (defaultQuestion ?? this.diffAiQuestion()).trim();
    if (!question) {
      this.diffAiError.set('Enter a question about this workflow diff.');
      return;
    }

    this.diffAiLoading.set(true);
    this.diffAiError.set(null);
    this.diffAiAnswer.set(null);
    this.api.askAi({
      question,
      scope: 'selected workflow environment diff',
      sourceEnvironmentKey: this.sourceKey(),
      targetEnvironmentKey: this.targetKey(),
      workflowFilePath: workflow.newFilePath ?? workflow.oldFilePath,
      workflowId: workflow.workflowId,
      diffContext: this.selectedWorkflowDiffContext(workflow),
    }).subscribe({
      next: (answer) => {
        this.diffAiAnswer.set(answer);
        this.diffAiLoading.set(false);
      },
      error: (response) => {
        this.diffAiError.set(response?.error?.error ?? 'AI diff explanation failed.');
        this.diffAiLoading.set(false);
      },
    });
  }

  private selectedWorkflowDiffContext(workflow: NonNullable<WorkflowSemanticDiffCollection['workflows']>[number]): WorkflowSemanticDiffCollection {
    return {
      source: this.sourceKey(),
      target: this.targetKey(),
      generatedAt: this.semanticCompare()?.generatedAt ?? new Date().toISOString(),
      workflows: [workflow],
    };
  }

  private safeMappingContext(): string {
    const logical = this.selectedLogicalCredential();
    const source = this.selectedSourceCredential();
    const target = this.selectedTargetCredential();
    const context = {
      purpose: 'Credential mapping tells promotion/export which target credential reference should replace a source credential reference. It does not copy or decrypt credential secrets.',
      sourceEnvironment: this.sourceKey(),
      targetEnvironment: this.targetKey(),
      logicalCredential: logical ? { key: logical.key, displayName: logical.displayName } : null,
      sourceCredential: source ? this.safeCredential(source) : null,
      targetCredential: target ? this.safeCredential(target) : null,
      blockers: this.mappingBlockers(),
      validation: this.validation(),
    };

    return JSON.stringify(context);
  }

  private safeCredential(credential: EnvironmentCredential): object {
    return {
      environmentKey: credential.environmentKey,
      credentialType: credential.credentialType,
      credentialName: credential.credentialName,
      credentialId: credential.credentialId,
      referenceCount: credential.referenceCount,
    };
  }

  private credentialSuggestionScore(candidate: EnvironmentCredential, source: EnvironmentCredential | null): number {
    if (!source || candidate.credentialType !== source.credentialType) {
      return -1;
    }

    const sourceLabel = this.normalizedCredentialLabel(source);
    const candidateLabel = this.normalizedCredentialLabel(candidate);
    let score = candidate.referenceCount;
    if (sourceLabel === candidateLabel) score += 1000;
    if (source.credentialName && source.credentialName === candidate.credentialName) score += 500;
    if (source.credentialId && source.credentialId === candidate.credentialId) score += 250;
    if (sourceLabel && candidateLabel && (sourceLabel.includes(candidateLabel) || candidateLabel.includes(sourceLabel))) score += 100;
    return score;
  }

  private normalizedCredentialLabel(credential: EnvironmentCredential): string {
    return (credential.credentialName ?? credential.credentialId ?? '').toLowerCase().replace(/[^a-z0-9]/g, '');
  }

  private loadAiSettings(): void {
    this.api.getAiSettings().subscribe({
      next: (settings) => this.aiSettings.set(settings),
      error: () => this.aiSettings.set(null),
    });
  }
}
