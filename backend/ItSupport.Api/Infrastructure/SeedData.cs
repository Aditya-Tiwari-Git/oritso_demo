using ItSupport.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ItSupport.Api.Infrastructure;

public static class SeedData
{
    public static async Task InitializeAsync(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PriorityRules" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PriorityRules" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "Description" TEXT NOT NULL DEFAULT '',
                "Keywords" TEXT NULL,
                "Category" TEXT NULL,
                "Service" TEXT NULL,
                "Priority" TEXT NOT NULL,
                "Order" INTEGER NOT NULL,
                "IsActive" INTEGER NOT NULL
            );
            """);
        if (await db.AssignmentGroups.AnyAsync())
        {
            await SeedEnhancementsAsync(db);
            return;
        }

        var serviceDesk = new AssignmentGroup { Name = "Service Desk", Description = "First-line intake and triage" };
        var cts = new AssignmentGroup { Name = "CTS Hardware Support", Description = "Cheque Truncation System scanners and devices" };
        var cbs = new AssignmentGroup { Name = "CBS Support", Description = "Core Banking System integration and dispatch" };
        db.AssignmentGroups.AddRange(serviceDesk, cts, cbs);

        var hardware = new Category { Name = "CTS Hardware", Subcategories = [new() { Name = "Scanner Jam" }, new() { Name = "Connectivity" }] };
        var integration = new Category { Name = "CBS Integration", Subcategories = [new() { Name = "Catch & Dispatch" }] };
        var general = new Category { Name = "General", Subcategories = [new() { Name = "Software" }] };
        db.Categories.AddRange(hardware, integration, general);
        db.Services.AddRange([new() { Name = "CTS Scanner" }, new() { Name = "CTS / CBS Integration" }, new() { Name = "Branch IT" }]);
        db.Priorities.AddRange([new() { Name = "Critical", Rank = 1 }, new() { Name = "High", Rank = 2 }, new() { Name = "Medium", Rank = 3 }, new() { Name = "Low", Rank = 4 }]);
        db.Statuses.AddRange([new() { Name = "New", SortOrder = 1 }, new() { Name = "Assigned", SortOrder = 2 }, new() { Name = "In Progress", SortOrder = 3 }, new() { Name = "Pending", SortOrder = 4 }, new() { Name = "Resolved", SortOrder = 5 }, new() { Name = "Closed", SortOrder = 6 }]);
        await db.SaveChangesAsync();

        db.AgentGroupMemberships.AddRange([new() { UserName = "rahul", AssignmentGroupId = cts.Id }, new() { UserName = "priya", AssignmentGroupId = cbs.Id }, new() { UserName = "agent", AssignmentGroupId = serviceDesk.Id }]);
        db.RoutingRules.AddRange([
            new() { Name = "CBS Domains and Hosts", Order = 5, Keywords = "cbs.com,corebank,core-banking", AssignmentGroupId = cbs.Id },
            new() { Name = "Scanner Jam Issues", Order = 10, Category = "CTS Hardware", Subcategory = "Scanner Jam", AssignmentGroupId = cts.Id },
            new() { Name = "Scanner Connectivity Issues", Order = 20, Category = "CTS Hardware", Subcategory = "Connectivity", AssignmentGroupId = cts.Id },
            new() { Name = "CBS Catch and Dispatch", Order = 30, Category = "CBS Integration", Subcategory = "Catch & Dispatch", AssignmentGroupId = cbs.Id },
            new() { Name = "Service Desk Catch All", Order = 999, AssignmentGroupId = serviceDesk.Id }
        ]);
        db.KnowledgeArticles.AddRange([
            new() { Title = "Scanner Jam Troubleshooting", Category = "CTS Hardware", Subcategory = "Scanner Jam", Keywords = "scanner jam outward inward clearing paper document", Content = "1. Stop the current scanning operation.\n2. Check the scanner and visible paper path.\n3. Safely remove any visibly jammed document without forcing it.\n4. Check for folded or damaged documents and align the batch correctly.\n5. Reinitialize the scanner using the approved application control.\n6. Retry with a single aligned document.\n7. If the jam continues, stop and escalate to CTS Hardware Support." },
            new() { Title = "Scanner Connectivity Troubleshooting", Category = "CTS Hardware", Subcategory = "Connectivity", Keywords = "scanner disconnected device not detected communication connection", Content = "1. Confirm the scanner power and ready indicators.\n2. Check approved physical cable connections at both ends.\n3. Confirm whether the CTS workstation detects the scanner.\n4. Close the active scan operation, then use the approved reconnect/restart procedure.\n5. Reopen the CTS application and check device status.\n6. Record the device model and exact error if the scanner remains unavailable." },
            new() { Title = "CBS Catch & Dispatch Information Collection", Category = "CBS Integration", Subcategory = "Catch & Dispatch", Keywords = "cbs url catch dispatch communication integration error", Content = "Do not repeatedly retry a failing dispatch. Record the exact CBS error, affected branch/user, start time, whether multiple users are affected, and the clearing stage. Capture approved screenshots or logs with no customer-sensitive data. Confirm network availability, then escalate the collected evidence to CBS Support for investigation and dispatch." }
        ]);
        AddDefaultPriorityRules(db);
        await db.SaveChangesAsync();
    }

    private static void AddDefaultPriorityRules(AppDbContext db) => db.PriorityRules.AddRange([
        new() { Name = "CBS Critical", Description = "Core Banking issues and configured CBS hosts are high priority.", Keywords = "cbs,core banking,core banking solution,cbs.com", Priority = "High", Order = 1 },
        new() { Name = "Scanner Issue", Description = "Scanner and document scanning issues are medium priority.", Keywords = "scanner,scanning,scan document,scanner not detected,scanner disconnected,scanner driver", Priority = "Medium", Order = 2 },
        new() { Name = "Default", Description = "Fallback for all other issues.", Priority = "Low", Order = 999 }
    ]);

    private static async Task SeedEnhancementsAsync(AppDbContext db)
    {
        if (!await db.PriorityRules.AnyAsync()) AddDefaultPriorityRules(db);
        if (!await db.Categories.AnyAsync(x => x.Name == "General")) db.Categories.Add(new() { Name = "General", Subcategories = [new() { Name = "Software" }] });
        if (!await db.Services.AnyAsync(x => x.Name == "Branch IT")) db.Services.Add(new() { Name = "Branch IT" });
        var cbs = await db.AssignmentGroups.FirstOrDefaultAsync(x => x.Name == "CBS Support");
        if (cbs is not null && !await db.RoutingRules.AnyAsync(x => x.Name == "CBS Domains and Hosts"))
            db.RoutingRules.Add(new() { Name = "CBS Domains and Hosts", Order = 5, Keywords = "cbs.com,corebank,core-banking", AssignmentGroupId = cbs.Id });
        await db.SaveChangesAsync();
    }
}
