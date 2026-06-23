import { Injectable, signal } from '@angular/core';

export type ToastKind = 'success' | 'error' | 'info';

export interface ToastOptions {
  actionLabel?: string;
  action?: () => void;
  duration?: number;
}

export interface ToastItem extends Required<Pick<ToastOptions, 'duration'>> {
  id: number;
  kind: ToastKind;
  message: string;
  actionLabel?: string;
  action?: () => void;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly items = signal<ToastItem[]>([]);
  private nextId = 0;

  success(message: string, options?: ToastOptions): void { this.show('success', message, options); }
  error(message: string, options?: ToastOptions): void { this.show('error', message, { duration: 7000, ...options }); }
  info(message: string, options?: ToastOptions): void { this.show('info', message, options); }

  dismiss(id: number): void {
    this.items.update((items) => items.filter((item) => item.id !== id));
  }

  runAction(item: ToastItem): void {
    item.action?.();
    this.dismiss(item.id);
  }

  private show(kind: ToastKind, message: string, options: ToastOptions = {}): void {
    const item: ToastItem = {
      id: ++this.nextId,
      kind,
      message,
      duration: options.duration ?? 4500,
      actionLabel: options.actionLabel,
      action: options.action,
    };
    this.items.update((items) => [...items, item]);
    if (item.duration > 0) {
      window.setTimeout(() => this.dismiss(item.id), item.duration);
    }
  }
}
