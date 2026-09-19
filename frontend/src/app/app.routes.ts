import { Routes } from '@angular/router';
import { Component } from '@angular/core';
import { ChatbotPage } from './chatbot-page/chatbot-page';
import { portalGuard } from './core/auth.guard';

@Component({ standalone: true, template: '' })
class PortalRouteMarker {}

export const routes: Routes = [
  { path: 'user', component: PortalRouteMarker, title: 'Oritso User Portal', canActivate: [portalGuard], data: { portal: 'user' } },
  { path: 'admin', component: PortalRouteMarker, title: 'Oritso Admin Portal', canActivate: [portalGuard], data: { portal: 'admin' } },
  { path: 'agent', component: PortalRouteMarker, title: 'Oritso Support Agent Portal', canActivate: [portalGuard], data: { portal: 'agent' } },
  { path: 'chatbot', component: ChatbotPage, title: 'Oritso IT Assistant' }
];
