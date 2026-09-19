# Oritso IT Support CRM

Oritso IT Support CRM is a demo-ready IT Service Management application for banking Cheque Truncation System (CTS) support. It combines an Angular 20 portal, ASP.NET Core 10 API, EF Core/SQLite, a knowledge-grounded OpenAI assistant, and SignalR live support.

## Demonstrable workflows

- Bank users create incidents, see only their tickets, add comments/evidence, use approved CTS guidance, and request live support.
- Agents see only tickets in their assignment groups or assigned directly to them. They can accept, reassign, prioritize, investigate with internal notes, escalate, resolve, and participate in realtime chat.
- Admins manage user emails, agent-to-group memberships, support teams, categories/subcategories, services, routing rules, automatic priority rules, and knowledge articles.
- Admins can permanently delete tickets, chatbot conversations, and live-support transcripts from the Admin danger zone. Related records are cascaded, unreferenced attachment files are removed, and a deletion tombstone is retained in the audit log.
- Ordered routing matches classification plus normalized free-text keywords, URLs, and domains. Scanner work goes to **CTS Hardware Support**, configured CBS hosts such as `cbs.com` go to **CBS Support**, and unmatched work goes to **Service Desk**.
- Priority is server-calculated from ordered Admin rules: CBS defaults to **High**, scanner issues to **Medium**, and unmatched work to **Low**. User-supplied priority values are ignored.
- The bot searches approved knowledge first. If troubleshooting is unresolved, it offers either an in-widget prefilled ticket form or a live-agent transfer; it supports multiple issue cycles in one session.
- Live support uses authenticated SignalR WebSockets, persists the transcript, and can link or convert a conversation into a routed ticket.

## Demo personas

Accounts are intentionally configured in `backend/ItSupport.Api/credentials.json` rather than ASP.NET Identity:

| Persona | Login | Password | Initial queue |
|---|---|---|---|
| Admin | `admin` | `Admin@123` | All tickets |
| CTS agent | `rahul` | `Rahul@123` | CTS Hardware Support |
| CBS agent | `priya` | `Priya@123` | CBS Support |
| Service Desk agent | `agent` | `Agent@123` | Service Desk |
| Bank user | `user` | `User@123` | Own tickets only |

Change every password and the signing key before sharing the demo. Passwords are plaintext only because this project explicitly uses editable file authentication; a real deployment must use hashed passwords or an enterprise identity provider.

## Local setup

Prerequisites:

- .NET 10 SDK
- Node.js 22 or newer
- PowerShell on Windows

Create a local environment file:

```powershell
Copy-Item .env.example .env
```

Set a long random `AUTH__SIGNINGKEY`. Add `OPENAI_API_KEY` if LLM wording is required. The assistant still performs classification, KB search, intake, confirmation, ticket creation, routing, and live escalation when OpenAI is disabled or unavailable.

Install and build:

```powershell
dotnet restore ItSupport.slnx
dotnet build ItSupport.slnx
cd frontend
cmd /c npm install
cmd /c npm run build
cd ..
```

Start the API:

```powershell
dotnet run --project backend/ItSupport.Api
```

Start Angular in a second terminal:

```powershell
cd frontend
cmd /c npm start
```

Open `http://localhost:4200`. The API root at `http://localhost:5266/`, `/health`, and `/api/auth/login` are public; protected APIs require the signed session token Angular attaches through its HTTP interceptor.

## OpenAI diagnostics

All OpenAI traffic originates in ASP.NET Core; the key is never sent to Angular or returned by an API. Logs explicitly show:

- whether OpenAI is enabled;
- the selected model;
- request start and successful response;
- HTTP or transport failure;
- fallback to deterministic, knowledge-grounded behavior.

Admin UI/API status is available at `/api/admin/bot-status`. An HTTP `429` means the key was accepted into the integration path but its OpenAI project needs available quota/rate capacity. Update billing/quota for that key or leave fallback active.

## Reset and reseed

Stop the API and run:

```powershell
powershell -ExecutionPolicy Bypass -File tools/reset-demo.ps1
```

The next API start recreates SQLite with CTS teams, memberships, classifications, routing rules, automatic priority rules, statuses, and three complete knowledge articles. Existing databases receive the additive priority-rule table and missing enhancement seed data at startup without being recreated. The pre-upgrade database was preserved under `backend/ItSupport.Api/data/backup-pre-cts-upgrade`.

## Verification

With the API running:

```powershell
powershell -ExecutionPolicy Bypass -File tools/smoke-test.ps1
cd frontend
node signalr-smoke.mjs
```

The API suite covers public/protected endpoints, all persona logins, queue authorization, sequential numbering, URL/domain routing, deterministic priority precedence, user priority-tampering resistance, email/contact data, assignment, comments/work notes, history, resolution, general and multi-issue bot flows, prefilled bot ticket creation, CBS/scanner knowledge, contextual live transfer, transcript persistence, conversion to a ticket, admin membership changes, Admin-only operational deletion, database cascades, physical attachment cleanup, and deletion audit tombstones. The SignalR test establishes two authenticated WebSocket clients and verifies both message directions, agent identity, and persisted ordering.

## Docker/server deployment

```bash
cp .env.example .env
# edit .env and credentials.json
docker compose up -d --build
```

Open port `8080`, or place a TLS-enabled load balancer/Caddy/Nginx in front of it. The included Nginx configuration proxies REST calls and WebSocket upgrades. Back up the `crm-data` and `crm-uploads` volumes, store `.env` outside source control, and restrict access to `credentials.json`.

For a multi-instance production service, replace file authentication, SQLite, and local attachment storage with managed identity, a server database, object storage/malware scanning, centralized secrets, migrations, and distributed SignalR.

## Architecture pointers

The complete implemented architecture, flows, entity model, endpoint inventory, security model, and Google Cloud deployment guidance are documented in [`docs/TECHNICAL-ARCHITECTURE.md`](docs/TECHNICAL-ARCHITECTURE.md).

- `backend/ItSupport.Api/Domain/Entities.cs` — tickets, memberships, bot/live sessions, histories
- `backend/ItSupport.Api/Application/Services.cs` — routing, permissions, ticket creation, agentic orchestrator, OpenAI fallback
- `backend/ItSupport.Api/Infrastructure/LiveSupportHub.cs` — authenticated realtime messaging
- `backend/ItSupport.Api/Infrastructure/SeedData.cs` — banking CTS demo configuration
- `backend/ItSupport.Api/Program.cs` — protected REST surface and role enforcement
- `frontend/src/app/live-support` — reusable live-support console
- `frontend/src/app/chat-widget` — reusable agentic assistant widget
