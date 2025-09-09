import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, CleanupResponse } from '../../services/api.service';

interface FileItem {
  name: string;
  status: 'idle' | 'uploading' | 'success' | 'error';
  message?: string;
}

@Component({
  selector: 'app-admin-upload',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
  <h2>Admin Upload</h2>
  <div class="upload-guidance">
    <p>Upload .docx files named as "XX.docx" where XX is an uppercase ISO-3166 alpha-2 code (e.g., DE.docx).</p>
  </div>

  <label>Optional passcode: <input [(ngModel)]="passcode" placeholder="admin passcode"/></label>

  <div class="upload-dropzone" (drop)="onDrop($event)" (dragover)="$event.preventDefault()" (click)="fileInput.click()">
    <p>Drag and drop .docx files here or click to select.</p>
    <input type="file" #fileInput style="display:none" (change)="onFileSelect($event)" accept=".docx" multiple />
  </div>

  <div class="upload-queue" *ngIf="queue.length">
    <div class="upload-item" *ngFor="let item of queue">
      <span>{{item.name}}</span>
      <span class="file-item-status" [ngClass]="item.status">{{item.status}}</span>
      <span *ngIf="item.message">{{item.message}}</span>
    </div>
  </div>

  <div class="delete-controls">
    <h3>Cleanup Index</h3>
    <label>ISO code: <input [(ngModel)]="cleanupIso" placeholder="FR or ALL"/></label>
    <button (click)="onCleanup()">Cleanup</button>
    <div class="status-message" *ngIf="cleanupMessage" [ngClass]="cleanupClass">{{cleanupMessage}}</div>
  </div>
  `,
  styles: [`
    .upload-dropzone{border:2px dashed #ccc;border-radius:8px;padding:40px;text-align:center;cursor:pointer}
    .upload-item{display:flex;justify-content:space-between;gap:1rem;margin:8px 0}
    .file-item-status.success{background:#D1FAE5;color:#065F46;padding:2px 6px;border-radius:4px}
    .file-item-status.error{background:#FEE2E2;color:#991B1B;padding:2px 6px;border-radius:4px}
    .file-item-status.uploading{background:#DBEAFE;color:#1E40AF;padding:2px 6px;border-radius:4px}
  `]
})
export class AdminUploadComponent {
  private api = inject(ApiService);
  queue: FileItem[] = [];
  passcode = '';
  cleanupIso = '';
  cleanupMessage = '';
  cleanupClass = '';

  onFileSelect(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const files = input.files; if (!files) return;
    this.handleFiles(files);
    input.value = '';
  }

  onDrop(ev: DragEvent) {
    ev.preventDefault();
    if (ev.dataTransfer?.files) {
      this.handleFiles(ev.dataTransfer.files);
    }
  }

  private handleFiles(files: FileList) {
    Array.from(files).forEach(file => {
      const item: FileItem = { name: file.name, status: 'uploading' };
      this.queue.push(item);
      const reader = new FileReader();
      reader.onload = () => {
        const base64 = (reader.result as string).split(',')[1] || '';
        this.api.uploadBlob(file.name, base64, 'legaldocsrag', this.passcode).subscribe({
          next: (res: any) => {
            if (res && 'message' in res && res.message?.startsWith('File')) {
              item.status = 'success';
              item.message = res.message;
            } else {
              item.status = 'error';
              item.message = res.message || 'Upload failed';
            }
          },
          error: (err) => {
            item.status = 'error';
            item.message = err?.error?.message || 'Upload failed';
          }
        });
      };
      reader.readAsDataURL(file);
    });
  }

  onCleanup() {
    const iso = (this.cleanupIso || '').toUpperCase();
    if (!iso) return;
    this.api.cleanupIndex(iso as any).subscribe({
      next: (res: CleanupResponse) => {
        this.cleanupMessage = res.message;
        this.cleanupClass = res.success ? 'success' : 'error';
      },
      error: (err) => {
        this.cleanupMessage = err?.error?.error || 'Cleanup failed';
        this.cleanupClass = 'error';
      }
    });
  }
}
