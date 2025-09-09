import { Routes } from '@angular/router';
import { AskComponent } from './components/ask/ask.component';
import { AdminUploadComponent } from './components/admin-upload/admin-upload.component';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'ask' },
  { path: 'ask', component: AskComponent },
  { path: 'admin-upload', component: AdminUploadComponent },
  { path: '**', redirectTo: 'ask' }
];
