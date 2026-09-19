export type PortalContext = 'default' | 'user' | 'admin' | 'agent';

export function resolvePortalContext(): PortalContext {
  const path = window.location.pathname.replace(/^\/+|\/+$/g, '').toLowerCase();
  if (path === 'user' || path === 'admin' || path === 'agent') return path;
  if (path === 'chatbot') {
    const requested = new URLSearchParams(window.location.search).get('portal')?.toLowerCase();
    if (requested === 'user' || requested === 'admin' || requested === 'agent') return requested;
  }
  return 'default';
}

export function sessionStorageKey(portal: PortalContext) {
  return portal === 'default' ? 'it-session' : `it-session-${portal}`;
}

export function portalForRole(role: string): Exclude<PortalContext, 'default'> | null {
  return role === 'User' ? 'user' : role === 'Admin' ? 'admin' : role === 'ITSupport' || role === 'Agent' ? 'agent' : null;
}

export function expectedRole(portal: PortalContext): string | null {
  return portal === 'user' ? 'User' : portal === 'admin' ? 'Admin' : portal === 'agent' ? 'ITSupport' : null;
}

export function portalDisplayName(portal: PortalContext) {
  return portal === 'user' ? 'User Portal' : portal === 'admin' ? 'Admin Portal' : portal === 'agent' ? 'Support Agent Portal' : 'IT Support CRM';
}
