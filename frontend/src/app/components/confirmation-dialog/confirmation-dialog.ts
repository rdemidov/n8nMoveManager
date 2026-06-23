import { Component, HostListener, effect, inject } from '@angular/core';
import { ConfirmationService } from '../../services/confirmation';

@Component({
  selector: 'app-confirmation-dialog',
  templateUrl: './confirmation-dialog.html',
})
export class ConfirmationDialogComponent {
  readonly confirmation = inject(ConfirmationService);

  constructor() {
    effect(() => {
      if (this.confirmation.request()) {
        queueMicrotask(() => document.getElementById('confirmation-cancel')?.focus());
      }
    });
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    if (this.confirmation.request()) this.confirmation.close(false);
  }
}
