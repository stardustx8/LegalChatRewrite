import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer } from '@angular/platform-browser';
import { Router } from '@angular/router';
import { ApiService, AskResponse } from '../../services/api.service';
import { marked } from 'marked';
import DOMPurify from 'dompurify';
import { AuthService } from '../../services/auth.service';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-ask',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
  <div class="container">
    <div class="main-card">
      <h1>Knife Legislation Bot</h1>
      
      <ng-container *ngIf="!aadEnabled || loggedIn; else signInBlock">
        <textarea 
          [(ngModel)]="question" 
          placeholder="Ask a question about knife legislation..." 
          aria-label="Question"
          id="question">
        </textarea>
        <button 
          (click)="onAsk()" 
          [disabled]="loading" 
          class="btn-primary"
          id="askButton">
          Ask!
        </button>
      </ng-container>
      
      <ng-template #signInBlock>
        <div class="signin-card">
          <p>Please sign in with your corporate account to use this app.</p>
          <button (click)="login()" class="btn-primary">Sign in</button>
        </div>
      </ng-template>
      
      <div id="aria-live-status" class="sr-only" aria-live="polite"></div>
      
      <!-- Query Progress -->
      <div class="query-progress" [class.hidden]="!loading && !errorMessage">
        <div class="query-progress-bar" role="progressbar" 
             [attr.aria-valuemin]="0" 
             [attr.aria-valuemax]="100" 
             [attr.aria-valuenow]="progress"
             aria-label="Query progress" 
             aria-describedby="query-status">
          <div class="query-progress-fill" [style.width.%]="progress"></div>
        </div>
        <div id="query-status" class="query-status-text" aria-live="polite">{{statusText}}</div>
      </div>

      <!-- Error Message -->
      <div *ngIf="errorMessage" class="status-message error">{{ errorMessage }}</div>
      
      <!-- Response Area -->
      <div class="response-area" [class.hidden]="!response">
        <!-- Country Detection (inline format) -->
        <div class="country-detection-inline" *ngIf="response">
          Detected countries: 
          <span *ngFor="let code of response.country_detection.iso_codes; let last = last">
            {{code}} <span [class.check-success]="response.country_detection.available.includes(code)"
                          [class.check-fail]="!response.country_detection.available.includes(code)">
              {{response.country_detection.available.includes(code) ? '✅' : '❌'}}
            </span><span *ngIf="!last">, </span>
          </span>
        </div>
        
        <!-- Answer Section -->
        <div class="answer-header">
          <h2>Answer</h2>
          <div class="answer-actions">
            <button (click)="copyAnswer()" class="btn-secondary" type="button" aria-label="Copy answer">Copy</button>
            <button (click)="printAnswer()" class="btn-secondary" type="button" aria-label="Print answer">Print</button>
          </div>
        </div>
        
        <div id="refined-answer-container" class="response-content" aria-live="polite" [innerHTML]="refinedAnswerHtml"></div>
      </div>
    </div>
  </div>
  
  <!-- Admin Link -->
  <a href="#" class="admin-link" (click)="openAdminUpload($event)">Admin</a>
  `,
  styles: [`
    /* Component-specific styles - most styling comes from global styles.scss */
    .signin-card {
      text-align: center;
      padding: 2rem;
      background: var(--color-grey-100);
      border-radius: var(--radius-md);
      margin: 2rem 0;
    }
    
    .country-detection-inline {
      text-align: center;
      padding: 0.25rem 0 2rem 0;
      color: var(--accent);
      font-weight: 500;
      margin: 0;
    }
    
    .check-success {
      color: #15803d;
    }
    
    .check-fail {
      color: var(--accent);
    }
  `]
})
export class AskComponent {
  private api = inject(ApiService);
  private sanitizer = inject(DomSanitizer);
  auth = inject(AuthService);
  private router = inject(Router);

  question = '';
  loading = false;
  progress = 0;
  statusText = '';
  errorMessage: string | null = null;
  aadEnabled = !!environment.aad?.enabled;
  loggedIn = false;

  response?: AskResponse;
  countryHeaderHtml: any;
  refinedAnswerHtml: any;

  setProgress(val: number, text: string) {
    this.progress = val;
    this.statusText = text;
  }

  onAsk() {
    if (!this.question?.trim()) return;
    if (this.aadEnabled && !this.loggedIn) { this.login(); return; }
    this.loading = true;
    this.errorMessage = null;
    this.setProgress(10, 'Identifying countries in user query…');
    this.api.ask(this.question).subscribe({
      next: (res: AskResponse) => {
        // Immediately show we got a response and start processing
        this.setProgress(30, 'Retrieving documents...');
        
        setTimeout(() => {
          this.setProgress(50, 'Drafting answer...');
        }, 400);
        
        setTimeout(() => {
          this.setProgress(75, 'Finalizing answer...');
        }, 800);
        
        setTimeout(() => {
          this.setProgress(90, 'Almost done...');
          // Render country header and refined answer as safe HTML
          this.countryHeaderHtml = this.sanitizer.bypassSecurityTrustHtml(this.renderMarkdown(res.country_header));
          this.refinedAnswerHtml = this.sanitizer.bypassSecurityTrustHtml(this.renderMarkdown(res.refined_answer));
          // Set response to show the country detection
          this.response = res;
        }, 1200);
        
        setTimeout(() => {
          this.loading = false;
        }, 1500);
      },
      error: (err: any) => {
        console.error(err);
        this.loading = false;
        this.setProgress(0, 'Error occurred.');
        // surface a friendly error message
        let msg = 'Request failed. Please try again.';
        if (err?.status === 0) msg = 'Network error contacting the API.';
        else if (typeof err?.error === 'string') msg = err.error;
        else if (err?.error?.message) msg = err.error.message;
        else if (err?.message) msg = err.message;
        this.errorMessage = msg;
      }
    });
  }

  login() {
    this.auth.login();
  }

  constructor() {
    if (this.aadEnabled) {
      this.auth.state$.subscribe((st: any) => this.loggedIn = !!st.account);
    }
  }

  // minimal markdown rendering using marked if present; otherwise basic replacements
  renderMarkdown(md: string): string {
    try {
      // Use marked to parse markdown
      const raw = marked.parse(md) as string;
      
      // Add custom styling for better list formatting
      let formatted = raw;
      
      // Add indentation classes to nested lists
      formatted = formatted.replace(/<ul>/g, '<ul class="formatted-list">');
      formatted = formatted.replace(/<ol>/g, '<ol class="formatted-list">');
      
      // Clean with DOMPurify
      return DOMPurify.sanitize(formatted, { USE_PROFILES: { html: true } } as any) as unknown as string;
    } catch {
      return md.replace(/\n/g, '<br/>');
    }
  }

  copyAnswer() {
    if (!this.response?.refined_answer) return;
    const text = this.response.refined_answer;
    navigator.clipboard.writeText(text).then(() => {
      // Optional: Show success feedback
      console.log('Answer copied to clipboard');
    });
  }

  printAnswer() {
    window.print();
  }

  openAdminUpload(event: Event) {
    event.preventDefault();
    this.router.navigate(['/admin-upload']);
  }
}
