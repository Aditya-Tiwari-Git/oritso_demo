import { HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { resolvePortalContext, sessionStorageKey } from './session-context';
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const storageKey = sessionStorageKey(resolvePortalContext());
  let token = ''; try { token = JSON.parse(localStorage.getItem(storageKey) || 'null')?.token || ''; } catch {}
  return next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req).pipe(catchError(error => { if (error.status === 401 && !req.url.includes('/auth/login')) { localStorage.removeItem(storageKey); location.reload(); } return throwError(() => error); }));
};
