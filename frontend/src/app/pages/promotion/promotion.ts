import { DatePipe, JsonPipe } from '@angular/common';
import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AiAssistantResponse, ApiService, EnvironmentItem, GitCommit, PromotionApplyResult, PromotionComparisonBaseline, PromotionMergePreview, PromotionPlan, PromotionWorkflowChange, PromotionWorkflowResolution, WorkflowSemanticDiff } from '../../services/api';
import { ConfirmationService } from '../../services/confirmation';
import { ToastService } from '../../services/toast';

@Component({
  selector: 'app-promotion',
  imports: [DatePipe, JsonPipe, FormsModule, RouterLink],
  templateUrl: './promotion.html',
})
export class PromotionComponent implements OnInit {
  environments = signal<EnvironmentItem[]>([]);
  sourceKey = signal('local');
  targetKey = signal('local');
  includeDeletions = signal(false);
  plan = signal<PromotionPlan | null>(null);
  selectedFiles = signal<Set<string>>(new Set<string>());
  resolutionOverrides = signal<Record<string, string>>({});
  generatingPlan = signal(false);
  applying = signal(false);
  previewing = signal(false);
  result = signal<PromotionApplyResult | null>(null);
  preview = signal<PromotionMergePreview | null>(null);
  error = signal<string | null>(null);
  message = signal<string | null>(null);
  saveAiSummary = signal(false);
  aiAnswer = signal<AiAssistantResponse | null>(null);
  aiQuestion = signal('');
  aiLoading = signal(false);
  aiError = signal<string | null>(null);
  resolutionSuggestionLoadingPath = signal<string | null>(null);
  resolutionSuggestionOpenPath = signal<string | null>(null);
  resolutionSuggestionErrors = signal<Record<string, string>>({});
  resolutionSuggestions = signal<Record<string, AiAssistantResponse>>({});
  selectedWorkflowDiffKey = signal('');
  baseline = signal<PromotionComparisonBaseline | null>(null);
  baselineCommits = signal<GitCommit[]>([]);
  selectedBaselineSha = '';
  baselineSaving = signal(false);

  hasBlockingErrors = computed(() => (this.plan()?.blockingErrors.length ?? 0) > 0 || (this.preview()?.blockingErrors.length ?? 0) > 0);
  selectedCount = computed(() => this.selectedFiles().size);
  unresolvedConflictCount = computed(() => (this.plan()?.workflowChanges ?? []).filter((change) => change.isConflict && !this.resolutionFor(change)).length);
  deleteTargetCount = computed(() => this.workflowResolutions().filter((item) => item.resolution === 'delete-target').length);
  canApply = computed(() => Boolean(this.plan() && this.preview() && !this.hasBlockingErrors() && this.unresolvedConflictCount() === 0 && this.selectedCount() > 0 && !this.applying()));
  applyBlockers = computed(() => {
    const blockers: string[] = [];
    if (!this.plan()) blockers.push('Generate a promotion plan first.');
    if (this.plan() && !this.preview()) blockers.push('Run the merge preview before applying so the dry run can validate this selection.');
    if (this.hasBlockingErrors()) blockers.push('Resolve blocking validation issues before applying.');
    if (this.unresolvedConflictCount() > 0) blockers.push('Resolve every conflicted workflow before applying.');
    if (this.plan() && this.selectedCount() === 0) blockers.push('No workflows are selected to write or delete. Choose Use source or Delete target for at least one workflow.');
    if (this.applying()) blockers.push('Promotion is already being applied.');
    return blockers;
  });

  constructor(private readonly api: ApiService, private readonly confirmation: ConfirmationService, private readonly toast: ToastService) {}

  ngOnInit(): void {
    this.api.getEnvironments().subscribe({
      next: (environments) => {
        this.environments.set(environments);
        this.resetMissingEnvironmentSelections(environments);
        this.loadBaselineContext();
        if (this.sourceKey() !== this.targetKey()) {
          this.generatePlan();
        }
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load environments.'),
    });
  }

  generatePlan(): void {
    this.error.set(null);
    this.message.set(null);
    this.result.set(null);
    this.preview.set(null);
    this.generatingPlan.set(false);

    if (this.sourceKey() === this.targetKey()) {
      this.error.set('Choose different source and target environments.');
      return;
    }

    if (!this.environmentExists(this.sourceKey()) || !this.environmentExists(this.targetKey())) {
      this.resetMissingEnvironmentSelections(this.environments());
      this.error.set('One of the selected environments no longer exists. I reset the selectors to available environments.');
      return;
    }

    this.generatingPlan.set(true);
    this.api.getPromotionPlan(this.sourceKey(), this.targetKey(), this.includeDeletions()).subscribe({
      next: (plan) => {
        this.plan.set(plan);
        this.baseline.set(plan.baseline);
        this.selectedBaselineSha = plan.baseline?.commitSha ?? '';
        this.resolutionOverrides.set({});
        this.resolutionSuggestionOpenPath.set(null);
        this.resolutionSuggestionErrors.set({});
        this.resolutionSuggestions.set({});
        this.selectedFiles.set(new Set(plan.workflowChanges
          .filter((change) => this.defaultResolution(change) === 'use-source' || this.defaultResolution(change) === 'delete-target')
          .map((change) => change.workflowFilePath)));
        this.generatingPlan.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not generate promotion plan.');
        this.generatingPlan.set(false);
      },
    });
  }

  onEnvironmentChanged(side: 'source' | 'target', key: string): void {
    if (side === 'source') {
      this.sourceKey.set(key);
    } else {
      this.targetKey.set(key);
    }
    this.loadBaselineContext();
  }

  saveBaseline(): void {
    this.baselineSaving.set(true);
    this.error.set(null);
    this.api.setPromotionBaseline({
      sourceEnvironmentKey: this.sourceKey(),
      targetEnvironmentKey: this.targetKey(),
      commitSha: this.selectedBaselineSha || null,
      label: null,
    }).subscribe({
      next: (baseline) => {
        this.baseline.set(baseline);
        this.message.set(baseline ? 'Comparison baseline saved.' : 'Comparison baseline cleared. Using direct environment comparison.');
        this.baselineSaving.set(false);
        this.generatePlan();
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not save comparison baseline.');
        this.baselineSaving.set(false);
      },
    });
  }

  scrollToWorkflowChanges(): void {
    document.getElementById('workflow-changes')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  toggleSelection(filePath: string, checked: boolean): void {
    const next = new Set(this.selectedFiles());
    if (checked) {
      next.add(filePath);
    } else {
      next.delete(filePath);
    }
    this.selectedFiles.set(next);
  }

  isSelected(filePath: string): boolean {
    return this.selectedFiles().has(filePath);
  }

  isSelectable(changeType: string): boolean {
    return changeType !== 'unchanged' && changeType !== 'manual-merge';
  }

  resolutionFor(change: PromotionWorkflowChange): string {
    return this.resolutionForInternal(change);
  }

  setResolution(change: PromotionWorkflowChange, resolution: string): void {
    this.resolutionOverrides.set({ ...this.resolutionOverrides(), [change.workflowFilePath]: resolution });
    const next = new Set(this.selectedFiles());
    if (resolution === 'use-source' || resolution === 'delete-target') {
      next.add(change.workflowFilePath);
    } else {
      next.delete(change.workflowFilePath);
    }
    this.selectedFiles.set(next);
    this.preview.set(null);
  }

  workflowResolutions(): PromotionWorkflowResolution[] {
    return (this.plan()?.workflowChanges ?? [])
      .map((change) => ({
        workflowFilePath: change.workflowFilePath,
        resolution: this.resolutionForInternal(change),
      }))
      .filter((item) => item.resolution);
  }

  previewMerge(): void {
    const plan = this.plan();
    if (!plan) {
      return;
    }

    this.previewing.set(true);
    this.error.set(null);
    this.preview.set(null);
    this.api.previewPromotionMerge({
      sourceEnvironmentKey: plan.sourceEnvironment.key,
      targetEnvironmentKey: plan.targetEnvironment.key,
      workflowResolutions: this.workflowResolutions(),
      includeDeletions: this.includeDeletions(),
      confirmDeletions: this.deleteTargetCount() === 0 ? false : true,
    }).subscribe({
      next: (preview) => {
        this.preview.set(preview);
        this.previewing.set(false);
        this.toast.success(`Dry run complete: ${preview.workflowsToWrite.length} write, ${preview.workflowsToKeep.length} keep, ${preview.workflowsToSkip.length} skip, ${preview.workflowsToDelete.length} delete.`);
      },
      error: (response) => {
        const message = response?.error?.error ?? 'Merge preview failed.';
        this.error.set(message);
        this.toast.error(message, { actionLabel: 'Retry', action: () => this.previewMerge() });
        this.previewing.set(false);
      },
    });
  }

  diffTitle(diff: WorkflowSemanticDiff | null, fallback: string): string {
    if (!diff) {
      return fallback;
    }

    return `${diff.changeType}: +${diff.summary.addedNodes} nodes, -${diff.summary.removedNodes} nodes, ${diff.summary.modifiedNodes} modified`;
  }

  workflowDiffKey(change: PromotionWorkflowChange): string {
    return change.workflowFilePath || change.workflowName;
  }

  selectWorkflowDiff(change: PromotionWorkflowChange): void {
    if (!change.semanticDiff) {
      return;
    }

    this.selectedWorkflowDiffKey.set(this.workflowDiffKey(change));
  }

  closeWorkflowDiff(): void {
    this.selectedWorkflowDiffKey.set('');
  }

  selectedWorkflowDiff(): WorkflowSemanticDiff | null {
    const key = this.selectedWorkflowDiffKey();
    const change = (this.plan()?.workflowChanges ?? []).find((item) => this.workflowDiffKey(item) === key);
    return change?.semanticDiff ?? null;
  }

  selectedWorkflowDiffChange(): PromotionWorkflowChange | null {
    const key = this.selectedWorkflowDiffKey();
    return (this.plan()?.workflowChanges ?? []).find((item) => this.workflowDiffKey(item) === key) ?? null;
  }

  workflowHasVisibleChanges(workflow: WorkflowSemanticDiff | null): boolean {
    return Boolean(workflow)
      && (workflow!.changeType !== 'unchanged'
        || workflow!.summary.addedNodes > 0
        || workflow!.summary.removedNodes > 0
        || workflow!.summary.modifiedNodes > 0
        || workflow!.summary.changedCredentials > 0
        || workflow!.summary.changedConnections > 0
        || workflow!.summary.changedWorkflowSettings > 0
        || workflow!.warnings.length > 0);
  }

  nodeChangeCount(node: WorkflowSemanticDiff['nodeChanges'][number]): number {
    return node.parameterChanges.length + node.credentialChanges.length + node.metadataChanges.length;
  }

  async apply(): Promise<void> {
    if (!this.canApply()) {
      return;
    }

    const preview = this.preview();
    if (!preview || !await this.confirmation.confirm({
      title: 'Apply promotion?',
      message: `This will write ${preview.workflowsToWrite.length} workflow(s) to ${this.targetKey()}${preview.workflowsToDelete.length ? ` and delete ${preview.workflowsToDelete.length} workflow(s)` : ''}. A Git commit will be created if changes exist.`,
      confirmLabel: 'Apply promotion',
      danger: preview.workflowsToDelete.length > 0,
    })) return;

    this.applying.set(true);
    this.error.set(null);
    this.result.set(null);
    if (!this.environmentExists(this.sourceKey()) || !this.environmentExists(this.targetKey())) {
      this.resetMissingEnvironmentSelections(this.environments());
      this.error.set('One of the selected environments no longer exists. I reset the selectors to available environments.');
      this.applying.set(false);
      return;
    }

    this.api.applyPromotion({
      sourceEnvironmentKey: this.sourceKey(),
      targetEnvironmentKey: this.targetKey(),
      selectedWorkflowFiles: Array.from(this.selectedFiles()),
      workflowResolutions: this.workflowResolutions(),
      confirmation: true,
      includeDeletions: this.includeDeletions(),
      confirmDeletions: this.deleteTargetCount() > 0,
    }).subscribe({
      next: (result) => {
        this.result.set(result);
        this.message.set(result.message);
        this.toast.success(result.message);
        this.applying.set(false);
      },
      error: (response) => {
        const message = response?.error?.error ?? 'Promotion failed.';
        this.error.set(message);
        this.toast.error(message, { actionLabel: 'Retry', action: () => this.apply() });
        this.applying.set(false);
      },
    });
  }

  summarizePromotionPlan(): void {
    const plan = this.plan();
    if (!plan) {
      this.aiError.set('Generate a promotion plan first.');
      return;
    }

    this.runAi(() => this.api.summarizePromotionPlan(plan, this.saveAiSummary()));
  }

  findPromotionRisks(): void {
    const plan = this.plan();
    if (!plan) {
      this.aiError.set('Generate a promotion plan first.');
      return;
    }

    this.runAi(() => this.api.askAi({
      question: 'Find likely risks and blocking issues in this promotion plan. Highlight missing credential mappings.',
      scope: 'current promotion plan',
      sourceEnvironmentKey: plan.sourceEnvironment.key,
      targetEnvironmentKey: plan.targetEnvironment.key,
      promotionPlan: plan,
    }));
  }

  explainPromotionCredentials(): void {
    const plan = this.plan();
    if (!plan) {
      this.aiError.set('Generate a promotion plan first.');
      return;
    }

    this.runAi(() => this.api.askAi({
      question: 'Explain credentials affected by this promotion plan and what needs review.',
      scope: 'current promotion plan',
      sourceEnvironmentKey: plan.sourceEnvironment.key,
      targetEnvironmentKey: plan.targetEnvironment.key,
      promotionPlan: plan,
    }));
  }

  suggestResolution(change: PromotionWorkflowChange): void {
    const plan = this.plan();
    if (!plan) {
      this.setResolutionSuggestionError(change.workflowFilePath, 'Generate a promotion plan first.');
      return;
    }

    this.resolutionSuggestionOpenPath.set(change.workflowFilePath);
    this.resolutionSuggestionLoadingPath.set(change.workflowFilePath);
    this.clearResolutionSuggestionError(change.workflowFilePath);
    this.api.explainConflict(plan, change).subscribe({
      next: (answer) => {
        this.resolutionSuggestions.set({ ...this.resolutionSuggestions(), [change.workflowFilePath]: answer });
        this.resolutionSuggestionLoadingPath.set(null);
      },
      error: (response) => {
        this.setResolutionSuggestionError(change.workflowFilePath, response?.error?.error ?? 'AI assistant request failed.');
        this.resolutionSuggestionLoadingPath.set(null);
      },
    });
  }

  closeResolutionSuggestion(): void {
    this.resolutionSuggestionOpenPath.set(null);
  }

  resolutionSuggestionFor(change: PromotionWorkflowChange): AiAssistantResponse | null {
    return this.resolutionSuggestions()[change.workflowFilePath] ?? null;
  }

  resolutionSuggestionErrorFor(change: PromotionWorkflowChange): string | null {
    return this.resolutionSuggestionErrors()[change.workflowFilePath] ?? null;
  }

  isResolutionSuggestionOpen(change: PromotionWorkflowChange): boolean {
    return this.resolutionSuggestionOpenPath() === change.workflowFilePath;
  }

  isResolutionSuggestionLoading(change: PromotionWorkflowChange): boolean {
    return this.resolutionSuggestionLoadingPath() === change.workflowFilePath;
  }

  askAi(): void {
    const plan = this.plan();
    const question = this.aiQuestion().trim();
    if (!plan || !question) {
      this.aiError.set('Generate a promotion plan and enter a question first.');
      return;
    }

    this.runAi(() => this.api.askAi({
      question,
      scope: 'current promotion plan',
      sourceEnvironmentKey: plan.sourceEnvironment.key,
      targetEnvironmentKey: plan.targetEnvironment.key,
      promotionPlan: plan,
    }));
  }

  private environmentExists(environmentKey: string): boolean {
    return this.environments().some((environment) => environment.key === environmentKey);
  }

  private loadBaselineContext(): void {
    if (this.sourceKey() === this.targetKey()) {
      this.baseline.set(null);
      this.baselineCommits.set([]);
      this.selectedBaselineSha = '';
      return;
    }

    this.baselineCommits.set([]);
    this.api.getPromotionBaseline(this.sourceKey(), this.targetKey()).subscribe({
      next: (baseline) => {
        this.baseline.set(baseline);
        this.selectedBaselineSha = baseline?.commitSha ?? '';
      },
      error: () => this.baseline.set(null),
    });
    this.loadBaselineCommits(this.sourceKey());
    this.loadBaselineCommits(this.targetKey());
  }

  private loadBaselineCommits(environmentKey: string): void {
    this.api.getCommits(50, environmentKey).subscribe({
      next: (commits) => {
        const merged = [...this.baselineCommits(), ...commits];
        this.baselineCommits.set(Array.from(new Map(merged.map((commit) => [commit.sha, commit])).values()));
      },
      error: () => {},
    });
  }

  private resetMissingEnvironmentSelections(environments: EnvironmentItem[]): void {
    if (environments.length === 0) {
      this.sourceKey.set('local');
      this.targetKey.set('local');
      this.plan.set(null);
      this.selectedFiles.set(new Set<string>());
      return;
    }

    if (!environments.some((environment) => environment.key === this.sourceKey())) {
      this.sourceKey.set(environments[0].key);
    }

    if (!environments.some((environment) => environment.key === this.targetKey()) || this.targetKey() === this.sourceKey()) {
      this.targetKey.set(environments.find((environment) => environment.key !== this.sourceKey())?.key ?? environments[0].key);
    }
  }

  private runAi(request: () => ReturnType<ApiService['askAi']>): void {
    this.aiLoading.set(true);
    this.aiError.set(null);
    request().subscribe({
      next: (answer) => {
        this.aiAnswer.set(answer);
        this.aiLoading.set(false);
      },
      error: (response) => {
        this.aiError.set(response?.error?.error ?? 'AI assistant request failed.');
        this.aiLoading.set(false);
      },
    });
  }

  private setResolutionSuggestionError(filePath: string, message: string): void {
    this.resolutionSuggestionErrors.set({ ...this.resolutionSuggestionErrors(), [filePath]: message });
    this.resolutionSuggestionOpenPath.set(filePath);
  }

  private clearResolutionSuggestionError(filePath: string): void {
    const { [filePath]: _, ...remaining } = this.resolutionSuggestionErrors();
    this.resolutionSuggestionErrors.set(remaining);
  }

  private resolutionForInternal(change: PromotionWorkflowChange): string {
    const override = this.resolutionOverrides()[change.workflowFilePath];
    if (override) {
      return override;
    }

    const selected = this.selectedFiles().has(change.workflowFilePath);
    if (!selected && change.isConflict) {
      return '';
    }

    if (!selected) {
      return change.changeType === 'deleted' ? 'keep-target' : 'skip';
    }

    return this.defaultResolution(change);
  }

  private defaultResolution(change: PromotionWorkflowChange): string {
    return change.resolution ?? (change.changeType === 'deleted' ? 'keep-target' : change.changeType === 'unchanged' ? 'keep-target' : '');
  }
}
