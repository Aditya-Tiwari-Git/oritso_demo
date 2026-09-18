import { HttpInterceptorFn } from '@angular/common/http';
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  let token = ''; try { token = JSON.parse(localStorage.getItem('it-session') || 'null')?.token || ''; } catch {}
  return next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req);
};
