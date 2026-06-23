import { DatePipe } from '@angular/common';
import { Component, effect, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService, BackupCreateResult, CommitFileContent, CommitFileItem, GitCommit, RestoreWorkflowResult } from '../../services/api';

@Component({
  selector: 'app-commit-file-browser',
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './commit-file-browser.html',
})
export class CommitFileBrowserComponent {
  readonly commitSha = signal('');
  readonly commits = signal<GitCommit[]>([]);
  readonly files = signal<CommitFileItem[]>([]);
  readonly selectedFile = signal<CommitFileContent | null>(null);
  readonly restoreResult = signal<RestoreWorkflowResult | null>(null);
  readonly backupResult = signal<BackupCreateResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly loading = signal(false);
  readonly restoring = signal(false);
  readonly creatingBackup = signal(false);
  includeMetadata = true;
  includeDatabaseSnapshot = false;
  confirmRestoreWorkflow = false;

  constructor(
    readonly api: ApiService,
    private readonly route: ActivatedRoute,
  ) {
    this.route.paramMap.subscribe((params) => {
      this.commitSha.set(params.get('sha') ?? '');
      this.load();
    });

    effect(() => {
      this.api.selectedEnvironmentKey();
      if (this.commitSha()) {
        this.load();
      }
    });
  }

  selectCommit(sha: string): void {
    this.commitSha.set(sha);
    this.load();
  }

  load(): void {
    const sha = this.commitSha();
    if (!sha) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);
    this.selectedFile.set(null);
    this.restoreResult.set(null);
    this.backupResult.set(null);
    this.api.getCommits(50).subscribe({
      next: (commits) => this.commits.set(commits),
      error: () => this.commits.set([]),
    });
    this.api.getCommitFiles(sha).subscribe({
      next: (files) => {
        this.files.set(files);
        this.loading.set(false);
        if (files.length > 0) {
          this.openFile(files[0].filePath);
        }
      },
      error: () => {
        this.files.set([]);
        this.error.set('Could not load workflow files for this commit.');
        this.loading.set(false);
      },
    });
  }

  openFile(filePath: string): void {
    this.error.set(null);
    this.restoreResult.set(null);
    this.api.getCommitFileContent(this.commitSha(), filePath).subscribe({
      next: (file) => this.selectedFile.set(file),
      error: () => this.error.set(`Could not load ${filePath}.`),
    });
  }

  downloadUrl(filePath: string): string {
    return this.api.getCommitFileDownloadUrl(this.commitSha(), filePath);
  }

  restoreSelectedWorkflow(): void {
    const file = this.selectedFile();
    if (!file || !this.confirmRestoreWorkflow) {
      return;
    }

    this.restoring.set(true);
    this.error.set(null);
    this.api.restoreWorkflow(this.commitSha(), file.filePath).subscribe({
      next: (result) => {
        this.restoreResult.set(result);
        this.restoring.set(false);
        this.confirmRestoreWorkflow = false;
      },
      error: () => {
        this.error.set(`Could not restore ${file.filePath}.`);
        this.restoring.set(false);
      },
    });
  }

  createBackup(): void {
    this.creatingBackup.set(true);
    this.error.set(null);
    this.api.createBackupFromCommit(this.commitSha(), this.includeMetadata, this.includeDatabaseSnapshot).subscribe({
      next: (result) => {
        this.backupResult.set(result);
        this.creatingBackup.set(false);
      },
      error: () => {
        this.error.set('Could not create backup for this commit.');
        this.creatingBackup.set(false);
      },
    });
  }
}
