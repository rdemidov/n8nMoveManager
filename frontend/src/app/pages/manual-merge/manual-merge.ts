import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  AiAssistantResponse,
  ApiService,
  ManualMergeApplyResult,
  ManualMergeResult,
  ManualMergeSelection,
  ManualMergeSession,
  NodeMergeSelection,
  ParameterMergeSelection,
  WorkflowSettingMergeSelection,
} from '../../services/api';

@Component({
  selector: 'app-manual-merge',
  imports: [FormsModule, RouterLink],
  templateUrl: './manual-merge.html',
})
export class ManualMergeComponent implements OnInit {
  readonly session = signal<ManualMergeSession | null>(null);
  readonly preview = signal<ManualMergeResult | null>(null);
  readonly applyResult = signal<ManualMergeApplyResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly previewing = signal(false);
  readonly applying = signal(false);
  readonly showJson = signal(false);
  readonly resultTab = signal<'target' | 'source' | 'nodes'>('target');
  readonly openedNodeKey = signal<string | null>(null);
  readonly aiAnswer = signal<AiAssistantResponse | null>(null);
  readonly aiLoading = signal(false);
  readonly aiError = signal<string | null>(null);
  readonly confirmApply = signal(false);

  readonly blockingErrors = computed(() => this.preview()?.blockingErrors ?? []);
  readonly canApply = computed(() => Boolean(this.preview() && this.blockingErrors().length === 0 && this.confirmApply() && !this.applying()));
  readonly nodeResolutionSummary = computed(() => {
    const nodes = this.session()?.selection.nodeSelections ?? [];
    return {
      source: nodes.filter((node) => node.resolution === 'use-source').length,
      target: nodes.filter((node) => node.resolution === 'use-target').length,
      parameterLevel: nodes.filter((node) => node.resolution === 'parameter-level').length,
      excluded: nodes.filter((node) => node.resolution === 'exclude').length,
    };
  });
  readonly mappedCredentialCount = computed(() =>
    this.session()?.sourceWorkflow.credentialReferences.filter((credential) => credential.isMapped).length ?? 0);
  readonly missingCredentialCount = computed(() =>
    this.session()?.sourceWorkflow.credentialReferences.filter((credential) => !credential.isMapped).length ?? 0);

  constructor(
    readonly api: ApiService,
    private readonly route: ActivatedRoute,
    private readonly router: Router,
  ) {}

  ngOnInit(): void {
    const existingSession = this.route.snapshot.paramMap.get('sessionId');
    if (existingSession) {
      this.loadSession(existingSession);
      return;
    }

    const query = this.route.snapshot.queryParamMap;
    const source = query.get('source');
    const target = query.get('target');
    const path = query.get('path');
    if (!source || !target || !path) {
      this.error.set('Manual merge needs source, target, and workflow path.');
      return;
    }

    this.loading.set(true);
    this.api.createManualMergeSession({
      sourceEnvironmentKey: source,
      targetEnvironmentKey: target,
      workflowFilePath: path,
      sourceCommitSha: query.get('sourceCommitSha'),
      targetCommitSha: query.get('targetCommitSha'),
    }).subscribe({
      next: (session) => {
        this.session.set(session);
        this.loading.set(false);
        this.router.navigate(['/manual-merge', session.id], { replaceUrl: true });
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not create manual merge session.');
        this.loading.set(false);
      },
    });
  }

  loadSession(sessionId: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getManualMergeSession(sessionId).subscribe({
      next: (session) => {
        this.session.set(session);
        this.loading.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not load manual merge session.');
        this.loading.set(false);
      },
    });
  }

  setSetting(selection: WorkflowSettingMergeSelection, side: string): void {
    this.updateSelection((current) => ({
      ...current,
      workflowSettingsSelections: current.workflowSettingsSelections.map((item) =>
        item.propertyName === selection.propertyName ? { ...item, selectedSide: side } : item),
    }));
  }

  setNode(selection: NodeMergeSelection, resolution: string): void {
    this.openedNodeKey.set(selection.nodeMatchKey);
    this.updateSelection((current) => ({
      ...current,
      nodeSelections: current.nodeSelections.map((item) =>
        item.nodeMatchKey === selection.nodeMatchKey ? { ...item, resolution } : item),
    }));
  }

  setParameter(selection: ParameterMergeSelection, side: string): void {
    this.updateSelection((current) => ({
      ...current,
      parameterSelections: current.parameterSelections.map((item) =>
        item.nodeMatchKey === selection.nodeMatchKey && item.parameterPath === selection.parameterPath
          ? { ...item, selectedSide: side }
          : item),
    }));
  }

  setConnectionSelection(side: string): void {
    this.updateSelection((current) => ({ ...current, connectionSelection: side }));
  }

  parametersFor(node: NodeMergeSelection): ParameterMergeSelection[] {
    return this.session()?.selection.parameterSelections.filter((item) => item.nodeMatchKey === node.nodeMatchKey) ?? [];
  }

  buildPreview(): void {
    const session = this.session();
    if (!session) {
      return;
    }

    this.previewing.set(true);
    this.error.set(null);
    this.api.previewManualMerge(session.id).subscribe({
      next: (preview) => {
        this.preview.set(preview);
        this.previewing.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not build manual merge preview.');
        this.previewing.set(false);
      },
    });
  }

  apply(): void {
    const session = this.session();
    if (!session || !this.canApply()) {
      return;
    }

    this.applying.set(true);
    this.error.set(null);
    this.api.applyManualMerge(session.id).subscribe({
      next: (result) => {
        this.applyResult.set(result);
        this.message.set(result.commitSha ? `Manual merge committed: ${result.commitSha}` : result.message);
        this.applying.set(false);
        this.confirmApply.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not apply manual merge.');
        this.applying.set(false);
      },
    });
  }

  resultDiffSummary(side: 'target' | 'source'): string {
    const diff = side === 'target'
      ? this.preview()?.semanticDiffResultVsTarget
      : this.preview()?.semanticDiffResultVsSource;
    if (!diff) {
      return 'Build preview to inspect the generated result.';
    }

    return `+${diff.summary.addedNodes} nodes, -${diff.summary.removedNodes} nodes, ${diff.summary.modifiedNodes} modified, ${diff.summary.changedConnections} connection changes, ${diff.summary.changedCredentials} credential changes.`;
  }

  resultDiffNodes(side: 'target' | 'source') {
    const diff = side === 'target'
      ? this.preview()?.semanticDiffResultVsTarget
      : this.preview()?.semanticDiffResultVsSource;
    return diff?.nodeChanges ?? [];
  }

  resultDiffSettings(side: 'target' | 'source') {
    const diff = side === 'target'
      ? this.preview()?.semanticDiffResultVsTarget
      : this.preview()?.semanticDiffResultVsSource;
    return diff?.workflowSettingsChanges ?? [];
  }

  resultDiffCredentials(side: 'target' | 'source') {
    const diff = side === 'target'
      ? this.preview()?.semanticDiffResultVsTarget
      : this.preview()?.semanticDiffResultVsSource;
    return diff?.credentialChanges ?? [];
  }

  resultDiffConnections(side: 'target' | 'source') {
    const diff = side === 'target'
      ? this.preview()?.semanticDiffResultVsTarget
      : this.preview()?.semanticDiffResultVsSource;
    return diff?.connectionChanges ?? [];
  }

  nodeChangeCount(node: { parameterChanges: unknown[]; credentialChanges: unknown[]; metadataChanges: unknown[] }): number {
    return node.parameterChanges.length + node.credentialChanges.length + node.metadataChanges.length;
  }

  explainNode(node: NodeMergeSelection): void {
    const session = this.session();
    if (!session) {
      return;
    }

    this.aiLoading.set(true);
    this.aiError.set(null);
    this.api.askAi({
      question: `Explain the manual merge conflict for node "${node.nodeName}" and identify risks. Do not choose automatically.`,
      scope: 'manual merge node conflict',
      sourceEnvironmentKey: session.sourceEnvironmentKey,
      targetEnvironmentKey: session.targetEnvironmentKey,
      workflowFilePath: session.workflowFilePath,
      diffContext: {
        source: session.targetEnvironmentKey,
        target: session.sourceEnvironmentKey,
        generatedAt: session.updatedAt,
        workflows: [session.semanticDiff],
      },
    }).subscribe({
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

  suggestParameters(node: NodeMergeSelection): void {
    const session = this.session();
    if (!session) {
      return;
    }

    this.aiLoading.set(true);
    this.aiError.set(null);
    this.api.askAi({
      question: `Suggest source/target choices for changed parameters on node "${node.nodeName}". Do not apply changes automatically.`,
      scope: 'manual merge parameter choices',
      sourceEnvironmentKey: session.sourceEnvironmentKey,
      targetEnvironmentKey: session.targetEnvironmentKey,
      workflowFilePath: session.workflowFilePath,
      diffContext: {
        source: session.targetEnvironmentKey,
        target: session.sourceEnvironmentKey,
        generatedAt: session.updatedAt,
        workflows: [session.semanticDiff],
      },
    }).subscribe({
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

  private updateSelection(update: (selection: ManualMergeSelection) => ManualMergeSelection): void {
    const session = this.session();
    if (!session) {
      return;
    }

    const selection = update(session.selection);
    const next = { ...session, selection };
    this.session.set(next);
    this.preview.set(null);
    this.saving.set(true);
    this.api.updateManualMergeSelection(session.id, selection).subscribe({
      next: (updated) => {
        this.session.set(updated);
        this.saving.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Could not save manual merge selection.');
        this.saving.set(false);
      },
    });
  }
}
