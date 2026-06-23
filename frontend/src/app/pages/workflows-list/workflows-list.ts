import { DatePipe } from '@angular/common';
import { Component, computed, effect, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, N8nApiConfig, WorkflowApiReconciliationPreview, WorkflowApiSyncResult, WorkflowDeploymentPreview, WorkflowDeploymentResult, WorkflowListItem } from '../../services/api';
import { ConfirmationService } from '../../services/confirmation';
import { ToastService } from '../../services/toast';
import { UiAlertComponent } from '../../components/ui-alert/ui-alert';
import { UiPanelComponent } from '../../components/ui-panel/ui-panel';
import { UiTableComponent } from '../../components/ui-table/ui-table';

@Component({
  selector: 'app-workflows-list',
  imports: [DatePipe, FormsModule, RouterLink, UiAlertComponent, UiPanelComponent, UiTableComponent],
  templateUrl: './workflows-list.html',
})
export class WorkflowsListComponent {
  workflows = signal<WorkflowListItem[]>([]);
  error = signal<string | null>(null);
  loading = signal(false);
  syncing = signal(false);
  previewing = signal(false);
  apiConfig = signal<N8nApiConfig | null>(null);
  syncResult = signal<WorkflowApiSyncResult | null>(null);
  reconciliation = signal<WorkflowApiReconciliationPreview | null>(null);
  selectedRemoteIds = signal<string[]>([]);
  selectedWorkflowPaths = signal<string[]>([]);
  targetEnvironment = '';
  deploymentPreview = signal<WorkflowDeploymentPreview | null>(null);
  deploymentResult = signal<WorkflowDeploymentResult | null>(null);
  previewingDeployment = signal(false);
  deploying = signal(false);
  selectedWorkflowCount = computed(() => this.selectedWorkflowPaths().length);
  totalCount = signal(0);
  page = signal(1);
  search = '';

  constructor(readonly api: ApiService, private readonly confirmation: ConfirmationService, private readonly toast: ToastService) {
    effect(() => {
      const environmentKey = this.api.selectedEnvironmentKey();
      this.page.set(1);
      this.selectedWorkflowPaths.set([]);
      this.deploymentPreview.set(null);
      this.deploymentResult.set(null);
      this.load(environmentKey);
      this.loadApiConfig(environmentKey);
    });
  }

  private loadApiConfig(environmentKey: string): void {
    this.api.getN8nApiConfig(environmentKey).subscribe({
      next: (config) => this.apiConfig.set(config),
      error: () => this.apiConfig.set(null),
    });
  }

  syncFromN8n(): void {
    this.syncing.set(true);
    this.error.set(null);
    this.syncResult.set(null);
    this.api.syncWorkflowsFromN8nApi().subscribe({
      next: (result) => {
        this.syncResult.set(result);
        this.syncing.set(false);
        this.toast.success(`n8n sync finished: ${result.importedWorkflowsCount} workflow(s) imported.`);
        this.load(this.api.selectedEnvironmentKey());
      },
      error: (response) => {
        const message = response?.error?.error ?? 'Could not sync workflows from n8n.';
        this.error.set(message);
        this.toast.error(message, { actionLabel: 'Retry', action: () => this.syncFromN8n() });
        this.syncing.set(false);
      },
    });
  }

  previewReconciliation(): void {
    this.previewing.set(true);
    this.error.set(null);
    this.syncResult.set(null);
    this.api.previewWorkflowReconciliation().subscribe({
      next: (preview) => {
        this.reconciliation.set(preview);
        this.selectedRemoteIds.set(preview.items.filter((item) => item.canSync && item.workflowId).map((item) => item.workflowId!));
        this.previewing.set(false);
      },
      error: (response) => { this.error.set(response?.error?.error ?? 'Could not compare n8n with the Git snapshot.'); this.previewing.set(false); },
    });
  }

  toggleRemote(id: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.selectedRemoteIds.update((ids) => checked ? [...ids, id] : ids.filter((value) => value !== id));
  }

  syncSelected(): void {
    if (this.selectedRemoteIds().length === 0) return;
    this.syncing.set(true); this.error.set(null);
    this.api.syncSelectedWorkflowsFromN8nApi(this.selectedRemoteIds()).subscribe({
      next: (result) => { this.syncResult.set(result); this.reconciliation.set(null); this.syncing.set(false); this.toast.success(`Selected sync finished: ${result.importedWorkflowsCount} workflow(s) imported.`); this.load(this.api.selectedEnvironmentKey()); },
      error: (response) => { const message = response?.error?.error ?? 'Could not sync the selected workflows.'; this.error.set(message); this.toast.error(message, { actionLabel: 'Retry', action: () => this.syncSelected() }); this.syncing.set(false); },
    });
  }

  toggleWorkflow(filePath: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.selectedWorkflowPaths.update((paths) => checked ? [...paths, filePath] : paths.filter((path) => path !== filePath));
    this.deploymentPreview.set(null);
  }

  isWorkflowSelected(filePath: string): boolean {
    return this.selectedWorkflowPaths().includes(filePath);
  }

  deploymentCreateCount(): number {
    return this.deploymentPreview()?.workflows.filter((workflow) => workflow.action === 'create').length ?? 0;
  }

  deploymentUpdateCount(): number {
    return this.deploymentPreview()?.workflows.filter((workflow) => workflow.action === 'update').length ?? 0;
  }

  previewDeployment(): void {
    if (!this.targetEnvironment || this.targetEnvironment === this.api.selectedEnvironmentKey() || this.selectedWorkflowCount() === 0) {
      this.error.set('Choose a different target environment and select at least one workflow.');
      return;
    }

    this.previewingDeployment.set(true);
    this.error.set(null);
    this.deploymentPreview.set(null);
    this.deploymentResult.set(null);
    this.api.previewWorkflowDeployment({
      sourceEnvironmentKey: this.api.selectedEnvironmentKey(),
      targetEnvironmentKey: this.targetEnvironment,
      workflowFilePaths: this.selectedWorkflowPaths(),
    }).subscribe({
      next: (preview) => { this.deploymentPreview.set(preview); this.previewingDeployment.set(false); },
      error: (response) => { this.error.set(response?.error?.error ?? 'Workflow deployment preview failed.'); this.previewingDeployment.set(false); },
    });
  }

  async deploySelected(): Promise<void> {
    const preview = this.deploymentPreview();
    if (!preview || preview.blockingErrors.length > 0 || this.deploying()) return;
    if (!await this.confirmation.confirm({
      title: 'Deploy selected workflows?',
      message: `This will create ${this.deploymentCreateCount()} and update ${this.deploymentUpdateCount()} workflow(s) in ${preview.targetEnvironmentKey}. Deployed workflows remain inactive.`,
      confirmLabel: 'Deploy workflows',
      danger: true,
    })) return;

    this.deploying.set(true);
    this.error.set(null);
    this.api.deployWorkflows({
      sourceEnvironmentKey: preview.sourceEnvironmentKey,
      targetEnvironmentKey: preview.targetEnvironmentKey,
      workflowFilePaths: this.selectedWorkflowPaths(),
      confirmation: true,
      activateWorkflows: false,
    }).subscribe({
      next: (result) => { this.deploymentResult.set(result); this.deploying.set(false); this.toast.success(`Deployed ${result.createdWorkflowIds.length + result.updatedWorkflowIds.length} workflow(s) to ${result.targetEnvironmentKey}.`); },
      error: (response) => { this.error.set(response?.error?.error ?? 'Workflow deployment failed.'); this.deploying.set(false); },
    });
  }

  private load(environmentKey: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getWorkflowPage(environmentKey, this.page(), 25, this.search).subscribe({
      next: (result) => {
        this.workflows.set(result.items);
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => {
        this.workflows.set([]);
        this.error.set(`Could not load workflows for ${environmentKey}.`);
        this.loading.set(false);
      },
    });
  }

  searchWorkflows(): void {
    this.page.set(1);
    this.load(this.api.selectedEnvironmentKey());
  }

  previous(): void {
    if (this.page() > 1) { this.page.update((value) => value - 1); this.load(this.api.selectedEnvironmentKey()); }
  }

  next(): void {
    if (this.page() * 25 < this.totalCount()) { this.page.update((value) => value + 1); this.load(this.api.selectedEnvironmentKey()); }
  }
}
