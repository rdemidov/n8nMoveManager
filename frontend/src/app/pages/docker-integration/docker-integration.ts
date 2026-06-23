import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService, DockerExportResult, DockerStatus, EnvironmentDockerConfigRequest } from '../../services/api';

@Component({
  selector: 'app-docker-integration',
  imports: [FormsModule, RouterLink],
  templateUrl: './docker-integration.html',
})
export class DockerIntegrationComponent implements OnInit {
  status = signal<DockerStatus | null>(null);
  testResult = signal<DockerExportResult | null>(null);
  exportResult = signal<DockerExportResult | null>(null);
  loadingStatus = signal(false);
  saving = signal(false);
  testing = signal(false);
  exporting = signal(false);
  error = signal<string | null>(null);
  message = signal<string | null>(null);

  form: EnvironmentDockerConfigRequest = {
    dockerEnabled: false,
    containerName: 'n8n',
    n8nCliCommand: 'n8n',
    tempContainerPath: '/tmp/n8nmm-workflows.json',
    tempHostImportPath: '',
  };

  constructor(readonly api: ApiService) {}

  ngOnInit(): void {
    this.loadStatus();
    this.loadConfig();
  }

  loadStatus(): void {
    this.loadingStatus.set(true);
    this.api.getDockerStatus().subscribe({
      next: (status) => {
        this.status.set(status);
        this.loadingStatus.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Docker status could not be loaded.');
        this.loadingStatus.set(false);
      },
    });
  }

  loadConfig(): void {
    this.error.set(null);
    this.message.set(null);
    this.testResult.set(null);
    this.exportResult.set(null);
    this.api.getEnvironmentDockerConfig(this.api.selectedEnvironmentKey()).subscribe({
      next: (config) => {
        this.form = {
          dockerEnabled: config.dockerEnabled,
          containerName: config.containerName || 'n8n',
          n8nCliCommand: config.n8nCliCommand || 'n8n',
          tempContainerPath: config.tempContainerPath || '/tmp/n8nmm-workflows.json',
          tempHostImportPath: config.tempHostImportPath ?? '',
        };
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Docker configuration could not be loaded.'),
    });
  }

  save(): void {
    this.saving.set(true);
    this.error.set(null);
    this.message.set(null);
    this.testResult.set(null);
    this.exportResult.set(null);
    this.api.saveEnvironmentDockerConfig(this.api.selectedEnvironmentKey(), this.normalizedRequest()).subscribe({
      next: () => {
        this.message.set('Docker settings saved.');
        this.saving.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Docker settings could not be saved.');
        this.saving.set(false);
      },
    });
  }

  test(): void {
    this.testing.set(true);
    this.error.set(null);
    this.message.set(null);
    this.testResult.set(null);
    this.exportResult.set(null);
    this.api.testEnvironmentDocker(this.api.selectedEnvironmentKey()).subscribe({
      next: (result) => {
        this.testResult.set(result);
        this.message.set('Docker and n8n CLI connection tested.');
        this.testing.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Docker test failed.');
        this.testing.set(false);
      },
    });
  }

  exportNow(): void {
    this.exporting.set(true);
    this.error.set(null);
    this.message.set(null);
    this.exportResult.set(null);
    this.api.exportWorkflowsFromDocker(this.api.selectedEnvironmentKey()).subscribe({
      next: (result) => {
        this.exportResult.set(result);
        this.message.set(result.skippedCommit ? 'No changes detected.' : 'Docker workflow snapshot imported.');
        this.exporting.set(false);
      },
      error: (response) => {
        this.error.set(response?.error?.error ?? 'Docker export failed.');
        this.exporting.set(false);
      },
    });
  }

  private normalizedRequest(): EnvironmentDockerConfigRequest {
    return {
      dockerEnabled: this.form.dockerEnabled,
      containerName: this.form.containerName?.trim() || null,
      n8nCliCommand: this.form.n8nCliCommand?.trim() || 'n8n',
      tempContainerPath: this.form.tempContainerPath?.trim() || '/tmp/n8nmm-workflows.json',
      tempHostImportPath: this.form.tempHostImportPath?.trim() || null,
    };
  }
}
