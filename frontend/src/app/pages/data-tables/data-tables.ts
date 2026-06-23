import { DatePipe } from '@angular/common';
import { Component, effect, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, DataTableComparison, DataTableItem, DataTablePromotionPlan, N8nApiConfigRequest } from '../../services/api';

@Component({
  selector: 'app-data-tables',
  imports: [DatePipe, FormsModule],
  templateUrl: './data-tables.html',
})
export class DataTablesComponent {
  tables = signal<DataTableItem[]>([]);
  comparison = signal<DataTableComparison | null>(null);
  promotionPlan = signal<DataTablePromotionPlan | null>(null);
  totalCount = signal(0);
  page = signal(1);
  loading = signal(false);
  saving = signal(false);
  syncing = signal(false);
  syncingWorkflows = signal(false);
  staging = signal(false);
  error = signal<string | null>(null);
  message = signal<string | null>(null);
  search = '';
  targetEnvironment = '';
  selectedTableIds = signal<string[]>([]);
  apiKey = '';
  form: N8nApiConfigRequest = { enabled: false, baseUrl: '', dataTablesPath: '/api/v1/data-tables', dataTablesWritePathTemplate: '', workflowApiPath: '/api/v1/workflows', apiKey: '' };

  constructor(readonly api: ApiService) {
    effect(() => {
      this.api.selectedEnvironmentKey();
      this.page.set(1);
      this.loadConfig();
      this.loadTables();
    });
  }

  loadConfig(): void {
    this.api.getN8nApiConfig().subscribe({
      next: (config) => this.form = { enabled: config.enabled, baseUrl: config.baseUrl, dataTablesPath: config.dataTablesPath, dataTablesWritePathTemplate: config.dataTablesWritePathTemplate ?? '', workflowApiPath: config.workflowApiPath, apiKey: '' },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load n8n API settings.'),
    });
  }

  loadTables(): void {
    this.loading.set(true);
    this.api.getDataTables(undefined, this.page(), 25, this.search).subscribe({
      next: (result) => { this.tables.set(result.items); this.totalCount.set(result.totalCount); this.loading.set(false); },
      error: (response) => { this.error.set(response?.error?.error ?? 'Could not load Data Table snapshots.'); this.loading.set(false); },
    });
  }

  save(): void {
    this.saving.set(true); this.error.set(null); this.message.set(null);
    this.api.saveN8nApiConfig(this.api.selectedEnvironmentKey(), { ...this.form, apiKey: this.apiKey.trim() || null }).subscribe({
      next: (config) => { this.form = { enabled: config.enabled, baseUrl: config.baseUrl, dataTablesPath: config.dataTablesPath, dataTablesWritePathTemplate: config.dataTablesWritePathTemplate ?? '', workflowApiPath: config.workflowApiPath, apiKey: '' }; this.apiKey = ''; this.message.set('n8n API settings saved.'); this.saving.set(false); },
      error: (response) => { this.error.set(response?.error?.error ?? 'Could not save n8n API settings.'); this.saving.set(false); },
    });
  }

  sync(): void {
    this.syncing.set(true); this.error.set(null); this.message.set(null);
    this.api.syncDataTables().subscribe({
      next: (result) => { this.message.set(result.skippedCommit ? `Synced ${result.syncedCount} tables; no schema changes.` : `Synced ${result.syncedCount} tables and committed ${result.changedCount} schema changes.`); this.syncing.set(false); this.loadTables(); },
      error: (response) => { this.error.set(response?.error?.error ?? 'Data Table sync failed.'); this.syncing.set(false); },
    });
  }

  syncWorkflows(): void {
    this.syncingWorkflows.set(true); this.error.set(null); this.message.set(null);
    this.api.syncWorkflowsFromN8nApi().subscribe({
      next: (result) => {
        this.message.set(result.skippedCommit
          ? `Fetched ${result.fetchedWorkflowsCount} workflow(s); no changes detected.`
          : `Fetched and versioned ${result.importedWorkflowsCount} workflow(s) in Git.`);
        this.syncingWorkflows.set(false); this.loadTables();
      },
      error: (response) => { this.error.set(response?.error?.error ?? 'Workflow API sync failed.'); this.syncingWorkflows.set(false); },
    });
  }

  compare(): void {
    if (!this.targetEnvironment || this.targetEnvironment === this.api.selectedEnvironmentKey()) return;
    this.api.compareDataTables(this.api.selectedEnvironmentKey(), this.targetEnvironment).subscribe({
      next: (result) => {
        this.comparison.set(result);
        this.loadPromotionPlan();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not compare Data Tables.'),
    });
  }

  loadPromotionPlan(): void {
    if (!this.targetEnvironment) return;
    this.api.getDataTablePromotionPlan(this.api.selectedEnvironmentKey(), this.targetEnvironment).subscribe({
      next: (plan) => { this.promotionPlan.set(plan); this.selectedTableIds.set(plan.changes.map((item) => item.id)); },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not prepare the promotion plan.'),
    });
  }

  toggleTable(id: string, event: Event): void {
    const checked = (event.target as HTMLInputElement).checked;
    this.selectedTableIds.update((ids) => checked ? [...ids, id] : ids.filter((value) => value !== id));
  }

  stagePromotion(): void {
    const selected = this.selectedTableIds();
    if (!this.targetEnvironment || selected.length === 0) return;
    this.staging.set(true); this.error.set(null);
    this.api.stageDataTablePromotion(this.api.selectedEnvironmentKey(), this.targetEnvironment, selected).subscribe({
      next: (result) => { this.message.set(result.skippedCommit ? 'No target schema changes were needed.' : `Staged ${result.stagedTablesCount} schema snapshot(s) in ${result.targetEnvironmentKey}.`); this.staging.set(false); this.loadPromotionPlan(); },
      error: (response) => { this.error.set(response?.error?.error ?? 'Could not stage the promotion.'); this.staging.set(false); },
    });
  }

  previous(): void { if (this.page() > 1) { this.page.update(value => value - 1); this.loadTables(); } }
  next(): void { if (this.page() * 25 < this.totalCount()) { this.page.update(value => value + 1); this.loadTables(); } }
}
