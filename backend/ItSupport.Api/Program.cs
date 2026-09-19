using System.Security.Claims;
using ItSupport.Api.Application;
using ItSupport.Api.Domain;
using ItSupport.Api.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddConsole();
EnvLoader.AddLocalEnv(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/itsupport.db"));
builder.Services.AddSingleton<CredentialStore>(); builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<RoutingService>(); builder.Services.AddScoped<PriorityService>(); builder.Services.AddScoped<TicketService>(); builder.Services.AddScoped<TicketAccessService>(); builder.Services.AddScoped<ChatService>();
builder.Services.AddHttpClient(); builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration["FrontendUrl"] ?? "http://localhost:4200").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data")); Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "uploads"));
app.UseCors();
app.Use(async (ctx, next) =>
{
    var isPublic = ctx.Request.Path == "/" || ctx.Request.Path.StartsWithSegments("/api/auth") || ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/openapi");
    if (isPublic) { await next(); return; }
    var token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(token) && ctx.Request.Path.StartsWithSegments("/hubs")) token = ctx.Request.Query["access_token"].ToString();
    var identity = ctx.RequestServices.GetRequiredService<TokenService>().Validate(token);
    if (identity is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "A valid, non-expired session token is required." }); return; }
    ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, identity.Value.User), new Claim(ClaimTypes.Role, identity.Value.Role), new Claim("displayName", identity.Value.Name)], "CustomToken"));
    await next();
});
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) { app.Logger.LogError(ex, "Unhandled request failure for {Method} {Path}", ctx.Request.Method, ctx.Request.Path); if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsJsonAsync(new { error = "The request could not be completed." }); } }
    if (!ctx.Request.Path.StartsWithSegments("/api")) return;
    try { await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); db.AuditLogs.Add(new() { Actor = ctx.User.Identity?.Name ?? "anonymous", Method = ctx.Request.Method, Path = ctx.Request.Path, StatusCode = ctx.Response.StatusCode }); await db.SaveChangesAsync(); } catch { }
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapGet("/", () => Results.Ok(new { service = "Oritso IT Support CRM API", status = "running", version = "2.1", health = "/health" }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTimeOffset.UtcNow }));
app.MapPost("/api/auth/login", (LoginRequest request, CredentialStore users, TokenService tokens, IConfiguration config) => { var user = users.Validate(request.UserName, request.Password); if (user is null) return Results.Unauthorized(); var hours = double.TryParse(config["Auth:SessionHours"], out var configured) ? configured : 8; var expires = DateTimeOffset.UtcNow.AddHours(hours); return Results.Ok(new LoginResponse(tokens.Create(user, expires), user.UserName, user.DisplayName, user.Email, user.Role, expires)); });

var api = app.MapGroup("/api");
api.MapGet("/me", async (HttpContext c, AppDbContext db) => Results.Ok(new { userName = c.User.Identity!.Name, displayName = c.User.FindFirst("displayName")?.Value, role = c.User.FindFirst(ClaimTypes.Role)?.Value, assignmentGroupIds = await db.AgentGroupMemberships.Where(x => x.UserName == c.User.Identity!.Name).Select(x => x.AssignmentGroupId).ToArrayAsync() }));
api.MapGet("/catalog", async (AppDbContext db) => Results.Ok(new { assignmentGroups = await db.AssignmentGroups.Where(x => x.IsActive).ToListAsync(), categories = await db.Categories.Include(x => x.Subcategories.Where(s => s.IsActive)).Where(x => x.IsActive).ToListAsync(), services = await db.Services.Where(x => x.IsActive).ToListAsync(), priorities = await db.Priorities.OrderBy(x => x.Rank).ToListAsync(), statuses = await db.Statuses.OrderBy(x => x.SortOrder).ToListAsync() }));

api.MapGet("/tickets", async (HttpContext c, AppDbContext db, TicketAccessService access) =>
{
    var role = Role(c); var user = UserName(c); var query = db.Tickets.AsNoTracking();
    if (role == "User") query = query.Where(x => x.CreatedBy == user);
    else if (role == "ITSupport") { var groups = await access.GroupIdsAsync(user); query = query.Where(x => x.AssignedAgent == user || x.AssignmentGroupId.HasValue && groups.Contains(x.AssignmentGroupId.Value)); }
    return Results.Ok((await query.ToListAsync()).OrderByDescending(x => x.UpdatedAt));
});
api.MapGet("/tickets/{id:int}", async (int id, HttpContext c, AppDbContext db, TicketAccessService access, CredentialStore credentials) =>
{
    var t = await db.Tickets.Include(x => x.Comments).Include(x => x.WorkNotes).Include(x => x.History).Include(x => x.Attachments).FirstOrDefaultAsync(x => x.Id == id);
    if (t is null || !await access.CanViewAsync(t, UserName(c), Role(c))) return Results.NotFound();
    var creator = credentials.Load().FirstOrDefault(x => x.UserName.Equals(t.CreatedBy, StringComparison.OrdinalIgnoreCase));
    return Results.Ok(TicketView(t, IsStaff(c), creator));
});
api.MapPost("/tickets", async (CreateTicketRequest r, HttpContext c, TicketService service) =>
{
    if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Description) || string.IsNullOrWhiteSpace(r.Category) || string.IsNullOrWhiteSpace(r.Subcategory)) return Results.BadRequest(new { error = "Title, description, category, and subcategory are required." });
    try { var result = await service.CreateAsync(r, UserName(c)); return Results.Created($"/api/tickets/{result.Ticket.Id}", result.Ticket); } catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
api.MapPost("/tickets/{id:int}/accept", async (int id, HttpContext c, AppDbContext db, TicketAccessService access) =>
{
    if (!IsStaff(c)) return Results.StatusCode(403); var t = await db.Tickets.FindAsync(id); if (t is null || !await access.CanViewAsync(t, UserName(c), Role(c))) return Results.NotFound();
    if (t.AssignedAgent is not null && t.AssignedAgent != UserName(c) && Role(c) != "Admin") return Results.Conflict(new { error = $"Ticket is already assigned to {t.AssignedAgent}." });
    t.AssignedAgent = UserName(c); if (t.Status == "New") t.Status = "Assigned"; Touch(t); AddHistory(db, t, c, "Agent Assigned", $"Accepted by {UserName(c)}."); await db.SaveChangesAsync(); return Results.Ok(t);
});
api.MapPatch("/tickets/{id:int}", async (int id, UpdateTicketRequest r, HttpContext c, AppDbContext db, TicketAccessService access) =>
{
    if (!IsStaff(c)) return Results.StatusCode(403); var t = await db.Tickets.FindAsync(id); if (t is null || !await access.CanViewAsync(t, UserName(c), Role(c))) return Results.NotFound();
    if (r.Status is not null && r.Status != t.Status) { AddHistory(db, t, c, "Status Changed", $"{t.Status} → {r.Status}"); t.Status = r.Status; }
    if (r.Priority is not null && r.Priority != t.Priority) { AddHistory(db, t, c, "Priority Changed", $"{t.Priority} → {r.Priority}"); t.Priority = r.Priority; }
    if (r.AssignmentGroupId.HasValue && r.AssignmentGroupId != t.AssignmentGroupId) { var old = await GroupName(db, t.AssignmentGroupId); var next = await GroupName(db, r.AssignmentGroupId); AddHistory(db, t, c, "Assignment Group Changed", $"{old} → {next}"); t.AssignmentGroupId = r.AssignmentGroupId; t.AssignedAgent = null; }
    if (r.AssignedAgent is not null && r.AssignedAgent != t.AssignedAgent) { AddHistory(db, t, c, "Agent Assignment Changed", $"{t.AssignedAgent ?? "Unassigned"} → {r.AssignedAgent}"); t.AssignedAgent = r.AssignedAgent; }
    if (r.Impact is not null) t.Impact = r.Impact; if (r.Urgency is not null) t.Urgency = r.Urgency;
    if (r.ParentMajorIncidentId.HasValue) { t.ParentMajorIncidentId = r.ParentMajorIncidentId; AddHistory(db, t, c, "Linked to Major Incident", $"Linked to ticket ID {r.ParentMajorIncidentId}."); }
    if (r.Escalate == true) { t.IsEscalated = true; AddHistory(db, t, c, "Escalated", "Ticket escalated for additional support."); }
    if (r.ResolutionCode is not null || r.ResolutionNotes is not null) { if (string.IsNullOrWhiteSpace(r.ResolutionNotes)) return Results.BadRequest(new { error = "Resolution notes are required when resolving a ticket." }); t.ResolutionCode = r.ResolutionCode; t.ResolutionNotes = r.ResolutionNotes; t.Status = "Resolved"; t.ResolvedAt = DateTimeOffset.UtcNow; AddHistory(db, t, c, "Resolved", $"{r.ResolutionCode}: {r.ResolutionNotes}"); }
    Touch(t); await db.SaveChangesAsync(); return Results.Ok(t);
});
api.MapPost("/tickets/{id:int}/comments", async (int id, AddCommentRequest r, HttpContext c, AppDbContext db, TicketAccessService access) =>
{
    var t = await db.Tickets.FindAsync(id); if (t is null || !await access.CanViewAsync(t, UserName(c), Role(c))) return Results.NotFound(); if (string.IsNullOrWhiteSpace(r.Body)) return Results.BadRequest(new { error = "Comment is required." });
    var item = new TicketComment { TicketId = id, Body = r.Body.Trim(), Author = UserName(c) }; db.Add(item); AddHistory(db, t, c, "Public Comment Added", Summarize(r.Body)); Touch(t); await db.SaveChangesAsync(); return Results.Ok(item);
});
api.MapPost("/tickets/{id:int}/work-notes", async (int id, AddWorkNoteRequest r, HttpContext c, AppDbContext db, TicketAccessService access) =>
{
    if (!IsStaff(c)) return Results.StatusCode(403); var t = await db.Tickets.FindAsync(id); if (t is null || !await access.CanViewAsync(t, UserName(c), Role(c))) return Results.NotFound();
    var item = new TicketWorkNote { TicketId = id, Body = r.Body.Trim(), Author = UserName(c) }; db.Add(item); AddHistory(db, t, c, "Internal Work Note Added", "Internal troubleshooting/investigation note recorded."); Touch(t); await db.SaveChangesAsync(); return Results.Ok(item);
});
api.MapPost("/tickets/{id:int}/attachments", async (int id, IFormFile file, HttpContext c, AppDbContext db, TicketAccessService access, IWebHostEnvironment env) =>
{
    var t = await db.Tickets.FindAsync(id); if (t is null || !await access.CanViewAsync(t, UserName(c), Role(c))) return Results.NotFound(); if (file.Length == 0 || file.Length > 10_000_000) return Results.BadRequest(new { error = "Files must be between 1 byte and 10 MB." });
    var allowed = new[] { "image/png", "image/jpeg", "application/pdf", "text/plain", "application/zip" }; if (!allowed.Contains(file.ContentType)) return Results.BadRequest(new { error = "Only PNG, JPEG, PDF, text, and ZIP files are allowed." });
    var extension = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant(); var stored = Guid.NewGuid().ToString("N") + extension; var fullPath = Path.GetFullPath(Path.Combine(env.ContentRootPath, "uploads", stored)); var uploadRoot = Path.GetFullPath(Path.Combine(env.ContentRootPath, "uploads")); if (!fullPath.StartsWith(uploadRoot, StringComparison.OrdinalIgnoreCase)) return Results.BadRequest();
    await using (var stream = File.Create(fullPath)) await file.CopyToAsync(stream); var item = new TicketAttachment { TicketId = id, OriginalName = Path.GetFileName(file.FileName), StoredName = stored, ContentType = file.ContentType, Size = file.Length }; db.Add(item); AddHistory(db, t, c, "Attachment Added", item.OriginalName); Touch(t); await db.SaveChangesAsync(); return Results.Ok(item);
}).DisableAntiforgery();
api.MapGet("/attachments/{id:int}", async (int id, HttpContext c, AppDbContext db, TicketAccessService access, IWebHostEnvironment env) => { var a = await db.TicketAttachments.FindAsync(id); if (a is null) return Results.NotFound(); var t = await db.Tickets.FindAsync(a.TicketId); if (t is null || !await access.CanViewAsync(t, UserName(c), Role(c))) return Results.NotFound(); return Results.File(Path.Combine(env.ContentRootPath, "uploads", a.StoredName), a.ContentType, a.OriginalName); });
api.MapGet("/knowledge", async (string? q, AppDbContext db) => { var query = db.KnowledgeArticles.Where(x => x.IsPublished); if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Title.Contains(q) || x.Content.Contains(q) || x.Keywords.Contains(q)); return Results.Ok(await query.OrderBy(x => x.Category).ThenBy(x => x.Title).ToListAsync()); });
api.MapPost("/chat", async (ChatRequest r, HttpContext c, ChatService chat) => Results.Ok(await chat.ReplyAsync(r, UserName(c))));
api.MapPost("/chat/tickets", async (BotTicketRequest r, HttpContext c, ChatService chat) => { try { var result = await chat.CreateTicketAsync(r, UserName(c)); return result is null ? Results.NotFound(new { error = "Chat session not found." }) : Results.Ok(result); } catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); } });

api.MapPost("/live-support", async (LiveSupportRequest r, HttpContext c, AppDbContext db, IHubContext<LiveSupportHub> hub) => { var item = new LiveSupportSession { RequestedBy = UserName(c), Subject = string.IsNullOrWhiteSpace(r.Subject) ? "Live support request" : r.Subject.Trim(), TicketId = r.TicketId }; item.Messages.Add(new() { Sender = "System", Body = "User entered the live support queue." }); db.Add(item); await db.SaveChangesAsync(); await hub.Clients.All.SendAsync("QueueChanged"); return Results.Ok(item); });
api.MapGet("/live-support", async (HttpContext c, AppDbContext db) => { var query = db.LiveSupportSessions.AsNoTracking(); if (!IsStaff(c)) { var user = UserName(c); query = query.Where(x => x.RequestedBy == user); } return Results.Ok((await query.ToListAsync()).OrderByDescending(x => x.UpdatedAt)); });
api.MapGet("/live-support/{publicId:guid}", async (Guid publicId, HttpContext c, AppDbContext db, CredentialStore credentials) => { var item = await db.LiveSupportSessions.Include(x => x.Messages).FirstOrDefaultAsync(x => x.PublicId == publicId); if (item is null || (!IsStaff(c) && item.RequestedBy != UserName(c))) return Results.NotFound(); item.Messages = item.Messages.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList(); var agentName = item.AcceptedBy is null ? null : credentials.Load().FirstOrDefault(x => x.UserName.Equals(item.AcceptedBy, StringComparison.OrdinalIgnoreCase))?.DisplayName; return Results.Ok(new { item.Id, item.PublicId, item.RequestedBy, item.AcceptedBy, agentDisplayName = agentName, item.Status, item.Subject, item.TicketId, item.CreatedAt, item.UpdatedAt, item.EndedAt, item.Messages }); });
api.MapPost("/live-support/{publicId:guid}/messages", async (Guid publicId, LiveMessageRequest r, HttpContext c, AppDbContext db, IHubContext<LiveSupportHub> hub) => { var item = await db.LiveSupportSessions.FirstOrDefaultAsync(x => x.PublicId == publicId); if (item is null || item.Status == "Ended" || (!IsStaff(c) && item.RequestedBy != UserName(c))) return Results.NotFound(); var message = new LiveSupportMessage { LiveSupportSessionId = item.Id, Sender = UserName(c), Body = r.Body.Trim() }; db.Add(message); item.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); await hub.Clients.Group(publicId.ToString()).SendAsync("MessageReceived", new { message.Id, message.Sender, message.Body, message.CreatedAt }); return Results.Ok(message); });
api.MapPost("/live-support/{publicId:guid}/accept", async (Guid publicId, HttpContext c, AppDbContext db, IHubContext<LiveSupportHub> hub, CredentialStore credentials) => { if (!IsStaff(c)) return Results.StatusCode(403); var item = await db.LiveSupportSessions.FirstOrDefaultAsync(x => x.PublicId == publicId); if (item is null) return Results.NotFound(); if (item.Status != "Waiting") return Results.Conflict(new { error = "Conversation is no longer waiting." }); item.Status = "Active"; item.AcceptedBy = UserName(c); item.UpdatedAt = DateTimeOffset.UtcNow; var agentName = credentials.Load().FirstOrDefault(x => x.UserName.Equals(item.AcceptedBy, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? item.AcceptedBy; db.LiveSupportMessages.Add(new() { LiveSupportSessionId = item.Id, Sender = "System", Body = $"Connected to {agentName}." }); await db.SaveChangesAsync(); var update = new { item.PublicId, item.Status, item.AcceptedBy, agentDisplayName = agentName, item.UpdatedAt }; await hub.Clients.Group(publicId.ToString()).SendAsync("SessionUpdated", update); await hub.Clients.All.SendAsync("QueueChanged"); return Results.Ok(update); });
api.MapPost("/live-support/{publicId:guid}/end", async (Guid publicId, HttpContext c, AppDbContext db, IHubContext<LiveSupportHub> hub) => { var item = await db.LiveSupportSessions.FirstOrDefaultAsync(x => x.PublicId == publicId); if (item is null || (!IsStaff(c) && item.RequestedBy != UserName(c))) return Results.NotFound(); item.Status = "Ended"; item.EndedAt = item.UpdatedAt = DateTimeOffset.UtcNow; db.LiveSupportMessages.Add(new() { LiveSupportSessionId = item.Id, Sender = "System", Body = "Conversation ended." }); await db.SaveChangesAsync(); await hub.Clients.Group(publicId.ToString()).SendAsync("SessionUpdated", item); await hub.Clients.All.SendAsync("QueueChanged"); return Results.Ok(item); });
api.MapPost("/live-support/{publicId:guid}/ticket", async (Guid publicId, LiveLinkTicketRequest r, HttpContext c, AppDbContext db, TicketService tickets) => { if (!IsStaff(c)) return Results.StatusCode(403); var live = await db.LiveSupportSessions.Include(x => x.Messages).FirstOrDefaultAsync(x => x.PublicId == publicId); if (live is null) return Results.NotFound(); if (r.TicketId.HasValue) live.TicketId = r.TicketId; else if (r.CreateTicket) { var transcript = string.Join("\n", live.Messages.Select(x => $"[{x.CreatedAt:u}] {x.Sender}: {x.Body}")); var description = (r.Description ?? live.Subject) + "\n\nLive chat transcript:\n" + transcript; if (description.Length > 4000) description = description[..4000]; try { var created = await tickets.CreateAsync(new("Incident", r.Title ?? live.Subject, description, "CTS Hardware", "Connectivity", null, "CTS Scanner", "Single User", "Medium"), live.RequestedBy, "Live Support"); live.TicketId = created.Ticket.Id; } catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); } } await db.SaveChangesAsync(); return Results.Ok(live); });

var admin = api.MapGroup("/admin").AddEndpointFilter(async (ctx, next) => ctx.HttpContext.User.IsInRole("Admin") ? await next(ctx) : Results.StatusCode(403));
admin.MapGet("/configuration", async (AppDbContext db) => Results.Ok(new { groups = await db.AssignmentGroups.ToListAsync(), memberships = await db.AgentGroupMemberships.ToListAsync(), categories = await db.Categories.Include(x => x.Subcategories).ToListAsync(), services = await db.Services.ToListAsync(), priorities = await db.Priorities.OrderBy(x => x.Rank).ToListAsync(), statuses = await db.Statuses.OrderBy(x => x.SortOrder).ToListAsync(), rules = await db.RoutingRules.OrderBy(x => x.Order).ToListAsync(), priorityRules = await db.PriorityRules.OrderBy(x => x.Order).ThenBy(x => x.Id).ToListAsync(), articles = await db.KnowledgeArticles.ToListAsync() }));
admin.MapGet("/users", (CredentialStore credentials) => Results.Ok(credentials.Load().Select(x => new { x.UserName, x.DisplayName, x.Email, x.Role, x.IsActive })));
admin.MapPost("/users", (UserAdminRequest request, CredentialStore credentials) => { try { var user = credentials.Upsert(request); return Results.Ok(new { user.UserName, user.DisplayName, user.Email, user.Role, user.IsActive }); } catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); } });
admin.MapPut("/memberships/{userName}", async (string userName, MembershipRequest r, AppDbContext db, CredentialStore credentials) => { if (!credentials.Load().Any(x => x.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase) && x.Role == "ITSupport")) return Results.BadRequest(new { error = "Only ITSupport users can receive assignment groups." }); var old = await db.AgentGroupMemberships.Where(x => x.UserName == userName).ToListAsync(); db.RemoveRange(old); db.AgentGroupMemberships.AddRange(r.AssignmentGroupIds.Distinct().Select(id => new AgentGroupMembership { UserName = userName, AssignmentGroupId = id })); await db.SaveChangesAsync(); return Results.NoContent(); });
admin.MapPost("/{kind}", async (string kind, NamedOptionRequest r, AppDbContext db) => { object? entity = kind switch { "groups" => new AssignmentGroup { Name = r.Name, IsActive = r.IsActive }, "categories" => new Category { Name = r.Name, IsActive = r.IsActive }, "subcategories" when r.CategoryId.HasValue => new Subcategory { Name = r.Name, CategoryId = r.CategoryId.Value, IsActive = r.IsActive }, "services" => new ServiceItem { Name = r.Name, IsActive = r.IsActive }, "priorities" => new PriorityOption { Name = r.Name, Rank = 99 }, "statuses" => new StatusOption { Name = r.Name, SortOrder = 99 }, _ => null }; if (entity is null) return Results.BadRequest(); db.Add(entity); await db.SaveChangesAsync(); return Results.Ok(entity); });
admin.MapPost("/articles", async (ArticleRequest r, AppDbContext db) => { var x = new KnowledgeArticle { Title = r.Title, Content = r.Content, Keywords = r.Keywords, Category = r.Category, Subcategory = r.Subcategory, IsPublished = r.IsPublished }; db.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapPost("/rules", async (RuleRequest r, AppDbContext db) => { var x = new RoutingRule { Name = r.Name, Order = r.Order, Category = r.Category, Subcategory = r.Subcategory, Service = r.Service, Priority = r.Priority, TicketType = r.TicketType, Keywords = r.Keywords, AssignmentGroupId = r.AssignmentGroupId, IsActive = r.IsActive }; db.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapPost("/priority-rules", async (PriorityRuleRequest r, AppDbContext db) => { if (!await db.Priorities.AnyAsync(x => x.Name == r.Priority)) return Results.BadRequest(new { error = "Select a configured priority." }); var x = new PriorityRule { Name = r.Name.Trim(), Description = r.Description.Trim(), Keywords = r.Keywords?.Trim(), Category = r.Category, Service = r.Service, Priority = r.Priority, Order = r.Order, IsActive = r.IsActive }; db.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapPut("/priority-rules/{id:int}", async (int id, PriorityRuleRequest r, AppDbContext db) => { var x = await db.PriorityRules.FindAsync(id); if (x is null) return Results.NotFound(); if (!await db.Priorities.AnyAsync(p => p.Name == r.Priority)) return Results.BadRequest(new { error = "Select a configured priority." }); x.Name = r.Name.Trim(); x.Description = r.Description.Trim(); x.Keywords = r.Keywords?.Trim(); x.Category = r.Category; x.Service = r.Service; x.Priority = r.Priority; x.Order = r.Order; x.IsActive = r.IsActive; await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapGet("/bot-status", (IConfiguration config) => Results.Ok(new { enabled = !string.Equals(config["OPENAI_ENABLED"], "false", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(config["OPENAI_API_KEY"]), model = config["OPENAI_MODEL"] ?? "gpt-5-mini", keyExposed = false }));
admin.MapGet("/deletion-inventory", async (AppDbContext db) => Results.Ok(new {
    tickets = (await db.Tickets.AsNoTracking().ToListAsync()).OrderByDescending(x => x.UpdatedAt).Select(x => new { x.Id, x.Number, x.Title, x.Status, x.CreatedBy, x.UpdatedAt }),
    chatSessions = (await db.ChatSessions.AsNoTracking().Include(x => x.Messages).ToListAsync()).OrderByDescending(x => x.UpdatedAt).Select(x => new { x.Id, x.PublicId, x.UserName, x.TicketId, messageCount = x.Messages.Count, x.UpdatedAt }),
    liveSessions = (await db.LiveSupportSessions.AsNoTracking().Include(x => x.Messages).ToListAsync()).OrderByDescending(x => x.UpdatedAt).Select(x => new { x.Id, x.PublicId, x.Subject, x.RequestedBy, x.AcceptedBy, x.Status, x.TicketId, messageCount = x.Messages.Count, x.UpdatedAt })
}));
admin.MapDelete("/tickets/{id:int}", async (int id, HttpContext c, AppDbContext db, IWebHostEnvironment env) => {
    var ticket = await db.Tickets.Include(x => x.Attachments).FirstOrDefaultAsync(x => x.Id == id); if (ticket is null) return Results.NotFound(new { error = "Ticket not found." });
    var attachments = ticket.Attachments.Select(x => x.StoredName).Distinct().ToArray();
    foreach (var session in await db.ChatSessions.Where(x => x.TicketId == id).ToListAsync()) session.TicketId = null;
    foreach (var session in await db.LiveSupportSessions.Where(x => x.TicketId == id).ToListAsync()) session.TicketId = null;
    foreach (var related in await db.Tickets.Where(x => x.ParentMajorIncidentId == id).ToListAsync()) related.ParentMajorIncidentId = null;
    db.AuditLogs.Add(new() { Actor = UserName(c), Method = "DELETE", Path = $"Admin deleted ticket {ticket.Number}; cascaded comments, work notes, history and {ticket.Attachments.Count} attachment record(s).", StatusCode = 204 });
    db.Tickets.Remove(ticket); await db.SaveChangesAsync();
    var uploadRoot = Path.GetFullPath(Path.Combine(env.ContentRootPath, "uploads"));
    foreach (var storedName in attachments) { if (await db.TicketAttachments.AnyAsync(x => x.StoredName == storedName)) continue; var path = Path.GetFullPath(Path.Combine(uploadRoot, storedName)); if (path.StartsWith(uploadRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) File.Delete(path); }
    return Results.NoContent();
});
admin.MapDelete("/chat-sessions/{id:int}", async (int id, HttpContext c, AppDbContext db) => { var session = await db.ChatSessions.Include(x => x.Messages).FirstOrDefaultAsync(x => x.Id == id); if (session is null) return Results.NotFound(new { error = "Chat session not found." }); var count = session.Messages.Count; db.AuditLogs.Add(new() { Actor = UserName(c), Method = "DELETE", Path = $"Admin deleted chatbot session {session.PublicId} for {session.UserName} with {count} message(s).", StatusCode = 204 }); db.ChatSessions.Remove(session); await db.SaveChangesAsync(); return Results.NoContent(); });
admin.MapDelete("/live-sessions/{id:int}", async (int id, HttpContext c, AppDbContext db, IHubContext<LiveSupportHub> hub) => { var session = await db.LiveSupportSessions.Include(x => x.Messages).FirstOrDefaultAsync(x => x.Id == id); if (session is null) return Results.NotFound(new { error = "Live support session not found." }); var publicId = session.PublicId; var count = session.Messages.Count; db.AuditLogs.Add(new() { Actor = UserName(c), Method = "DELETE", Path = $"Admin deleted live session {publicId} for {session.RequestedBy} with {count} message(s).", StatusCode = 204 }); db.LiveSupportSessions.Remove(session); await db.SaveChangesAsync(); await hub.Clients.Group(publicId.ToString()).SendAsync("SessionDeleted", new { publicId }); await hub.Clients.All.SendAsync("QueueChanged"); return Results.NoContent(); });
admin.MapDelete("/{kind}/{id:int}", async (string kind, int id, AppDbContext db) => { object? entity = kind switch { "groups" => await db.AssignmentGroups.FindAsync(id), "categories" => await db.Categories.FindAsync(id), "subcategories" => await db.Subcategories.FindAsync(id), "services" => await db.Services.FindAsync(id), "priorities" => await db.Priorities.FindAsync(id), "statuses" => await db.Statuses.FindAsync(id), "articles" => await db.KnowledgeArticles.FindAsync(id), "rules" => await db.RoutingRules.FindAsync(id), "priority-rules" => await db.PriorityRules.FindAsync(id), _ => null }; if (entity is null) return Results.NotFound(); db.Remove(entity); await db.SaveChangesAsync(); return Results.NoContent(); });

app.MapHub<LiveSupportHub>("/hubs/support");
await using (var scope = app.Services.CreateAsyncScope()) await SeedData.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
app.Run();

static string UserName(HttpContext c) => c.User.Identity!.Name!;
static string Role(HttpContext c) => c.User.FindFirst(ClaimTypes.Role)?.Value ?? "User";
static bool IsStaff(HttpContext c) => Role(c) is "Admin" or "ITSupport";
static void Touch(Ticket t) => t.UpdatedAt = DateTimeOffset.UtcNow;
static void AddHistory(AppDbContext db, Ticket t, HttpContext c, string action, string detail) => db.TicketHistory.Add(new() { TicketId = t.Id, Actor = UserName(c), Action = action, Detail = detail });
static string Summarize(string text) => text.Length > 180 ? text[..180] + "…" : text;
static async Task<string> GroupName(AppDbContext db, int? id) => id is null ? "Unassigned" : (await db.AssignmentGroups.FindAsync(id))?.Name ?? "Unknown";
static object TicketView(Ticket t, bool includeInternal, AppUser? creator) => new { t.Id, t.Number, t.Type, t.Title, t.Description, t.Category, t.Subcategory, t.Service, t.Priority, t.Impact, t.Urgency, t.Status, t.AssignmentGroupId, t.AssignedAgent, t.CreatedBy, createdByDisplayName = creator?.DisplayName ?? t.CreatedBy, createdByEmail = creator?.Email ?? "", t.ResolutionCode, t.ResolutionNotes, t.IsEscalated, t.ParentMajorIncidentId, t.CreatedAt, t.UpdatedAt, t.ResolvedAt, t.Comments, workNotes = includeInternal ? t.WorkNotes : [], t.History, t.Attachments };
public partial class Program { }
