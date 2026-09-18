import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService, Ticket } from './core/api.service';
import { ChatWidget } from './chat-widget/chat-widget';

@Component({ selector: 'app-root', standalone: true, imports: [CommonModule, FormsModule, ChatWidget], templateUrl: './app.html', styleUrl: './app.scss' })
export class App {
  api = inject(ApiService); view = signal('overview'); tickets = signal<Ticket[]>([]); catalog = signal<any>({ categories: [], services: [], priorities: [], statuses: [], assignmentGroups: [] }); admin = signal<any>({ groups: [], categories: [], services: [], rules: [], articles: [] }); selected = signal<Ticket | null>(null); loading = signal(false); error = signal(''); notice = signal('');
  login = { userName: 'admin', password: 'Admin@123' }; ticket = { type: 'Standard', title: '', description: '', category: 'Software', priority: 'Medium', service: 'Business Applications' }; comment = ''; article = { title: '', content: '', keywords: '', category: 'General', isPublished: true }; rule: any = { name: '', order: 100, category: '', service: '', priority: '', ticketType: '', keywords: '', assignmentGroupId: null, isActive: true }; newItem = { kind: 'groups', name: '' };
  metrics = computed(() => ({ total: this.tickets().length, active: this.tickets().filter(x => !['Resolved', 'Closed'].includes(x.status)).length, major: this.tickets().filter(x => x.type === 'Major Incident').length, resolved: this.tickets().filter(x => x.status === 'Resolved').length }));
  signIn() { this.error.set(''); this.api.login(this.login.userName, this.login.password).subscribe({ next: () => this.load(), error: () => this.error.set('Invalid username or password.') }); }
  logout() { this.api.logout(); this.tickets.set([]); this.view.set('overview'); }
  load() { this.loading.set(true); this.api.get<any>('/catalog').subscribe(c => this.catalog.set(c)); this.api.get<any[]>('/knowledge').subscribe(articles => this.admin.update(x => ({ ...x, articles }))); this.api.get<Ticket[]>('/tickets').subscribe({ next: t => { this.tickets.set(t); this.loading.set(false); }, error: () => this.loading.set(false) }); if (this.api.session()?.role === 'Admin') this.loadAdmin(); }
  loadAdmin() { this.api.get<any>('/admin/configuration').subscribe(x => this.admin.set(x)); }
  navigate(view: string) { this.view.set(view); this.selected.set(null); if (view === 'admin') this.loadAdmin(); }
  createTicket() { this.error.set(''); if (!this.ticket.title.trim() || !this.ticket.description.trim()) { this.error.set('Title and description are required.'); return; } this.loading.set(true); this.api.post<Ticket>('/tickets', this.ticket).subscribe({ next: t => { this.notice.set(`${t.number} created and routed successfully.`); this.ticket.title = ''; this.ticket.description = ''; this.loading.set(false); this.navigate('tickets'); this.load(); }, error: e => { this.error.set(e.error?.error || 'Ticket could not be created.'); this.loading.set(false); } }); }
  openTicket(id: number) { this.api.get<Ticket>(`/tickets/${id}`).subscribe(t => { this.selected.set(t); this.view.set('detail'); }); }
  addComment() { const t = this.selected(); if (!t || !this.comment.trim()) return; this.api.post(`/tickets/${t.id}/comments`, { body: this.comment }).subscribe(() => { this.comment = ''; this.openTicket(t.id); }); }
  updateStatus(status: string) { const t = this.selected(); if (!t) return; this.api.patch(`/tickets/${t.id}`, { status }).subscribe(() => this.openTicket(t.id)); }
  upload(event: Event) { const t = this.selected(); const file = (event.target as HTMLInputElement).files?.[0]; if (t && file) this.api.upload(t.id, file).subscribe({ next: () => this.openTicket(t.id), error: e => this.error.set(e.error?.error || 'Upload failed.') }); }
  addItem() { if (!this.newItem.name.trim()) return; this.api.post(`/admin/${this.newItem.kind}`, { name: this.newItem.name, isActive: true }).subscribe(() => { this.newItem.name = ''; this.loadAdmin(); this.load(); }); }
  addArticle() { this.api.post('/admin/articles', this.article).subscribe(() => { this.article = { title: '', content: '', keywords: '', category: 'General', isPublished: true }; this.loadAdmin(); }); }
  addRule() { this.rule.assignmentGroupId = Number(this.rule.assignmentGroupId); this.api.post('/admin/rules', this.rule).subscribe(() => { this.rule = { name: '', order: 100, category: '', service: '', priority: '', ticketType: '', keywords: '', assignmentGroupId: null, isActive: true }; this.loadAdmin(); }); }
  remove(kind: string, id: number) { if (!confirm('Delete this configuration item?')) return; this.api.delete(`/admin/${kind}/${id}`).subscribe(() => this.loadAdmin()); }
  groupName(id?: number) { return this.catalog().assignmentGroups.find((x: any) => x.id === id)?.name || this.admin().groups.find((x: any) => x.id === id)?.name || 'Unassigned'; }
  greeting() { return new Date().getHours() < 12 ? 'morning' : 'afternoon'; }
  ngOnInit() { if (this.api.session()) this.load(); }
}
