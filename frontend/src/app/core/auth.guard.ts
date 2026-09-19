import { CanActivateFn } from '@angular/router';
import { expectedRole, PortalContext, sessionStorageKey } from './session-context';
import { Session } from './api.service';

export const portalGuard: CanActivateFn = route => {
  const portal = route.data['portal'] as PortalContext;
  const requiredRole = expectedRole(portal);
  try {
    const session = JSON.parse(localStorage.getItem(sessionStorageKey(portal)) || 'null') as Session | null;
    if (session && session.role === requiredRole && new Date(session.expiresAt) > new Date()) return true;
  } catch { /* Invalid stored data is treated as signed out. */ }
  localStorage.removeItem(sessionStorageKey(portal));
  window.location.replace('/');
  return false;
};
