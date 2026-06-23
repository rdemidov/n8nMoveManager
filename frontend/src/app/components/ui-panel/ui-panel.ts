import { Component } from '@angular/core';

@Component({
  selector: 'app-ui-panel',
  template: '<ng-content />',
  host: { class: 'panel' },
})
export class UiPanelComponent {}
