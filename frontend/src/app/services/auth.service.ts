import { Injectable } from '@angular/core';
import { BehaviorSubject } from 'rxjs';
import { PublicClientApplication, Configuration, RedirectRequest, AuthenticationResult, AccountInfo, SilentRequest } from '@azure/msal-browser';
import { environment } from '../../environments/environment';

export interface AuthState {
  enabled: boolean;
  loggedIn: boolean;
  account?: AccountInfo | null;
  error?: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private pca?: PublicClientApplication;
  private account?: AccountInfo | null;
  state$ = new BehaviorSubject<AuthState>({ enabled: !!environment.aad?.enabled, loggedIn: false });

  constructor() {
    if (environment.aad?.enabled) {
      this.init();
    }
  }

  init() {
    if (this.pca) { return; }
    const aad = environment.aad!;
    const redirectUri = aad.redirectUri || (window.location.origin + '/');
    const config: Configuration = {
      auth: {
        clientId: aad.clientId,
        authority: `https://login.microsoftonline.com/${aad.tenantId}`,
        redirectUri,
        postLogoutRedirectUri: redirectUri,
      },
      cache: { cacheLocation: 'localStorage', storeAuthStateInCookie: false }
    };
    this.pca = new PublicClientApplication(config);

    // Handle redirect if present
    this.pca.handleRedirectPromise().then((res) => {
      if (res) {
        this.account = res.account;
        this.state$.next({ enabled: true, loggedIn: true, account: this.account });
      } else {
        const active = this.pca!.getAllAccounts()[0] || null;
        this.account = active;
        this.state$.next({ enabled: true, loggedIn: !!active, account: active });
      }
    }).catch(err => {
      this.state$.next({ enabled: true, loggedIn: false, error: (err as Error).message });
    });
  }

  login() {
    if (!this.pca) { this.init(); }
    const aad = environment.aad!;
    const request: RedirectRequest = {
      scopes: aad.scopes || ['User.Read']
    };
    return this.pca!.loginRedirect(request);
  }

  logout() {
    if (!this.pca) { return; }
    return this.pca.logoutRedirect();
  }

  isEnabled() { return !!environment.aad?.enabled; }
  isLoggedIn() { return !!this.account; }
  getAccount() { return this.account; }

  async getToken(scopes?: string): Promise<string | null> {
    if (!this.pca || !this.account) return null;
    const aad = environment.aad!;
    const req: SilentRequest = {
      account: this.account,
      scopes: scopes ? [scopes] : (aad.scopes || ['User.Read'])
    };
    try {
      const res: AuthenticationResult = await this.pca.acquireTokenSilent(req);
      return res.accessToken;
    } catch {
      return null;
    }
  }
}
