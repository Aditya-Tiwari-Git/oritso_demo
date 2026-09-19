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
            new() { Title = "CBS Catch & Dispatch Information Collection", Category = "CBS Integration", Subcategory = "Catch & Dispatch", Keywords = "cbs url catch dispatch communication integration error", Content = "Do not repeatedly retry a failing dispatch. Record the exact CBS error, affected branch/user, start time, whether multiple users are affected, and the clearing stage. Capture approved screenshots or logs with no customer-sensitive data. Confirm network availability, then escalate the collected evidence to CBS Support for investigation and dispatch." },
            ..GeneralKnowledgeArticles()
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
        foreach (var article in GeneralKnowledgeArticles())
            if (!await db.KnowledgeArticles.AnyAsync(x => x.Title == article.Title)) db.KnowledgeArticles.Add(article);
        await db.SaveChangesAsync();
    }

    private static KnowledgeArticle[] GeneralKnowledgeArticles() =>
    [
        new() { Title = "Clear Browser Cache and Cookies", Category = "General", Subcategory = "Software", Keywords = "browser chrome edge firefox safari cache cookies strange slow website page loading", Content = "1. Save any work in open browser tabs.\n2. Open the browser menu and select Settings.\n3. Find Privacy and security, then choose Clear browsing data.\n4. Select cached images/files and cookies for the affected time range. Be aware that clearing cookies may sign you out of websites.\n5. Close every browser window, reopen the browser, and sign in again if required.\n6. Retry the affected site. If it still fails, note the browser name, page address, and exact error." },
        new() { Title = "Clear Outlook Cache", Category = "General", Subcategory = "Software", Keywords = "outlook cache cached temporary files clear data", Content = "1. Save open messages and close Outlook completely.\n2. Confirm Outlook is no longer running in Task Manager.\n3. Open Windows Settings, choose Apps, find Outlook, and use the available Repair option first.\n4. For classic Outlook, use your organization's approved Outlook profile/cache reset procedure; do not delete mailbox data files unless instructed by IT.\n5. Reopen Outlook and allow the mailbox to synchronize.\n6. If the issue remains, record the Outlook version and any error before escalating." },
        new() { Title = "Outlook Not Responding", Category = "General", Subcategory = "Software", Keywords = "outlook stuck frozen freeze not responding hanging loading won't open", Content = "1. Wait briefly for any large send/receive operation to finish.\n2. If Outlook remains frozen, close it using Task Manager.\n3. Reopen Outlook and test in safe mode using your organization's approved procedure.\n4. Disable only recently added, nonessential add-ins if permitted by policy.\n5. Restart the computer and test Outlook again.\n6. If it still freezes, note when it occurs and capture the error without including sensitive email content." },
        new() { Title = "Outlook Synchronization and Connectivity", Category = "General", Subcategory = "Software", Keywords = "outlook sync synchronization not receiving not sending send receive offline connectivity connection", Content = "1. Confirm other approved websites or services can connect to the network.\n2. Check that Outlook does not show Work Offline or Disconnected.\n3. Select Send/Receive and allow the operation to complete once.\n4. Confirm the mailbox is not over quota and the message is not blocked in the Outbox.\n5. Close and reopen Outlook, then restart the computer if needed.\n6. If synchronization still fails, record the last successful sync time and exact connection status." },
        new() { Title = "Microsoft Teams Cache and Basic Troubleshooting", Category = "General", Subcategory = "Software", Keywords = "microsoft teams ms teams stuck frozen freeze not responding loading cache connectivity connection", Content = "1. Save any unsent text, then quit Teams completely from the system tray.\n2. Confirm Teams is no longer running in Task Manager.\n3. Check that the network works in another approved application.\n4. Open Windows Settings, choose Apps, Microsoft Teams, Advanced options, and select Repair.\n5. If Repair does not help and policy permits it, select Reset; you may need to sign in again.\n6. Restart Teams and test a chat or meeting. If it still fails, record the Teams version and exact symptom." }
    ];
}
