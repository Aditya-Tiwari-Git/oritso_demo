import { Component, Input, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import * as signalR from '@microsoft/signalr';
import { ApiService } from '../core/api.service';

type ChatRole = 'user' | 'assistant' | 'agent' | 'system';
interface UiMessage { id?: number; role: ChatRole; sender?: string; content: string; createdAt: string; canEscalate?: boolean; canCreateTicket?: boolean; ticketNumber?: string; }

@Component({ selector: 'it-chat-widget', standalone: true, imports: [CommonModule, FormsModule], templateUrl: './chat-widget.html', styleUrls: ['./chat-widget.scss', './chat-widget-standalone.scss'], host: { '[class.standalone]': 'standalone' } })
export class ChatWidget implements OnInit, OnDestroy {
  @Input() title = 'Oritso IT Assistant';
  @Input() standalone = false;
  @Input() startOpen = false;
  private api = inject(ApiService); private connection?: signalR.HubConnection;
  open = signal(false); busy = signal(false); sessionId?: string; draft = '';
  catalog = signal<any>({ categories: [], services: [] }); ticketForm = signal<any | null>(null); attachment?: File;
  liveSessionId = signal<string | null>(null); liveStatus = signal(''); liveAgent = signal('');
  messages = signal<UiMessage[]>([{ role: 'assistant', content: 'Hello—how can I help today? I can troubleshoot an issue, answer an IT question, check a ticket, or connect you with support.', createdAt: new Date().toISOString() }]);

  ngOnInit() { if (this.startOpen || this.standalone) this.open.set(true); this.api.get<any>('/catalog').subscribe(x => this.catalog.set(x)); }
  ngOnDestroy() { this.connection?.stop(); }
  send(action?: 'request-live' | 'create-ticket') {
    if (action === 'create-ticket') { this.sendToBot('Create a ticket for this issue.', action); return; }
    if (action === 'request-live') { this.sendToBot('Connect me to a live agent.', action); return; }
    const value = this.draft.trim(); if (!value || this.busy()) return; this.draft = '';
    if (this.liveSessionId() && this.liveStatus() !== 'Ended') { this.connection?.invoke('SendMessage', this.liveSessionId(), value).catch(() => this.add('system', 'Message could not be sent. Please retry.')); return; }
    this.sendToBot(value);
  }
  private sendToBot(value: string, action?: string) {
    this.add('user', value); this.busy.set(true);
    this.api.post<any>('/chat', { message: value, sessionId: this.sessionId, action }).subscribe({ next: r => { this.sessionId = r.sessionId; this.add('assistant', r.message, { canEscalate: r.canEscalate, canCreateTicket: r.canCreateTicket, ticketNumber: r.ticketNumber }); if (r.ticketDraft) this.ticketForm.set({ ...r.ticketDraft }); if (r.liveSessionId) this.startLive(r.liveSessionId, r.liveStatus); this.busy.set(false); }, error: () => { this.add('system', 'Support is temporarily unavailable. Your message was not submitted—please retry.'); this.busy.set(false); } });
  }
  submitTicket() {
    const form = this.ticketForm(); if (!form || !this.sessionId || this.busy()) return; this.busy.set(true);
    this.api.post<any>('/chat/tickets', { ...form, sessionId: this.sessionId }).subscribe({ next: r => { this.add('assistant', r.message, { ticketNumber: r.ticketNumber }); this.ticketForm.set(null); if (this.attachment && r.ticketId) this.api.upload(r.ticketId, this.attachment).subscribe({ next: () => this.add('system', `Attachment ${this.attachment?.name} uploaded.`), error: () => this.add('system', 'The ticket was created, but the attachment upload failed.') }); this.attachment = undefined; this.busy.set(false); }, error: e => { this.add('system', e.error?.error || 'The ticket could not be created.'); this.busy.set(false); } });
  }
  selectAttachment(event: Event) { this.attachment = (event.target as HTMLInputElement).files?.[0]; }
  subcategories() { return this.catalog().categories.find((x: any) => x.name === this.ticketForm()?.category)?.subcategories || []; }
  endLive() { const id = this.liveSessionId(); if (id) this.api.post(`/live-support/${id}/end`, {}).subscribe(() => this.finishLive('Live support ended. You can continue with the Oritso assistant.')); }
  private startLive(id: string, status = 'Waiting') {
    this.liveSessionId.set(id); this.liveStatus.set(status || 'Waiting'); const token = this.api.session()?.token || ''; const hubUrl = this.api.baseUrl.replace(/\/api$/, '') + '/hubs/support';
    this.connection?.stop(); this.connection = new signalR.HubConnectionBuilder().withUrl(hubUrl, { accessTokenFactory: () => token }).withAutomaticReconnect().build();
    this.connection.on('MessageReceived', m => { if (!this.messages().some(x => x.id === m.id)) this.add(m.sender === this.api.session()?.userName ? 'user' : m.sender === 'System' ? 'system' : 'agent', m.body, { id: m.id, sender: m.sender, createdAt: m.createdAt }); });
    this.connection.on('SessionUpdated', session => { this.liveStatus.set(session.status); this.liveAgent.set(session.agentDisplayName || session.acceptedBy || ''); if (session.status === 'Active') this.add('system', `Connected to ${session.agentDisplayName || session.acceptedBy || 'a support agent'}.`); if (session.status === 'Ended') this.finishLive('The live conversation has ended. You can continue with the assistant.'); });
    this.connection.on('SessionDeleted', () => this.finishLive('This live conversation was closed by an administrator.'));
    this.connection.onreconnecting(() => this.liveStatus.set('Reconnecting'));
    this.connection.onreconnected(() => { this.liveStatus.set('Connected'); this.connection?.invoke('JoinSession', id); this.refreshLive(id); });
    this.connection.start().then(() => { this.connection?.invoke('JoinSession', id); this.refreshLive(id); }).catch(() => { this.liveStatus.set('Connection unavailable'); this.add('system', 'Live connection unavailable. Retrying automatically…'); });
  }
  private refreshLive(id: string) { this.api.get<any>(`/live-support/${id}`).subscribe(session => { this.liveStatus.set(session.status); this.liveAgent.set(session.agentDisplayName || session.acceptedBy || ''); const marker = session.messages.findIndex((x: any) => x.sender === 'System' && x.body.includes('Waiting for')); for (const m of session.messages.slice(Math.max(marker, 0))) if (!this.messages().some(x => x.id === m.id)) this.add(m.sender === this.api.session()?.userName ? 'user' : m.sender === 'System' ? 'system' : 'agent', m.body, { id: m.id, sender: m.sender, createdAt: m.createdAt }); }); }
  private finishLive(message: string) { this.liveStatus.set('Ended'); this.liveSessionId.set(null); this.liveAgent.set(''); this.add('system', message); this.connection?.stop(); }
  private add(role: ChatRole, content: string, extra: Partial<UiMessage> = {}) { this.messages.update(x => [...x, { role, content, createdAt: new Date().toISOString(), ...extra }]); setTimeout(() => document.querySelector('.support-messages')?.lastElementChild?.scrollIntoView({ behavior: 'smooth' }), 0); }
}
