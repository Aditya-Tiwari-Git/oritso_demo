using ItSupport.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ItSupport.Api.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<TicketWorkNote> TicketWorkNotes => Set<TicketWorkNote>();
    public DbSet<TicketHistory> TicketHistory => Set<TicketHistory>();
    public DbSet<TicketAttachment> TicketAttachments => Set<TicketAttachment>();
    public DbSet<AssignmentGroup> AssignmentGroups => Set<AssignmentGroup>();
    public DbSet<AgentGroupMembership> AgentGroupMemberships => Set<AgentGroupMembership>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Subcategory> Subcategories => Set<Subcategory>();
    public DbSet<ServiceItem> Services => Set<ServiceItem>();
    public DbSet<PriorityOption> Priorities => Set<PriorityOption>();
    public DbSet<StatusOption> Statuses => Set<StatusOption>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<PriorityRule> PriorityRules => Set<PriorityRule>();
    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<LiveSupportSession> LiveSupportSessions => Set<LiveSupportSession>();
    public DbSet<LiveSupportMessage> LiveSupportMessages => Set<LiveSupportMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Ticket>().HasIndex(x => x.Number).IsUnique();
        b.Entity<ChatSession>().HasIndex(x => x.PublicId).IsUnique();
        b.Entity<LiveSupportSession>().HasIndex(x => x.PublicId).IsUnique();
        b.Entity<AgentGroupMembership>().HasIndex(x => new { x.UserName, x.AssignmentGroupId }).IsUnique();
        b.Entity<Category>().HasMany(x => x.Subcategories).WithOne().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Ticket>().HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Ticket>().HasMany(x => x.WorkNotes).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Ticket>().HasMany(x => x.History).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Ticket>().HasMany(x => x.Attachments).WithOne().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ChatSession>().HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.ChatSessionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LiveSupportSession>().HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.LiveSupportSessionId).OnDelete(DeleteBehavior.Cascade);
    }
}
