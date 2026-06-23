import { Component, effect, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService, CredentialReference, EnvironmentCredential } from '../../services/api';

@Component({
  selector: 'app-credential-inventory',
  imports: [DatePipe],
  templateUrl: './credential-inventory.html',
})
export class CredentialInventoryComponent {
  credentials = signal<EnvironmentCredential[]>([]);
  references = signal<CredentialReference[]>([]);
  error = signal<string | null>(null);
  loading = signal(false);

  constructor(readonly api: ApiService) {
    effect(() => {
      const environmentKey = this.api.selectedEnvironmentKey();
      this.load(environmentKey);
    });
  }

  load(environmentKey = this.api.selectedEnvironmentKey()): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getEnvironmentCredentials(environmentKey).subscribe({
      next: (credentials) => {
        this.credentials.set(credentials);
        this.loading.set(false);
      },
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load credentials.'),
    });
    this.api.getCredentialReferences(environmentKey).subscribe({
      next: (references) => this.references.set(references),
      error: (response) => this.error.set(response?.error?.error ?? 'Could not load credential references.'),
    });
  }
}
