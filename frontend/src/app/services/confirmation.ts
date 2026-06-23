import { Injectable, signal } from '@angular/core';

export interface ConfirmationOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  danger?: boolean;
}

interface ConfirmationRequest extends Required<ConfirmationOptions> {
  resolve: (confirmed: boolean) => void;
  returnFocusTo: HTMLElement | null;
}

@Injectable({ providedIn: 'root' })
export class ConfirmationService {
  readonly request = signal<ConfirmationRequest | null>(null);

  confirm(options: ConfirmationOptions): Promise<boolean> {
    const existing = this.request();
    if (existing) {
      existing.resolve(false);
    }

    return new Promise<boolean>((resolve) => {
      this.request.set({
        confirmLabel: 'Confirm',
        danger: false,
        ...options,
        resolve,
        returnFocusTo: document.activeElement instanceof HTMLElement ? document.activeElement : null,
      });
    });
  }

  close(confirmed: boolean): void {
    const request = this.request();
    if (!request) return;

    this.request.set(null);
    request.resolve(confirmed);
    queueMicrotask(() => request.returnFocusTo?.focus());
  }
}
