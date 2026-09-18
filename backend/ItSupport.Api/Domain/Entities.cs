using System.ComponentModel.DataAnnotations;

namespace ItSupport.Api.Domain;

public abstract class Entity { public int Id { get; set; } }

public class AssignmentGroup : Entity
{
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(240)] public string Description { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class AgentGroupMembership : Entity
{
    [Required, MaxLength(80)] public string UserName { get; set; } = "";
    public int AssignmentGroupId { get; set; }
}

public class Category : Entity
{
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public List<Subcategory> Subcategories { get; set; } = [];
}

public class Subcategory : Entity
{
    public int CategoryId { get; set; }
    [Required, MaxLength(80)] public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class ServiceItem : Entity { [Required, MaxLength(80)] public string Name { get; set; } = ""; public bool IsActive { get; set; } = true; }
public class PriorityOption : Entity { [Required, MaxLength(40)] public string Name { get; set; } = ""; public int Rank { get; set; } }
public class StatusOption : Entity { [Required, MaxLength(40)] public string Name { get; set; } = ""; public int SortOrder { get; set; } }

public class RoutingRule : Entity
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    public int Order { get; set; }
    [MaxLength(80)] public string? Category { get; set; }
    [MaxLength(80)] public string? Subcategory { get; set; }
    [MaxLength(80)] public string? Service { get; set; }
    [MaxLength(40)] public string? Priority { get; set; }
    [MaxLength(40)] public string? TicketType { get; set; }
    [MaxLength(240)] public string? Keywords { get; set; }
    public int AssignmentGroupId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Ticket : Entity
{
    [Required, MaxLength(24)] public string Number { get; set; } = "";
    [Required, MaxLength(30)] public string Type { get; set; } = "Incident";
    [Required, MaxLength(140)] public string Title { get; set; } = "";
    [Required, MaxLength(4000)] public string Description { get; set; } = "";
    [Required, MaxLength(80)] public string Category { get; set; } = "General";
    [MaxLength(80)] public string Subcategory { get; set; } = "General";
    [Required, MaxLength(80)] public string Service { get; set; } = "Other";
    [Required, MaxLength(40)] public string Priority { get; set; } = "Medium";
    [MaxLength(40)] public string Impact { get; set; } = "Single User";
    [MaxLength(40)] public string Urgency { get; set; } = "Medium";
    [Required, MaxLength(40)] public string Status { get; set; } = "New";
    public int? AssignmentGroupId { get; set; }
    [MaxLength(80)] public string? AssignedAgent { get; set; }
    [Required, MaxLength(80)] public string CreatedBy { get; set; } = "";
    [MaxLength(80)] public string? ResolutionCode { get; set; }
    [MaxLength(4000)] public string? ResolutionNotes { get; set; }
    public bool IsEscalated { get; set; }
    public int? ParentMajorIncidentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public List<TicketComment> Comments { get; set; } = [];
    public List<TicketWorkNote> WorkNotes { get; set; } = [];
    public List<TicketHistory> History { get; set; } = [];
    public List<TicketAttachment> Attachments { get; set; } = [];
}

public class TicketComment : Entity { public int TicketId { get; set; } [Required, MaxLength(2000)] public string Body { get; set; } = ""; [Required, MaxLength(80)] public string Author { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public class TicketWorkNote : Entity { public int TicketId { get; set; } [Required, MaxLength(4000)] public string Body { get; set; } = ""; [Required, MaxLength(80)] public string Author { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public class TicketHistory : Entity { public int TicketId { get; set; } [Required, MaxLength(80)] public string Actor { get; set; } = ""; [Required, MaxLength(80)] public string Action { get; set; } = ""; [MaxLength(1000)] public string Detail { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public class TicketAttachment : Entity { public int TicketId { get; set; } [Required, MaxLength(200)] public string OriginalName { get; set; } = ""; [Required, MaxLength(200)] public string StoredName { get; set; } = ""; [MaxLength(100)] public string ContentType { get; set; } = "application/octet-stream"; public long Size { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }

public class KnowledgeArticle : Entity
{
    [Required, MaxLength(160)] public string Title { get; set; } = "";
    [Required, MaxLength(6000)] public string Content { get; set; } = "";
    [MaxLength(300)] public string Keywords { get; set; } = "";
    [MaxLength(80)] public string Category { get; set; } = "General";
    [MaxLength(80)] public string Subcategory { get; set; } = "General";
    public bool IsPublished { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ChatSession : Entity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    [Required, MaxLength(80)] public string UserName { get; set; } = "";
    public int? TicketId { get; set; }
    [MaxLength(10000)] public string StateJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ChatMessage> Messages { get; set; } = [];
}

public class ChatMessage : Entity { public int ChatSessionId { get; set; } [Required, MaxLength(20)] public string Role { get; set; } = "user"; [Required, MaxLength(6000)] public string Content { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }

public class LiveSupportSession : Entity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    [Required, MaxLength(80)] public string RequestedBy { get; set; } = "";
    [MaxLength(80)] public string? AcceptedBy { get; set; }
    [Required, MaxLength(20)] public string Status { get; set; } = "Waiting";
    [MaxLength(500)] public string Subject { get; set; } = "Live support request";
    public int? TicketId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public List<LiveSupportMessage> Messages { get; set; } = [];
}

public class LiveSupportMessage : Entity { public int LiveSupportSessionId { get; set; } [Required, MaxLength(80)] public string Sender { get; set; } = ""; [Required, MaxLength(2000)] public string Body { get; set; } = ""; public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
public class AuditLog : Entity { [Required, MaxLength(80)] public string Actor { get; set; } = ""; [Required, MaxLength(20)] public string Method { get; set; } = ""; [Required, MaxLength(300)] public string Path { get; set; } = ""; public int StatusCode { get; set; } public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow; }
