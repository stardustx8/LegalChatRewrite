import { Component, inject, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ApiService, CleanupResponse } from '../../services/api.service';

interface FileItem {
  name: string;
  status: 'idle' | 'uploading' | 'success' | 'error';
  message?: string;
  base64?: string;
}

@Component({
  selector: 'app-admin-upload',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
  <div class="container">
    <div class="main-card">
      <button class="close-button" (click)="closeModal()" aria-label="Close admin panel">&times;</button>
      <h1>Admin Upload</h1>
      
      <div class="upload-section">
        <div class="section-description">
          <p>Upload .docx files named as <strong>"XX.docx"</strong> where XX is an uppercase ISO-3166 alpha-2 code (e.g., DE.docx).</p>
        </div>

        <div class="upload-dropzone" 
             (drop)="onDrop($event)" 
             (dragover)="onDragOver($event)" 
             (dragleave)="onDragLeave($event)"
             [class.drag-over]="dragOver"
             (click)="fileInput.click()">
          <div class="file-icon">📁</div>
          <p>Drag and drop .docx files here or click to select.</p>
          <input type="file" #fileInput style="display:none" (change)="onFileSelect($event)" accept=".docx" multiple />
        </div>

        <div class="file-list-container" *ngIf="queue.length">
          <div class="file-item" *ngFor="let item of queue">
            <span class="file-item-name">{{item.name}}</span>
            <span class="file-item-status" [ngClass]="item.status">
              <ng-container [ngSwitch]="item.status">
                <span *ngSwitchCase="'success'">✓ Uploaded</span>
                <span *ngSwitchCase="'error'" [title]="item.message || 'Upload failed'">❌ Failed</span>
                <span *ngSwitchCase="'uploading'">Uploading...</span>
                <span *ngSwitchDefault>Ready</span>
              </ng-container>
            </span>
          </div>
        </div>

        <div class="upload-actions" *ngIf="queue.length">
          <button class="btn-primary" (click)="uploadFiles()" [disabled]="!canUpload()">
            Upload {{ pendingFiles.length }} file{{ pendingFiles.length === 1 ? '' : 's' }}
          </button>
          <button class="btn-link" (click)="clearQueue()" [disabled]="isUploading()">Clear</button>
          <span class="hint" *ngIf="pendingFiles.length && !canUpload()">Preparing files...</span>
        </div>
      </div>

      <div class="cleanup-section">
        <h2>Delete Documents by Country</h2>
        <div class="section-description">
          <p>Remove uploaded documents for specific countries using ISO codes:<br>
          • Single country: <strong>FR</strong> (for France)<br>
          • Multiple countries: <strong>FR, DE, CH</strong> (comma-separated)<br>
          • All countries: <strong>ALL</strong> (requires confirmation)</p>
        </div>
        <div class="cleanup-controls">
          <input 
            [(ngModel)]="cleanupIso" 
            placeholder="Enter ISO codes (e.g., FR or FR,DE,CH or ALL)"
            class="cleanup-input"/>
          <button (click)="onCleanup()" class="btn-secondary">Delete Documents</button>
        </div>
        <div class="status-message" *ngIf="cleanupMessage" [ngClass]="cleanupClass">{{cleanupMessage}}</div>
      </div>
    </div>
  </div>
  `,
  styles: [`
    .close-button {
      position: absolute;
      top: 1rem;
      right: 1rem;
      background: none;
      border: none;
      font-size: 2rem;
      color: var(--color-grey-600);
      cursor: pointer;
      width: 40px;
      height: 40px;
      display: flex;
      align-items: center;
      justify-content: center;
      border-radius: var(--radius-md);
      transition: all 0.2s ease;
      
      &:hover {
        background: var(--color-grey-100);
        color: var(--accent);
      }
    }
    
    h1 {
      color: #0f172a;
      text-align: center;
      margin-bottom: 2rem;
      font-size: 2rem;
      font-weight: 600;
    }
    
    .upload-section {
      margin-bottom: 3rem;
    }
    
    .section-description {
      background: #f9fafb;
      border-left: 3px solid var(--accent);
      padding: 1rem 1.5rem;
      margin-bottom: 2rem;
      border-radius: var(--radius-sm);
      
      p {
        margin: 0;
        color: var(--text);
        font-size: 0.95rem;
      }
      
      strong {
        color: var(--accent);
        font-weight: 600;
      }
    }
    
    .upload-dropzone {
      background: linear-gradient(to bottom, #FAFAFA, #F5F5F5);
      border: 2px dashed var(--color-grey-300);
      border-radius: var(--radius-lg);
      padding: 3rem 2rem;
      text-align: center;
      cursor: pointer;
      transition: all 0.3s ease;
      
      &:hover {
        border-color: var(--accent);
        background: linear-gradient(to bottom, #FFF5F7, #FFEFF2);
      }
      
      &.drag-over {
        border-color: var(--accent);
        background: linear-gradient(to bottom, #FFF5F7, #FFEFF2);
        transform: scale(1.02);
      }
      
      .file-icon {
        font-size: 48px;
        margin-bottom: 1rem;
        opacity: 0.3;
      }
      
      p {
        color: var(--color-grey-600);
        font-size: 1rem;
        margin: 0;
      }
    }
    
    .file-list-container {
      margin-top: 1.5rem;
      max-height: 200px;
      overflow-y: auto;
    }
    .upload-actions {
      margin-top: 0.5rem;
      display: flex;
      align-items: center;
      gap: 0.75rem;
    }
    .btn-link {
      background: transparent;
      border: none;
      color: var(--color-grey-600);
      cursor: pointer;
    }
    .btn-link:hover { text-decoration: underline; color: var(--color-grey-800); }
    .hint { color: var(--muted); font-size: 0.9rem; }
    
    .file-item {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0.75rem 1rem;
      background: var(--color-grey-100);
      border-radius: var(--radius-md);
      margin-bottom: 0.5rem;
      transition: all 0.2s ease;
      
      &:hover {
        background: var(--color-grey-200);
      }
      
      .file-item-name {
        flex: 1;
        font-size: 0.9rem;
        color: var(--color-grey-900);
      }
      
      .file-item-status {
        font-size: 0.85rem;
        padding: 0.25rem 0.75rem;
        border-radius: var(--radius-sm);
        font-weight: 500;
        
        &.success {
          background: #D1FAE5;
          color: #065F46;
        }
        
        &.error {
          background: #FEE2E2;
          color: #991B1B;
        }
        
        &.uploading {
          background: #DBEAFE;
          color: #1E40AF;
        }
        
        &.idle {
          background: var(--color-grey-200);
          color: var(--color-grey-600);
        }
      }
    }
    
    .cleanup-section {
      border-top: 2px solid var(--color-grey-200);
      padding-top: 2rem;
      
      h2 {
        font-size: 1.25rem;
        font-weight: 600;
        color: var(--text);
        margin-bottom: 1.5rem;
      }
    }
    
    .cleanup-controls {
      display: flex;
      gap: 1rem;
      align-items: center;
      margin-bottom: 1rem;
      
      .cleanup-input {
        flex: 1;
        padding: 0.75rem 1rem;
        border: 1px solid var(--color-grey-300);
        border-radius: var(--radius-md);
        font-size: 1rem;
        transition: all 0.2s ease;
        
        &:focus {
          outline: none;
          border-color: var(--accent);
          box-shadow: 0 0 0 3px var(--ring);
        }
        
        &::placeholder {
          color: var(--muted);
        }
      }
    }
  `]
})
export class AdminUploadComponent implements OnInit {
  private api = inject(ApiService);
  private router = inject(Router);
  
  // Upload
  queue: FileItem[] = [];
  pendingFiles: FileItem[] = [];
  dragOver = false;
  
  
  // Cleanup
  cleanupIso = '';
  cleanupMessage = '';
  cleanupClass = '';
  
  ngOnInit() {}
  
  onCleanup() {
    if (!this.cleanupIso?.trim()) return;
    const input = this.cleanupIso.trim().toUpperCase();
    
    // Parse comma-separated ISO codes
    const isoCodes = input.split(',').map(code => code.trim()).filter(code => code);
    
    if (isoCodes.length === 0) return;
    
    // Require confirmation for ALL
    if (isoCodes.includes('ALL')) {
      const confirmed = confirm('⚠️ Are you sure you want to delete ALL documents for ALL countries? This action cannot be undone.');
      if (!confirmed) return;
      // Process only ALL
      this.processCleanup('ALL');
    } else if (isoCodes.length > 1) {
      // Require confirmation for multiple countries
      const countriesList = isoCodes.join(', ');
      const confirmed = confirm(`⚠️ Are you sure you want to delete documents for these countries: ${countriesList}? This action cannot be undone.`);
      if (!confirmed) return;
      // Process each country
      this.processBatchCleanup(isoCodes);
    } else {
      // Single country, no confirmation needed
      this.processCleanup(isoCodes[0]);
    }
  }
  
  private processCleanup(iso: string) {
    this.api.cleanupIndex(iso).subscribe({
      next: (res: CleanupResponse) => {
        this.cleanupMessage = res.message;
        this.cleanupClass = 'success';
        this.cleanupIso = '';
      },
      error: (err: any) => {
        this.cleanupMessage = err?.error?.message || 'Delete operation failed';
        this.cleanupClass = 'error';
      }
    });
  }
  
  private processBatchCleanup(isoCodes: string[]) {
    let successCount = 0;
    let failedCodes: string[] = [];
    let completed = 0;
    
    isoCodes.forEach(iso => {
      this.api.cleanupIndex(iso).subscribe({
        next: () => {
          successCount++;
          completed++;
          if (completed === isoCodes.length) {
            this.showBatchResult(successCount, failedCodes, isoCodes.length);
          }
        },
        error: () => {
          failedCodes.push(iso);
          completed++;
          if (completed === isoCodes.length) {
            this.showBatchResult(successCount, failedCodes, isoCodes.length);
          }
        }
      });
    });
  }

  canUpload(): boolean {
    // Enable only when all pending files finished reading and nothing is uploading
    if (!this.pendingFiles.length) return false;
    if (this.isUploading()) return false;
    return this.pendingFiles.every(f => !!f.base64 && f.status === 'idle');
  }

  isUploading(): boolean {
    return this.queue.some(f => f.status === 'uploading');
  }

  clearQueue() {
    if (this.isUploading()) return;
    this.queue = [];
    this.pendingFiles = [];
  }
  
  private showBatchResult(success: number, failed: string[], total: number) {
    if (failed.length === 0) {
      this.cleanupMessage = `Successfully deleted documents for ${success} countries`;
      this.cleanupClass = 'success';
    } else if (success === 0) {
      this.cleanupMessage = `Failed to delete documents for: ${failed.join(', ')}`;
      this.cleanupClass = 'error';
    } else {
      this.cleanupMessage = `Deleted ${success}/${total} countries. Failed: ${failed.join(', ')}`;
      this.cleanupClass = 'warning';
    }
    this.cleanupIso = '';
  }
  
  closeModal() {
    this.router.navigate(['/ask']);
  }
  
  
  onFileSelect(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const files = input.files;
    if (!files) return;
    this.handleFiles(files);
    input.value = '';
  }
  
  onDrop(ev: DragEvent) {
    ev.preventDefault();
    this.dragOver = false;
    if (ev.dataTransfer?.files) {
      this.handleFiles(ev.dataTransfer.files);
    }
  }
  
  onDragOver(ev: DragEvent) {
    ev.preventDefault();
    this.dragOver = true;
  }
  
  onDragLeave(ev: DragEvent) {
    ev.preventDefault();
    this.dragOver = false;
  }
  
  private handleFiles(files: FileList) {
    Array.from(files).forEach(file => {
      if (!file.name.endsWith('.docx')) {
        alert(`${file.name} is not a .docx file`);
        return;
      }
      
      const item: FileItem = { name: file.name, status: 'idle' };
      this.queue.push(item);
      this.pendingFiles.push(item);
      
      const reader = new FileReader();
      reader.onload = () => {
        const base64 = (reader.result as string).split(',')[1] || '';
        item.base64 = base64;
      };
      reader.readAsDataURL(file);
    });
  }
  
  
  uploadFiles() {
    if (!this.canUpload()) return;
    const countries = this.pendingFiles.map(f => f.name.replace('.docx', '').toUpperCase());
    const msg = `Warning: This will OVERWRITE existing content for:\n\n${countries.join(', ')}\n\nChanges may take ~15 minutes to propagate. Continue?`;
    if (!confirm(msg)) return;
    
    this.pendingFiles.forEach(item => {
      if (!item.base64) return;
      
      item.status = 'uploading';
      this.api.uploadBlob(item.name, item.base64, 'legaldocsrag').subscribe({
        next: (res: any) => {
          item.status = 'success';
          item.message = res.message;
          this.pendingFiles = this.pendingFiles.filter(f => f !== item);
        },
        error: (err) => {
          item.status = 'error';
          item.message = err?.error?.message || err?.message || 'Upload failed';
          // Remove from pending so upload button can be used again
          this.pendingFiles = this.pendingFiles.filter(f => f !== item);
        }
      });
    });
  }
}