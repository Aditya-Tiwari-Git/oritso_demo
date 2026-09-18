using System.Security.Claims;
using ItSupport.Api.Application;
using ItSupport.Api.Domain;
using ItSupport.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/itsupport.db"));
builder.Services.AddSingleton<CredentialStore>(); builder.Services.AddSingleton<TokenService>(); builder.Services.AddScoped<RoutingService>(); builder.Services.AddScoped<TicketService>(); builder.Services.AddScoped<ChatService>(); builder.Services.AddHttpClient();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration["FrontendUrl"] ?? "http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "data")); Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "uploads"));
app.UseCors();
app.Use(async (ctx, next) =>
{
 if (ctx.Request.Path.StartsWithSegments("/api/auth") || ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/openapi")) { await next(); return; }
 var token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase); var identity = ctx.RequestServices.GetRequiredService<TokenService>().Validate(token);
 if (identity is null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "A valid session token is required." }); return; }
 ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, identity.Value.User), new Claim(ClaimTypes.Role, identity.Value.Role), new Claim("displayName", identity.Value.Name)], "CustomToken")); await next();
});
app.Use(async (ctx, next) => { await next(); if (!ctx.Request.Path.StartsWithSegments("/api")) return; try { await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>(); db.AuditLogs.Add(new() { Actor = ctx.User.Identity?.Name ?? "anonymous", Method = ctx.Request.Method, Path = ctx.Request.Path, StatusCode = ctx.Response.StatusCode }); await db.SaveChangesAsync(); } catch { } });
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", utc = DateTimeOffset.UtcNow }));
app.MapPost("/api/auth/login", (LoginRequest request, CredentialStore users, TokenService tokens) => { var user = users.Validate(request.UserName, request.Password); if (user is null) return Results.Unauthorized(); var expires = DateTimeOffset.UtcNow.AddHours(8); return Results.Ok(new LoginResponse(tokens.Create(user, expires), user.UserName, user.DisplayName, user.Role, expires)); });

var api = app.MapGroup("/api");
api.MapGet("/catalog", async (AppDbContext db) => Results.Ok(new { assignmentGroups = await db.AssignmentGroups.Where(x => x.IsActive).ToListAsync(), categories = await db.Categories.Where(x => x.IsActive).ToListAsync(), services = await db.Services.Where(x => x.IsActive).ToListAsync(), priorities = await db.Priorities.OrderBy(x => x.Rank).ToListAsync(), statuses = await db.Statuses.OrderBy(x => x.SortOrder).ToListAsync() }));
api.MapGet("/tickets", async (HttpContext c, AppDbContext db) => { var query = db.Tickets.AsNoTracking(); if (!IsStaff(c)) query = query.Where(x => x.CreatedBy == c.User.Identity!.Name); return Results.Ok(await query.OrderByDescending(x => x.UpdatedAt).ToListAsync()); });
api.MapGet("/tickets/{id:int}", async (int id, HttpContext c, AppDbContext db) => { var t = await db.Tickets.Include(x => x.Comments).Include(x => x.History).Include(x => x.Attachments).FirstOrDefaultAsync(x => x.Id == id); return t is null || (!IsStaff(c) && t.CreatedBy != c.User.Identity!.Name) ? Results.NotFound() : Results.Ok(t); });
api.MapPost("/tickets", async (CreateTicketRequest r, HttpContext c, TicketService service) => Results.Created("/api/tickets", await service.CreateAsync(r, c.User.Identity!.Name!)));
api.MapPatch("/tickets/{id:int}", async (int id, UpdateTicketRequest r, HttpContext c, AppDbContext db) => { if (!IsStaff(c)) return Results.Forbid(); var t = await db.Tickets.FindAsync(id); if (t is null) return Results.NotFound(); if (r.Status is not null) t.Status = r.Status; if (r.Priority is not null) t.Priority = r.Priority; if (r.AssignmentGroupId.HasValue) t.AssignmentGroupId = r.AssignmentGroupId; t.UpdatedAt = DateTimeOffset.UtcNow; t.History.Add(new() { Actor = c.User.Identity!.Name!, Action = "Updated", Detail = $"Status: {t.Status}; priority: {t.Priority}; group: {t.AssignmentGroupId}" }); await db.SaveChangesAsync(); return Results.Ok(t); });
api.MapPost("/tickets/{id:int}/comments", async (int id, AddCommentRequest r, HttpContext c, AppDbContext db) => { var t = await db.Tickets.FindAsync(id); if (t is null || (!IsStaff(c) && t.CreatedBy != c.User.Identity!.Name)) return Results.NotFound(); var comment = new TicketComment { TicketId = id, Body = r.Body, Author = c.User.Identity!.Name! }; db.Add(comment); db.Add(new TicketHistory { TicketId = id, Actor = c.User.Identity!.Name!, Action = "Commented", Detail = r.Body.Length > 100 ? r.Body[..100] : r.Body }); t.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); return Results.Ok(comment); });
api.MapPost("/tickets/{id:int}/attachments", async (int id, IFormFile file, HttpContext c, AppDbContext db, IWebHostEnvironment env) => { var t = await db.Tickets.FindAsync(id); if (t is null || (!IsStaff(c) && t.CreatedBy != c.User.Identity!.Name)) return Results.NotFound(); if (file.Length == 0 || file.Length > 10_000_000) return Results.BadRequest(new { error = "Files must be between 1 byte and 10 MB." }); var allowed = new[] { "image/png", "image/jpeg", "application/pdf", "text/plain" }; if (!allowed.Contains(file.ContentType)) return Results.BadRequest(new { error = "Only PNG, JPEG, PDF, and text files are allowed." }); var stored = Guid.NewGuid().ToString("N") + Path.GetExtension(Path.GetFileName(file.FileName)); await using (var stream = File.Create(Path.Combine(env.ContentRootPath, "uploads", stored))) await file.CopyToAsync(stream); var a = new TicketAttachment { TicketId = id, OriginalName = Path.GetFileName(file.FileName), StoredName = stored, ContentType = file.ContentType, Size = file.Length }; db.Add(a); await db.SaveChangesAsync(); return Results.Ok(a); }).DisableAntiforgery();
api.MapGet("/attachments/{id:int}", async (int id, HttpContext c, AppDbContext db, IWebHostEnvironment env) => { var a = await db.TicketAttachments.FindAsync(id); if (a is null) return Results.NotFound(); var t = await db.Tickets.FindAsync(a.TicketId); if (t is null || (!IsStaff(c) && t.CreatedBy != c.User.Identity!.Name)) return Results.NotFound(); return Results.File(Path.Combine(env.ContentRootPath, "uploads", a.StoredName), a.ContentType, a.OriginalName); });
api.MapGet("/knowledge", async (string? q, AppDbContext db) => { var query = db.KnowledgeArticles.Where(x => x.IsPublished); if (!string.IsNullOrWhiteSpace(q)) query = query.Where(x => x.Title.Contains(q) || x.Content.Contains(q) || x.Keywords.Contains(q)); return Results.Ok(await query.OrderByDescending(x => x.UpdatedAt).ToListAsync()); });
api.MapPost("/chat", async (ChatRequest r, HttpContext c, ChatService chat) => Results.Ok(await chat.ReplyAsync(r, c.User.Identity!.Name!)));

var admin = api.MapGroup("/admin").AddEndpointFilter(async (context, next) => context.HttpContext.User.IsInRole("Admin") ? await next(context) : Results.Forbid());
admin.MapGet("/configuration", async (AppDbContext db) => Results.Ok(new { groups = await db.AssignmentGroups.ToListAsync(), categories = await db.Categories.ToListAsync(), services = await db.Services.ToListAsync(), priorities = await db.Priorities.OrderBy(x => x.Rank).ToListAsync(), statuses = await db.Statuses.OrderBy(x => x.SortOrder).ToListAsync(), rules = await db.RoutingRules.OrderBy(x => x.Order).ToListAsync(), articles = await db.KnowledgeArticles.ToListAsync() }));
admin.MapPost("/groups", async (NamedOptionRequest r, AppDbContext db) => { var x = new AssignmentGroup { Name = r.Name, IsActive = r.IsActive }; db.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapPost("/categories", async (NamedOptionRequest r, AppDbContext db) => { var x = new Category { Name = r.Name, IsActive = r.IsActive }; db.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapPost("/services", async (NamedOptionRequest r, AppDbContext db) => { var x = new ServiceItem { Name = r.Name, IsActive = r.IsActive }; db.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapPost("/articles", async (ArticleRequest r, AppDbContext db) => { var x = new KnowledgeArticle { Title = r.Title, Content = r.Content, Keywords = r.Keywords, Category = r.Category, IsPublished = r.IsPublished }; db.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapPost("/rules", async (RuleRequest r, AppDbContext db) => { var x = new RoutingRule { Name = r.Name, Order = r.Order, Category = r.Category, Service = r.Service, Priority = r.Priority, TicketType = r.TicketType, Keywords = r.Keywords, AssignmentGroupId = r.AssignmentGroupId, IsActive = r.IsActive }; db.Add(x); await db.SaveChangesAsync(); return Results.Ok(x); });
admin.MapDelete("/{kind}/{id:int}", async (string kind, int id, AppDbContext db) => { object? entity = kind switch { "groups" => await db.AssignmentGroups.FindAsync(id), "categories" => await db.Categories.FindAsync(id), "services" => await db.Services.FindAsync(id), "articles" => await db.KnowledgeArticles.FindAsync(id), "rules" => await db.RoutingRules.FindAsync(id), _ => null }; if (entity is null) return Results.NotFound(); db.Remove(entity); await db.SaveChangesAsync(); return Results.NoContent(); });

await using (var scope = app.Services.CreateAsyncScope()) await SeedData.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
app.Run();
static bool IsStaff(HttpContext c) => c.User.IsInRole("Admin") || c.User.IsInRole("ITSupport");
public partial class Program { }
