import { DatePipe } from '@angular/common';
import { Component, computed, effect, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiService, GitCommit, WorkflowListItem } from '../../services/api';

@Component({
  selector: 'app-dashboard',
  imports: [DatePipe, RouterLink],
  templateUrl: './dashboard.html',
})
export class DashboardComponent {
  workflows = signal<WorkflowListItem[]>([]);
  commits = signal<GitCommit[]>([]);
  private readonly workflowsError = signal<string | null>(null);
  private readonly commitsError = signal<string | null>(null);
  readonly error = computed(() => this.workflowsError() ?? this.commitsError());

  constructor(readonly api: ApiService) {
    effect(() => {
      const environmentKey = this.api.selectedEnvironmentKey();
      this.load(environmentKey);
    });
  }

  private load(environmentKey: string): void {
    this.workflowsError.set(null);
    this.commitsError.set(null);

    this.api.getWorkflows(environmentKey).subscribe({
      next: (workflows) => {
        this.workflows.set(workflows);
        this.workflowsError.set(null);
      },
      error: () => this.workflowsError.set('Could not load workflows. Is the backend running on http://localhost:5107?'),
    });

    this.api.getCommits(5, environmentKey).subscribe({
      next: (commits) => {
        this.commits.set(commits);
        this.commitsError.set(null);
      },
      error: () => this.commitsError.set('Could not load Git history. Is the backend running on http://localhost:5107?'),
    });
  }
}
