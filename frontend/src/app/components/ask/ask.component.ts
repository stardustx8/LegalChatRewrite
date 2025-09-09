import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer } from '@angular/platform-browser';
import { ApiService, AskResponse } from '../../services/api.service';
import { marked } from 'marked';
import DOMPurify from 'dompurify';

@Component({
  selector: 'app-ask',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
  <section aria-live="polite" class="query-progress">
    <div class="query-progress-bar"><div class="query-progress-fill" [style.width.%]="progress"></div></div>
    <p class="query-status-text">{{statusText}}</p>
  </section>

  <textarea [(ngModel)]="question" placeholder="Ask about knife rules…" aria-label="Question"></textarea>
  <button (click)="onAsk()" [disabled]="loading">Ask!</button>

  <div id="country-header-container" *ngIf="countryHeaderHtml" [innerHTML]="countryHeaderHtml"></div>

  <div class="panels" *ngIf="response">
    <div>
      <h3>Raw JSON</h3>
      <textarea readonly rows="12">{{response | json}}</textarea>
    </div>
    <div>
      <h3>Answer</h3>
      <div [innerHTML]="refinedAnswerHtml"></div>
    </div>
  </div>
  `,
  styles: [`
    .panels{display:grid;grid-template-columns:1fr 1fr;gap:1rem;margin-top:1rem}
    textarea[readonly]{width:100%}
    #country-header-container table{margin:auto}
  `]
})
export class AskComponent {
  private api = inject(ApiService);
  private sanitizer = inject(DomSanitizer);

  question = '';
  loading = false;
  progress = 0;
  statusText = '';

  response?: AskResponse;
  countryHeaderHtml: any;
  refinedAnswerHtml: any;

  setProgress(val: number, text: string) {
    this.progress = val;
    this.statusText = text;
  }

  onAsk() {
    if (!this.question?.trim()) return;
    this.loading = true;
    this.setProgress(10, 'Identifying countries in user query…');
    this.api.ask(this.question).subscribe({
      next: (res: AskResponse) => {
        this.response = res;
        this.setProgress(80, 'Drafting answer…');
        // Render country header and refined answer as safe HTML
        this.countryHeaderHtml = this.sanitizer.bypassSecurityTrustHtml(this.renderMarkdown(res.country_header));
        this.refinedAnswerHtml = this.sanitizer.bypassSecurityTrustHtml(this.renderMarkdown(res.refined_answer));
        this.setProgress(100, `Finalizing answer… Detected countries: ${res.country_detection.summary}`);
        this.loading = false;
      },
      error: (err: any) => {
        console.error(err);
        this.loading = false;
        this.setProgress(0, 'Error occurred.');
      }
    });
  }

  // minimal markdown rendering using marked if present; otherwise basic replacements
  renderMarkdown(md: string): string {
    try {
      const raw = marked.parse(md) as string;
      return DOMPurify.sanitize(raw, { USE_PROFILES: { html: true } } as any);
    } catch {
      return md.replace(/\n/g, '<br/>');
    }
  }
}
