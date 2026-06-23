import { Routes } from '@angular/router';
export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login/login').then((m) => m.LoginComponent) },
  { path: '', loadComponent: () => import('./pages/dashboard/dashboard').then((m) => m.DashboardComponent) },
  { path: 'environments', loadComponent: () => import('./pages/environments/environments').then((m) => m.EnvironmentsComponent) },
  { path: 'upload', loadComponent: () => import('./pages/upload-workflows/upload-workflows').then((m) => m.UploadWorkflowsComponent) },
  { path: 'docker', loadComponent: () => import('./pages/docker-integration/docker-integration').then((m) => m.DockerIntegrationComponent) },
  { path: 'scheduled-jobs', loadComponent: () => import('./pages/scheduled-jobs/scheduled-jobs').then((m) => m.ScheduledJobsComponent) },
  { path: 'workflows', loadComponent: () => import('./pages/workflows-list/workflows-list').then((m) => m.WorkflowsListComponent) },
  { path: 'data-tables', loadComponent: () => import('./pages/data-tables/data-tables').then((m) => m.DataTablesComponent) },
  { path: 'credentials', loadComponent: () => import('./pages/credential-inventory/credential-inventory').then((m) => m.CredentialInventoryComponent) },
  { path: 'mappings', loadComponent: () => import('./pages/credential-mapping/credential-mapping').then((m) => m.CredentialMappingComponent) },
  { path: 'promotion', loadComponent: () => import('./pages/promotion/promotion').then((m) => m.PromotionComponent) },
  { path: 'manual-merge', loadComponent: () => import('./pages/manual-merge/manual-merge').then((m) => m.ManualMergeComponent) },
  { path: 'manual-merge/:sessionId', loadComponent: () => import('./pages/manual-merge/manual-merge').then((m) => m.ManualMergeComponent) },
  { path: 'settings/ai', loadComponent: () => import('./pages/ai-settings/ai-settings').then((m) => m.AiSettingsComponent) },
  { path: 'history', loadComponent: () => import('./pages/git-history/git-history').then((m) => m.GitHistoryComponent) },
  { path: 'commits/:sha/files', loadComponent: () => import('./pages/commit-file-browser/commit-file-browser').then((m) => m.CommitFileBrowserComponent) },
  { path: 'restore/preview/:sha', loadComponent: () => import('./pages/restore-preview/restore-preview').then((m) => m.RestorePreviewComponent) },
  { path: 'backups', loadComponent: () => import('./pages/backups/backups').then((m) => m.BackupsComponent) },
  { path: 'diff/latest', loadComponent: () => import('./pages/commit-diff/commit-diff').then((m) => m.CommitDiffComponent) },
  { path: 'diff/:sha', loadComponent: () => import('./pages/commit-diff/commit-diff').then((m) => m.CommitDiffComponent) },
  { path: '**', redirectTo: '' }
];
