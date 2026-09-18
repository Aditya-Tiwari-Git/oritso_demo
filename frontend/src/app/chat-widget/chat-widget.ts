import { Component, Input, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';

@Component({ selector: 'it-chat-widget', standalone: true, imports: [CommonModule, FormsModule], templateUrl: './chat-widget.html', styleUrl: './chat-widget.scss' })
export class ChatWidget {
  @Input() title = 'Oritso IT Assistant';
  private api = inject(ApiService);
  open = signal(false); busy = signal(false); sessionId?: string; draft = '';
  messages = signal<{ role: 'user' | 'assistant'; content: string; canEscalate?: boolean; awaitingConfirmation?: boolean }[]>([{ role: 'assistant', content: 'Hi! I’m the Oritso IT assistant. Tell me about a scanner jam, connectivity problem, CBS catch-and-dispatch issue, or ask for a ticket update.' }]);
  send(action?: string) { const text = action === 'confirm-ticket' ? 'Yes, create the ticket.' : action === 'request-live' ? 'I want a live support agent.' : this.draft.trim(); if (!text || this.busy()) return; this.messages.update(x => [...x, { role: 'user', content: text }]); this.draft = ''; this.busy.set(true); this.api.post<any>('/chat', { message: text, sessionId: this.sessionId, action }).subscribe({ next: r => { this.sessionId = r.sessionId; this.messages.update(x => [...x, { role: 'assistant', content: r.message, canEscalate: r.canEscalate, awaitingConfirmation: r.awaitingConfirmation }]); this.busy.set(false); }, error: () => { this.messages.update(x => [...x, { role: 'assistant', content: 'I could not reach support. Your information has not been lost—please try again.' }]); this.busy.set(false); } }); }
}
