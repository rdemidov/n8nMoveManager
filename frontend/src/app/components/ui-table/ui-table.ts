import { Component } from '@angular/core';

@Component({
  selector: 'app-ui-table',
  template: '<ng-content />',
  host: { class: 'table-panel' },
})
export class UiTableComponent {}
