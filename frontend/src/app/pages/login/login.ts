import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../services/auth';

@Component({ selector: 'app-login', imports: [FormsModule], templateUrl: './login.html' })
export class LoginComponent {
  userName = ''; password = ''; error = signal<string | null>(null); busy = signal(false);
  constructor(private readonly auth: AuthService, private readonly router: Router) {}
  submit(): void { this.busy.set(true); this.error.set(null); this.auth.login(this.userName, this.password).subscribe({ next: () => this.router.navigateByUrl('/'), error: () => { this.error.set('Sign-in failed. Check your user name and password.'); this.busy.set(false); } }); }
}
