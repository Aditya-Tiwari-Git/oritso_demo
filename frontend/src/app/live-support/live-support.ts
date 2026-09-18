import { Component, Input, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import * as signalR from '@microsoft/signalr';
import { ApiService } from '../core/api.service';

@Component({ selector: 'it-live-support', standalone: true, imports: [CommonModule, FormsModule], templateUrl: './live-support.html', styleUrl: './live-support.scss' })
export class LiveSupport implements OnInit, OnDestroy {
  @Input() compact = false;
  api = inject(ApiService); sessions = signal<any[]>([]); active = signal<any | null>(null); draft = ''; subject = 'CTS support assistance'; busy = signal(false); error = signal('');
  private connection?: signalR.HubConnection;
  get isStaff() { return ['Admin', 'ITSupport'].includes(this.api.session()?.role || ''); }
  ngOnInit() { this.load(); this.connect(); }
  ngOnDestroy() { this.connection?.stop(); }
  load() { this.api.get<any[]>('/live-support').subscribe({ next: x => this.sessions.set(x), error: () => this.error.set('Could not load live support conversations.') }); }
  request() { this.busy.set(true); this.api.post<any>('/live-support', { subject: this.subject }).subscribe({ next: x => { this.busy.set(false); this.load(); this.open(x.publicId); }, error: () => { this.busy.set(false); this.error.set('Could not request live support.'); } }); }
  accept(item: any) { this.api.post(`/live-support/${item.publicId}/accept`, {}).subscribe(() => { this.load(); this.open(item.publicId); }); }
  open(id: string) { this.api.get<any>(`/live-support/${id}`).subscribe(x => { this.active.set(x); this.connection?.invoke('JoinSession', id); }); }
  send() { const active = this.active(); const message = this.draft.trim(); if (!active || !message) return; this.draft = ''; this.connection?.invoke('SendMessage', active.publicId, message).catch(() => this.error.set('Message could not be sent.')); }
  end() { const item = this.active(); if (item) this.api.post(`/live-support/${item.publicId}/end`, {}).subscribe(() => { this.open(item.publicId); this.load(); }); }
  convert() { const item = this.active(); if (item) this.api.post<any>(`/live-support/${item.publicId}/ticket`, { createTicket: true, title: item.subject, description: 'Escalated from live support.' }).subscribe(x => { this.open(item.publicId); this.load(); }); }
  private connect() { const token = this.api.session()?.token || ''; const hubUrl = this.api.baseUrl.replace(/\/api$/, '') + '/hubs/support'; this.connection = new signalR.HubConnectionBuilder().withUrl(hubUrl, { accessTokenFactory: () => token }).withAutomaticReconnect().build(); this.connection.on('MessageReceived', m => this.active.update(x => x ? ({ ...x, messages: [...(x.messages || []), m] }) : x)); this.connection.on('SessionUpdated', () => { const id = this.active()?.publicId; if (id) this.open(id); this.load(); }); this.connection.on('QueueChanged', () => this.load()); this.connection.start().catch(() => this.error.set('Realtime connection unavailable. Refresh to retry.')); }
}
