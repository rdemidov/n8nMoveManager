import { DatePipe } from '@angular/common';
import { Component, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, BackupItem } from '../../services/api';

@Component({
  selector: 'app-backups',
  imports: [DatePipe, RouterLink],
  templateUrl: './backups.html',
})
export class BackupsComponent {
  readonly backups = signal<BackupItem[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  constructor(readonly api: ApiService) {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getBackups().subscribe({
      next: (backups) => {
        this.backups.set(backups);
        this.loading.set(false);
      },
      error: () => {
        this.backups.set([]);
        this.error.set('Could not load backups.');
        this.loading.set(false);
      },
    });
  }

  deleteBackup(backup: BackupItem): void {
    this.api.deleteBackup(backup.id).subscribe({
      next: () => this.load(),
      error: () => this.error.set(`Could not delete ${backup.fileName}.`),
    });
  }
}
