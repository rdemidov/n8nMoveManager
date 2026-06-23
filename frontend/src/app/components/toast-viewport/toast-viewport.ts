import { Component, inject } from '@angular/core';
import { ToastService } from '../../services/toast';

@Component({
  selector: 'app-toast-viewport',
  templateUrl: './toast-viewport.html',
})
export class ToastViewportComponent {
  readonly toast = inject(ToastService);
}
