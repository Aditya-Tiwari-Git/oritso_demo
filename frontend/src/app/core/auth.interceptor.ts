import { HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  let token = ''; try { token = JSON.parse(localStorage.getItem('it-session') || 'null')?.token || ''; } catch {}
  return next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req).pipe(catchError(error => { if (error.status === 401 && !req.url.includes('/auth/login')) { localStorage.removeItem('it-session'); location.reload(); } return throwError(() => error); }));
};
