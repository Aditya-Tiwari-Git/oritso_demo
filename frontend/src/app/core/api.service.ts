import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';
import { expectedRole, portalForRole, PortalContext, resolvePortalContext, sessionStorageKey } from './session-context';

export interface Session { token: string; userName: string; displayName: string; email: string; role: string; expiresAt: string; }
export interface Ticket { id: number; number: string; type: string; title: string; description: string; category: string; subcategory: string; service: string; priority: string; impact: string; urgency: string; status: string; assignmentGroupId?: number; assignedAgent?: string; createdBy: string; createdByDisplayName?: string; createdByEmail?: string; resolutionCode?: string; resolutionNotes?: string; isEscalated: boolean; parentMajorIncidentId?: number; createdAt: string; updatedAt: string; comments?: any[]; workNotes?: any[]; history?: any[]; attachments?: any[]; }

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);
  readonly baseUrl = (window as any).__IT_CONFIG__?.apiUrl || 'http://localhost:5266/api';
  readonly portalContext: PortalContext = resolvePortalContext();
  readonly storageKey = sessionStorageKey(this.portalContext);
  readonly session = signal<Session | null>(this.portalContext === 'default' ? null : this.readSession());
  private readSession(): Session | null { try { const value = JSON.parse(localStorage.getItem(this.storageKey) || 'null') as Session | null; const required = expectedRole(this.portalContext); if (value && (!required || value.role === required) && new Date(value.expiresAt) > new Date()) return value; localStorage.removeItem(this.storageKey); return null; } catch { localStorage.removeItem(this.storageKey); return null; } }
  login(userName: string, password: string) { return this.http.post<Session>(`${this.baseUrl}/auth/login`, { userName, password }).pipe(tap(s => { const destination = portalForRole(s.role); if (!destination) throw new Error('The authenticated account has an unsupported role.'); localStorage.setItem(sessionStorageKey(destination), JSON.stringify(s)); })); }
  logout() { localStorage.removeItem(this.storageKey); this.session.set(null); }
  get<T>(path: string): Observable<T> { return this.http.get<T>(`${this.baseUrl}${path}`); }
  post<T>(path: string, body: unknown): Observable<T> { return this.http.post<T>(`${this.baseUrl}${path}`, body); }
  patch<T>(path: string, body: unknown): Observable<T> { return this.http.patch<T>(`${this.baseUrl}${path}`, body); }
  put<T>(path: string, body: unknown): Observable<T> { return this.http.put<T>(`${this.baseUrl}${path}`, body); }
  delete(path: string) { return this.http.delete(`${this.baseUrl}${path}`); }
  upload(ticketId: number, file: File) { const form = new FormData(); form.append('file', file); return this.http.post(`${this.baseUrl}/tickets/${ticketId}/attachments`, form); }
  download(attachment: any) { this.http.get(`${this.baseUrl}/attachments/${attachment.id}`, { responseType: 'blob' }).subscribe(blob => { const url = URL.createObjectURL(blob); const link = document.createElement('a'); link.href = url; link.download = attachment.originalName; link.click(); URL.revokeObjectURL(url); }); }
}
