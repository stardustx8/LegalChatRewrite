import { Component } from '@angular/core';
import { RouterModule } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterModule],
  template: `
  <div class="container">
    <main class="app-main">
      <router-outlet></router-outlet>
    </main>
  </div>
  `,
  styles: [`
    .container{max-width:960px;margin:0 auto;padding:1rem}
    .app-main{padding:1rem;}
  `]
})
export class AppComponent {}
