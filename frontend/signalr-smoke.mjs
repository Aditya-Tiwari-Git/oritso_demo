import * as signalR from '@microsoft/signalr';

const base = process.env.ORITSO_API || 'http://localhost:5266';
async function login(userName, password) {
  const response = await fetch(`${base}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ userName, password }) });
  if (!response.ok) throw new Error(`Login failed for ${userName}`);
  return (await response.json()).token;
}
async function api(path, token, method = 'GET', body) {
  const response = await fetch(`${base}${path}`, { method, headers: { authorization: `Bearer ${token}`, ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  if (!response.ok) throw new Error(`${method} ${path} returned ${response.status}`);
  return response.json();
}
const userToken = await login('user', 'User@123');
const agentToken = await login('rahul', 'Rahul@123');
const session = await api('/api/live-support', userToken, 'POST', { subject: 'SignalR realtime verification' });
const connect = async token => { const hub = new signalR.HubConnectionBuilder().withUrl(`${base}/hubs/support`, { accessTokenFactory: () => token }).withAutomaticReconnect().build(); await hub.start(); await hub.invoke('JoinSession', session.publicId); return hub; };
const userHub = await connect(userToken); const agentHub = await connect(agentToken);
await api(`/api/live-support/${session.publicId}/accept`, agentToken, 'POST', {});
const received = new Promise((resolve, reject) => { const timeout = setTimeout(() => reject(new Error('Realtime message timed out')), 5000); userHub.on('MessageReceived', message => { clearTimeout(timeout); resolve(message); }); });
await agentHub.invoke('SendMessage', session.publicId, 'Realtime CTS test message');
const message = await received;
if (message.sender !== 'rahul' || message.body !== 'Realtime CTS test message') throw new Error('Unexpected realtime message payload');
await api(`/api/live-support/${session.publicId}/end`, agentToken, 'POST', {});
await Promise.all([userHub.stop(), agentHub.stop()]);
console.log('PASS SignalR two-client realtime message exchange');
