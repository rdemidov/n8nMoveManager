import { DatePipe } from '@angular/common';
import { Component, effect, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AiDataTableMappingResult, ApiService, DataTableComparison, DataTableItem, DataTableMapping, DataTablePromotionPlan, N8nApiConfigRequest } from '../../services/api';
import { ConfirmationService } from '../../services/confirmation';

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
  deploying = signal(false);
  error = signal<string | null>(null);
  message = signal<string | null>(null);
  search = '';
  targetEnvironment = '';
  selectedTableIds = signal<string[]>([]);
  mappingSourceTables = signal<DataTableItem[]>([]);
  mappingTargetTables = signal<DataTableItem[]>([]);
  mappings = signal<DataTableMapping[]>([]);
  aiMappingLoading = signal(false);
  aiMappingResult = signal<AiDataTableMappingResult | null>(null);
  sourceTableId = '';
  targetTableId = '';
  apiKey = '';
  form: N8nApiConfigRequest = { enabled: false, baseUrl: '', dataTablesPath: '/api/v1/data-tables', dataTablesWritePathTemplate: '', workflowApiPath: '/api/v1/workflows', apiKey: '' };

  constructor(readonly api: ApiService, private readonly confirmation: ConfirmationService) {
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
        this.loadMappingOptions();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not compare Data Tables.'),
    });
  }

  loadMappingOptions(): void {
    if (!this.targetEnvironment) return;
    this.api.getDataTables(this.api.selectedEnvironmentKey(), 1, 100).subscribe({
      next: (result) => this.mappingSourceTables.set(result.items),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load source Data Tables.'),
    });
    this.api.getDataTables(this.targetEnvironment, 1, 100).subscribe({
      next: (result) => this.mappingTargetTables.set(result.items),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load target Data Tables.'),
    });
    this.loadMappings();
  }

  loadMappings(): void {
    if (!this.targetEnvironment) return;
    this.api.getDataTableMappings(this.api.selectedEnvironmentKey(), this.targetEnvironment).subscribe({
      next: (mappings) => this.mappings.set(mappings),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load Data Table mappings.'),
    });
  }

  chooseSourceTable(sourceTableId: string): void {
    this.sourceTableId = sourceTableId;
    const source = this.mappingSourceTables().find((table) => table.id === sourceTableId);
    const sameName = source && this.mappingTargetTables().find((table) => table.name.toLocaleLowerCase() === source.name.toLocaleLowerCase());
    if (sameName) this.targetTableId = sameName.id;
  }

  saveMapping(): void {
    if (!this.targetEnvironment || !this.sourceTableId || !this.targetTableId) return;
    this.api.saveDataTableMapping({ sourceEnvironmentKey: this.api.selectedEnvironmentKey(), targetEnvironmentKey: this.targetEnvironment, sourceTableId: this.sourceTableId, targetTableId: this.targetTableId }).subscribe({
      next: () => { this.message.set('Data Table mapping saved.'); this.sourceTableId = ''; this.targetTableId = ''; this.loadMappings(); this.compare(); },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not save the Data Table mapping.'),
    });
  }

  async deleteMapping(mapping: DataTableMapping): Promise<void> {
    if (!await this.confirmation.confirm({ title: 'Remove Data Table mapping?', message: `${mapping.sourceTableName} will no longer be remapped to ${mapping.targetTableName} during workflow deployment.`, confirmLabel: 'Remove mapping', danger: true })) return;
    this.api.deleteDataTableMapping(mapping.id).subscribe({
      next: () => { this.message.set('Data Table mapping removed.'); this.loadMappings(); this.compare(); },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not remove the Data Table mapping.'),
    });
  }

  createMappingsWithAi(): void {
    if (!this.targetEnvironment || this.aiMappingLoading()) return;
    this.aiMappingLoading.set(true);
    this.aiMappingResult.set(null);
    this.error.set(null);
    this.api.createDataTableMappingsWithAi(this.api.selectedEnvironmentKey(), this.targetEnvironment).subscribe({
      next: (result) => {
        this.aiMappingResult.set(result);
        this.message.set(`AI applied ${result.appliedMappingsCount} of ${result.suggestedMappingsCount} suggested Data Table mapping(s).`);
        this.aiMappingLoading.set(false);
        this.loadMappings();
        this.compare();
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'AI could not create Data Table mappings.');
        this.aiMappingLoading.set(false);
      },
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

  async deploySelectedSchemas(): Promise<void> {
    const selected = this.selectedTableIds();
    if (!this.targetEnvironment || selected.length === 0 || this.deploying()) return;
    if (!await this.confirmation.confirm({
      title: 'Deploy selected Data Table schemas?',
      message: `This will update ${selected.length} schema(s) in ${this.targetEnvironment} through its n8n API. Table rows are not copied.`,
      confirmLabel: 'Deploy schemas',
      danger: true,
    })) return;

    this.deploying.set(true);
    this.error.set(null);
    this.api.deployDataTableSchemas({ sourceEnvironmentKey: this.api.selectedEnvironmentKey(), targetEnvironmentKey: this.targetEnvironment, tableIds: selected, confirmation: true }).subscribe({
      next: (result) => { this.message.set(`Deployed ${result.deployedCount} Data Table schema(s) to ${result.targetEnvironmentKey}.`); this.deploying.set(false); },
      error: (response) => { this.error.set(response?.error?.error ?? 'Could not deploy Data Table schemas.'); this.deploying.set(false); },
    });
  }

  previous(): void { if (this.page() > 1) { this.page.update(value => value - 1); this.loadTables(); } }
  next(): void { if (this.page() * 25 < this.totalCount()) { this.page.update(value => value + 1); this.loadTables(); } }
}
