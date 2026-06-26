import { Component, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ApiService, EnvironmentItem } from './services/api';
import { AuthService } from './services/auth';
import { ConfirmationDialogComponent } from './components/confirmation-dialog/confirmation-dialog';
import { ToastViewportComponent } from './components/toast-viewport/toast-viewport';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ConfirmationDialogComponent, ToastViewportComponent],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App implements OnInit {
  environments = signal<EnvironmentItem[]>([]);

  constructor(
    readonly api: ApiService,
    private readonly route: ActivatedRoute,
    readonly router: Router,
    readonly auth: AuthService,
  ) {}

  ngOnInit(): void {
    this.route.queryParamMap.subscribe((params) => {
      const environmentKey = params.get('environment');
      if (environmentKey && environmentKey !== this.api.selectedEnvironmentKey()) {
        this.api.selectEnvironment(environmentKey);
      }
    });

    this.api.getEnvironments().subscribe({
      next: (environments) => {
        this.environments.set(environments);
        this.api.setEnvironments(environments);
        this.ensureEnvironmentInUrl();
      },
      error: () => {
        const fallback = [{
          id: 'local',
          name: 'Local',
          key: 'local',
          description: null,
          gitBranch: 'env/local',
          gitBranchName: 'env/local',
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          isDefault: true,
        }];
        this.environments.set(fallback);
        this.api.setEnvironments(fallback);
        this.ensureEnvironmentInUrl();
      },
    });
  }

  logout(): void { this.auth.logout(); this.router.navigateByUrl('/login'); }

  isLoginRoute(): boolean {
    return this.router.url.startsWith('/login');
  }

  selectEnvironment(event: Event): void {
    const select = event.target as HTMLSelectElement;
    this.api.selectEnvironment(select.value);
    this.navigateWithEnvironment(select.value, false);
  }

  private ensureEnvironmentInUrl(): void {
    if (window.location.pathname && window.location.pathname !== '/') {
      return;
    }

    const queryEnvironment = this.route.snapshot.queryParamMap.get('environment');
    if (queryEnvironment === this.api.selectedEnvironmentKey()) {
      return;
    }

    this.navigateWithEnvironment(this.api.selectedEnvironmentKey(), true);
  }

  private navigateWithEnvironment(environmentKey: string, replaceUrl: boolean): void {
    const tree = this.router.parseUrl(this.router.url);
    tree.queryParams = { ...tree.queryParams, environment: environmentKey };
    this.router.navigateByUrl(tree, { replaceUrl });
  }
}
