import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface CountryDetection {
  iso_codes: string[];
  available: string[];
  summary: string;
}
export interface AskResponse {
  country_header: string;
  refined_answer: string;
  country_detection: CountryDetection;
}
export interface CleanupResponse {
  success: boolean;
  message: string;
  deleted_count: number;
  failed_count: number;
  iso_code: string;
  warning?: string;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private base = environment.baseUrl || '';
  private adminBase = (environment as any).adminBase || this.base;
  constructor(private http: HttpClient) {}

  ask(question: string): Observable<AskResponse> {
    const url = this.base + '/api/ask';
    return this.http.post<AskResponse>(url, { question });
  }

  uploadBlob(filename: string, base64: string, container = 'legaldocsrag'): Observable<{ message: string; iso_code: string } | { success: false; message: string }> {
    const url = this.adminBase + '/api/upload_blob';
    return this.http.post<{ message: string; iso_code: string } | { success: false; message: string }>(url, { filename, file_data: base64, container });
  }

  cleanupIndex(isoCode: string | 'ALL'): Observable<CleanupResponse> {
    const url = this.adminBase + '/api/cleanup_index';
    return this.http.post<CleanupResponse>(url, { iso_code: isoCode });
  }
}
