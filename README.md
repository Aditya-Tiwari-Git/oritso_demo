# Northstar IT Support CRM

A demo-ready IT service portal built with Angular 20, ASP.NET Core 10, EF Core, and SQLite. It includes role-aware ticketing, deterministic auto-routing, major incidents, comments/history, safe attachment handling, admin-managed catalogs/rules/knowledge, and an LLM-optional conversational support widget.

## What is included

- `User` sees and updates their own tickets; `ITSupport` works the shared queue; `Admin` also configures routing, teams, catalogs, and knowledge.
- Custom HMAC-signed 8-hour sessions. Accounts live in `backend/ItSupport.Api/credentials.json` as requested; no Identity/OAuth dependency is used.
- Ordered routing rules match category, service, priority, issue type, and optional comma-separated keywords. The final catch-all sends work to Service Desk.
- Chat searches the local knowledge base, asks for more diagnostic context, checks a user's latest ticket, and creates/reroutes a ticket on escalation.
- OpenAI Responses API is optional. With no key, the complete local KB/routing flow still works.
- SQLite, audit logs, validation, ownership checks, file type/size checks, CORS, health check, responsive UI, and container deployment.

## Local setup

Prerequisites: .NET 10 SDK and Node.js 22+ (Node 24 is tested).

1. Edit `backend/ItSupport.Api/credentials.json` to set demo users. Valid roles are exactly `Admin`, `ITSupport`, and `User`.
2. For an LLM-enabled bot, set environment variables in the API terminal. The only required external credential is `OPENAI_API_KEY`. See `.env.example` for every supported value.
3. Start the API:

   ```powershell
   dotnet run --project backend/ItSupport.Api
   ```

4. In a second terminal, start Angular:

   ```powershell
   cd frontend
   cmd /c npm install
   cmd /c npm start
   ```

5. Open `http://localhost:4200`. Seed logins are `admin / Admin@123`, `agent / Agent@123`, and `user / User@123`.

The database is created and seeded automatically at `backend/ItSupport.Api/data/itsupport.db`. Delete only that file when you intentionally want a clean demo reset. Uploaded evidence is stored under `backend/ItSupport.Api/uploads`.

## Configuration and API

Development OpenAPI JSON is at `http://localhost:5266/openapi/v1.json`; health is at `/health`. The main routes are `/api/auth/login`, `/api/tickets`, `/api/catalog`, `/api/knowledge`, `/api/chat`, and `/api/admin/*`.

The chatbot is isolated in `frontend/src/app/chat-widget`. Copy that folder plus the small API service contract into another Angular application and use `<it-chat-widget title="IT Help"></it-chat-widget>`. For non-Angular hosts, call `POST /api/chat` with `{ "message": "...", "sessionId": null, "createTicket": false }`; keep the returned `sessionId` for continuity. The host supplies a user session token, so the widget never owns authentication or CRM internals.

## Server deployment

The simplest demo deployment is Docker Compose on any Linux VM with Docker Engine and Compose v2:

1. Copy `.env.example` to `.env`, generate a long random `AUTH__SIGNINGKEY`, set the public origin in `FRONTENDURL`, and optionally add the OpenAI key.
2. Replace all sample passwords in `credentials.json` before exposing the server.
3. Run `docker compose up -d --build`.
4. Open port 8080 in the host firewall, or put HTTPS-enabled Caddy/Nginx/your cloud load balancer in front of `http://127.0.0.1:8080`.
5. Back up the named volumes `crm-data` and `crm-uploads`, and keep `.env` and `credentials.json` outside public source control.

For a production system beyond this demo, replace file passwords with a managed identity provider or password hashes, move SQLite to a managed relational database for multi-instance writes, add malware scanning/object storage for uploads, use EF migrations, add rate limiting and secret management, and run API/widget contract and end-to-end tests in CI.

## Verification

```powershell
dotnet build ItSupport.slnx
cd frontend
cmd /c npm run build
```
