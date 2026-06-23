import { Component, input } from '@angular/core';

@Component({
  selector: 'app-ui-alert',
  template: '<ng-content />',
  host: {
    class: 'alert',
    '[class.danger]': "tone() === 'danger'",
    '[class.loading-alert]': "tone() === 'loading'",
    '[attr.role]': "tone() === 'danger' ? 'alert' : 'status'",
  },
})
export class UiAlertComponent {
  readonly tone = input<'default' | 'danger' | 'loading'>('default');
}
