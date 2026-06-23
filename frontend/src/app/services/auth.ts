import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { tap } from 'rxjs';

export interface LoginResult { accessToken: string; expiresAt: string; userName: string; role: string; }

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly key = 'n8nmm.access-token';
  readonly userName = signal(localStorage.getItem('n8nmm.user-name'));
  readonly role = signal(localStorage.getItem('n8nmm.user-role'));
  constructor(private readonly http: HttpClient) {}
  token(): string | null { return localStorage.getItem(this.key); }
  login(userName: string, password: string) {
    return this.http.post<LoginResult>('/api/auth/login', { userName, password }).pipe(tap(result => {
      localStorage.setItem(this.key, result.accessToken); localStorage.setItem('n8nmm.user-name', result.userName); localStorage.setItem('n8nmm.user-role', result.role);
      this.userName.set(result.userName); this.role.set(result.role);
    }));
  }
  logout(): void { localStorage.removeItem(this.key); localStorage.removeItem('n8nmm.user-name'); localStorage.removeItem('n8nmm.user-role'); this.userName.set(null); this.role.set(null); }
}
