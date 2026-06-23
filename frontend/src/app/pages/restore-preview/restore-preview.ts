import { DatePipe } from '@angular/common';
import { Component, effect, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService, RestoreEnvironmentResult, RestorePreview } from '../../services/api';

@Component({
  selector: 'app-restore-preview',
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './restore-preview.html',
})
export class RestorePreviewComponent {
  readonly commitSha = signal('');
  readonly preview = signal<RestorePreview | null>(null);
  readonly result = signal<RestoreEnvironmentResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly loading = signal(false);
  readonly applying = signal(false);
  confirmRestore = false;
  includeDeletedFiles = false;

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

  load(): void {
    if (!this.commitSha()) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);
    this.result.set(null);
    this.api.getRestorePreview(this.commitSha()).subscribe({
      next: (preview) => {
        this.preview.set(preview);
        this.loading.set(false);
      },
      error: () => {
        this.preview.set(null);
        this.error.set('Could not load restore preview.');
        this.loading.set(false);
      },
    });
  }

  applyRestore(): void {
    if (!this.confirmRestore || !this.preview()) {
      return;
    }

    this.applying.set(true);
    this.error.set(null);
    this.api.restoreEnvironment(this.commitSha(), this.includeDeletedFiles).subscribe({
      next: (result) => {
        this.result.set(result);
        this.applying.set(false);
        this.confirmRestore = false;
      },
      error: () => {
        this.error.set('Could not restore the environment.');
        this.applying.set(false);
      },
    });
  }
}
