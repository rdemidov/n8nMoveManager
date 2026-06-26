import { DatePipe } from '@angular/common';
import { Component, effect, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiService, EnvironmentItem, GitCommit } from '../../services/api';

@Component({
  selector: 'app-git-history',
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './git-history.html',
})
export class GitHistoryComponent {
  commits = signal<GitCommit[]>([]);
  error = signal<string | null>(null);
  loading = signal(false);

  constructor(
    readonly api: ApiService,
    private readonly router: Router,
  ) {
    effect(() => {
      const environmentKey = this.api.selectedEnvironmentKey();
      this.load(environmentKey);
    });
  }

  selectEnvironment(environmentKey: string): void {
    this.api.selectEnvironment(environmentKey);
    this.router.navigate(['/history'], {
      queryParams: { environment: environmentKey },
      queryParamsHandling: 'merge',
    });
  }

  isSelected(environment: EnvironmentItem): boolean {
    return environment.key === this.api.selectedEnvironmentKey();
  }

  hasPreviousCommit(index: number): boolean {
    return index < this.commits().length - 1;
  }

  private load(environmentKey: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getCommits(25, environmentKey).subscribe({
      next: (commits) => {
        this.commits.set(commits);
        this.loading.set(false);
      },
      error: () => {
        this.commits.set([]);
        this.error.set(`Could not load commits for ${environmentKey}.`);
        this.loading.set(false);
      },
    });
  }
}
