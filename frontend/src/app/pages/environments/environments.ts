import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, EnvironmentItem, EnvironmentRequest } from '../../services/api';
import { ConfirmationService } from '../../services/confirmation';
import { ToastService } from '../../services/toast';
import { UiAlertComponent } from '../../components/ui-alert/ui-alert';
import { UiPanelComponent } from '../../components/ui-panel/ui-panel';
import { UiTableComponent } from '../../components/ui-table/ui-table';

@Component({
  selector: 'app-environments',
  imports: [FormsModule, UiAlertComponent, UiPanelComponent, UiTableComponent],
  templateUrl: './environments.html',
})
export class EnvironmentsComponent implements OnInit {
  environments = signal<EnvironmentItem[]>([]);
  editingKey = signal<string | null>(null);
  error = signal<string | null>(null);
  message = signal<string | null>(null);
  clearingKey = signal<string | null>(null);
  form: EnvironmentRequest = { name: '', key: '', description: '', gitBranchName: '' };

  constructor(private readonly api: ApiService, private readonly confirmation: ConfirmationService, private readonly toast: ToastService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.api.getEnvironments().subscribe({
      next: (environments) => this.environments.set(environments),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load environments.'),
    });
  }

  edit(environment: EnvironmentItem): void {
    this.editingKey.set(environment.key);
    this.form = {
      name: environment.name,
      key: environment.key,
      description: environment.description,
      gitBranchName: environment.gitBranchName,
    };
    this.error.set(null);
    this.message.set(null);
  }

  reset(): void {
    this.editingKey.set(null);
    this.form = { name: '', key: '', description: '', gitBranchName: '' };
  }

  save(): void {
    this.error.set(null);
    this.message.set(null);
    const key = this.editingKey();
    const request = { ...this.form };
    const call = key ? this.api.updateEnvironment(key, request) : this.api.createEnvironment(request);
    call.subscribe({
      next: (environment) => {
        const message = `${environment.name} saved.`;
        this.message.set(message);
        this.toast.success(message);
        this.reset();
        this.load();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Environment could not be saved.'),
    });
  }

  async remove(environment: EnvironmentItem, force: boolean): Promise<void> {
    const action = force ? 'Force delete' : 'Delete';
    if (!await this.confirmation.confirm({
      title: `${action} ${environment.name}?`,
      message: force ? 'This force-deletes the environment. Its associated workspace data may be removed.' : 'This deletes the environment and its associated workspace data.',
      confirmLabel: action,
      danger: true,
    })) return;

    this.error.set(null);
    this.message.set(null);
    this.api.deleteEnvironment(environment.key, force).subscribe({
      next: (result) => {
        this.message.set(result.message);
        this.toast.success(result.message);
        this.load();
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Environment could not be deleted.'),
    });
  }

  async clear(environment: EnvironmentItem): Promise<void> {
    const confirmed = await this.confirmation.confirm({
      title: `Clear ${environment.name}?`,
      message: 'This removes all workflows and credential metadata. The environment and Git branch remain, and changed files are committed.',
      confirmLabel: 'Clear environment',
      danger: true,
    });
    if (!confirmed) return;

    this.error.set(null);
    this.message.set(null);
    this.clearingKey.set(environment.key);
    this.api.clearEnvironment(environment.key).subscribe({
      next: (result) => {
        const commitText = result.commitSha ? ` Commit ${result.commitSha.slice(0, 10)} created.` : ' No Git changes detected.';
        const message = `${result.message} Removed ${result.removedWorkflowFilesCount} workflow file(s), ${result.removedWorkflowMetadataCount} workflow record(s), and ${result.removedCredentialReferencesCount} credential reference(s).${commitText}`;
        this.message.set(message);
        this.toast.success(message);
        this.clearingKey.set(null);
      },
      error: (response) => {
        const message = response?.error?.error ?? 'Environment could not be cleared.';
        this.error.set(message);
        this.toast.error(message, { actionLabel: 'Retry', action: () => this.clear(environment) });
        this.clearingKey.set(null);
      },
    });
  }
}
