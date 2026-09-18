import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, tap } from 'rxjs';

export interface Session { token: string; userName: string; displayName: string; role: string; expiresAt: string; }
export interface Ticket { id: number; number: string; type: string; title: string; description: string; category: string; service: string; priority: string; status: string; assignmentGroupId?: number; createdBy: string; createdAt: string; updatedAt: string; comments?: any[]; history?: any[]; attachments?: any[]; }

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);
  readonly baseUrl = (window as any).__IT_CONFIG__?.apiUrl || 'http://localhost:5266/api';
  readonly session = signal<Session | null>(this.readSession());
  private readSession(): Session | null { try { return JSON.parse(localStorage.getItem('it-session') || 'null'); } catch { return null; } }
  login(userName: string, password: string) { return this.http.post<Session>(`${this.baseUrl}/auth/login`, { userName, password }).pipe(tap(s => { localStorage.setItem('it-session', JSON.stringify(s)); this.session.set(s); })); }
  logout() { localStorage.removeItem('it-session'); this.session.set(null); }
  get<T>(path: string): Observable<T> { return this.http.get<T>(`${this.baseUrl}${path}`); }
  post<T>(path: string, body: unknown): Observable<T> { return this.http.post<T>(`${this.baseUrl}${path}`, body); }
  patch<T>(path: string, body: unknown): Observable<T> { return this.http.patch<T>(`${this.baseUrl}${path}`, body); }
  delete(path: string) { return this.http.delete(`${this.baseUrl}${path}`); }
  upload(ticketId: number, file: File) { const form = new FormData(); form.append('file', file); return this.http.post(`${this.baseUrl}/tickets/${ticketId}/attachments`, form); }
}
