import { Component } from '@angular/core';
import { RouterModule } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterModule],
  template: `
  <div class="container">
    <header class="app-header">
      <h1>Knife Legislation Bot</h1>
      <nav>
        <a routerLink="/ask" routerLinkActive="active" aria-label="Ask view">Ask</a>
        <a routerLink="/admin-upload" routerLinkActive="active" aria-label="Admin upload">Admin Upload</a>
      </nav>
    </header>
    <main class="app-main">
      <router-outlet></router-outlet>
    </main>
  </div>
  `,
  styles: [`
    .container{max-width:960px;margin:0 auto;padding:1rem}
    .app-header{display:flex;align-items:center;justify-content:space-between;gap:1rem;margin:1rem 0}
    .app-header h1{margin:0}
    nav a{margin-right:1rem;text-decoration:none}
    nav a.active{font-weight:bold}
    .app-main{padding:1rem;}
  `]
})
export class AppComponent {}
