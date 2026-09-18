import { Component, Input, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';

@Component({ selector: 'it-chat-widget', standalone: true, imports: [CommonModule, FormsModule], templateUrl: './chat-widget.html', styleUrl: './chat-widget.scss' })
export class ChatWidget {
  @Input() title = 'IT Support Assistant';
  private api = inject(ApiService);
  open = signal(false); busy = signal(false); sessionId?: string; draft = '';
  messages = signal<{ role: 'user' | 'assistant'; content: string; canEscalate?: boolean }[]>([{ role: 'assistant', content: 'Hi! Describe the IT problem you are having, or ask me for a ticket update.' }]);
  send(createTicket = false) { const text = this.draft.trim(); if (!text || this.busy()) return; if (!createTicket) { this.messages.update(x => [...x, { role: 'user', content: text }]); this.draft = ''; } this.busy.set(true); this.api.post<any>('/chat', { message: text, sessionId: this.sessionId, createTicket }).subscribe({ next: r => { this.sessionId = r.sessionId; this.messages.update(x => [...x, { role: 'assistant', content: r.message, canEscalate: r.canEscalate }]); this.busy.set(false); }, error: () => { this.messages.update(x => [...x, { role: 'assistant', content: 'I could not reach support. Please try again.' }]); this.busy.set(false); } }); }
  escalate() { this.draft = this.messages().filter(x => x.role === 'user').at(-1)?.content || 'Chatbot escalation'; this.send(true); }
}
