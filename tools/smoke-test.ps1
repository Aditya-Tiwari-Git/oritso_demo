param([string]$BaseUrl = 'http://localhost:5266')
$ErrorActionPreference = 'Stop'
$script:passed = 0
function Assert([bool]$condition, [string]$name) { if (-not $condition) { throw "FAILED: $name" }; $script:passed++; Write-Host "PASS $name" }
function Login([string]$user, [string]$password) { $session = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" -ContentType 'application/json' -Body (@{userName=$user;password=$password}|ConvertTo-Json); return @{Authorization="Bearer $($session.token)"} }
function PostJson([string]$path, $body, $headers) { Invoke-RestMethod -Method Post -Uri "$BaseUrl$path" -Headers $headers -ContentType 'application/json' -Body ($body|ConvertTo-Json -Depth 8) }
function PatchJson([string]$path, $body, $headers) { Invoke-RestMethod -Method Patch -Uri "$BaseUrl$path" -Headers $headers -ContentType 'application/json' -Body ($body|ConvertTo-Json -Depth 8) }

$root = Invoke-RestMethod "$BaseUrl/"; Assert ($root.status -eq 'running' -and $root.service -eq 'Oritso IT Support CRM API') 'Public Oritso API root'
$health = Invoke-RestMethod "$BaseUrl/health"; Assert ($health.status -eq 'healthy') 'Public health endpoint'
try { Invoke-RestMethod "$BaseUrl/api/tickets" | Out-Null; throw 'Protected endpoint allowed anonymous request' } catch { Assert ($_.Exception.Response.StatusCode.value__ -eq 401) 'Protected endpoint rejects anonymous request' }
$admin = Login 'admin' 'Admin@123'; $rahul = Login 'rahul' 'Rahul@123'; $priya = Login 'priya' 'Priya@123'; $user = Login 'user' 'User@123'
Assert ($admin.Authorization -and $rahul.Authorization -and $priya.Authorization -and $user.Authorization) 'All three personas login'
try { Invoke-RestMethod "$BaseUrl/api/admin/users" -Headers $user | Out-Null; throw 'User accessed admin' } catch { Assert ($_.Exception.Response.StatusCode.value__ -eq 403) 'Admin API rejects User role' }
$catalog = Invoke-RestMethod "$BaseUrl/api/catalog" -Headers $user; $ctsGroup = ($catalog.assignmentGroups|Where-Object name -eq 'CTS Hardware Support').id; $cbsGroup = ($catalog.assignmentGroups|Where-Object name -eq 'CBS Support').id
Assert ($ctsGroup -and $cbsGroup) 'CTS seed groups available'

$ticket = PostJson '/api/tickets' @{type='Incident';title='Scanner disconnects in outward clearing';description='Device disconnects after the second cheque. Error SCN-42.';category='CTS Hardware';subcategory='Connectivity';priority='High';service='CTS Scanner';impact='Single User';urgency='High'} $user
Assert ($ticket.number -match '^INC\d{6}$') 'Sequential readable ticket number'
Assert ($ticket.assignmentGroupId -eq $ctsGroup) 'Connectivity auto-routes to CTS Hardware Support'
Assert ($ticket.priority -eq 'Medium') 'Scanner priority is calculated as Medium despite submitted High'
$rahulQueue = Invoke-RestMethod "$BaseUrl/api/tickets" -Headers $rahul; $priyaQueue = Invoke-RestMethod "$BaseUrl/api/tickets" -Headers $priya
Assert (($rahulQueue.id -contains $ticket.id) -and -not ($priyaQueue.id -contains $ticket.id)) 'Assignment-group queue authorization'
$accepted = PostJson "/api/tickets/$($ticket.id)/accept" @{} $rahul; Assert ($accepted.assignedAgent -eq 'rahul' -and $accepted.status -eq 'Assigned') 'Agent accepts ticket'
PatchJson "/api/tickets/$($ticket.id)" @{status='In Progress';priority='High'} $rahul | Out-Null
PostJson "/api/tickets/$($ticket.id)/comments" @{body='We are investigating the scanner communication issue.'} $rahul | Out-Null
PostJson "/api/tickets/$($ticket.id)/work-notes" @{body='Captured SCN-42 logs; approved cable checks completed.'} $rahul | Out-Null
$detailAgent = Invoke-RestMethod "$BaseUrl/api/tickets/$($ticket.id)" -Headers $rahul; $detailUser = Invoke-RestMethod "$BaseUrl/api/tickets/$($ticket.id)" -Headers $user
Assert ($detailAgent.workNotes.Count -eq 1 -and $detailUser.workNotes.Count -eq 0) 'Internal notes hidden from end user'
Assert ($detailUser.comments.Count -eq 1 -and $detailUser.status -eq 'In Progress') 'Public comment and status synchronize'
Assert (($detailAgent.history.action -contains 'Automatically Routed') -and ($detailAgent.history.action -contains 'Agent Assigned') -and ($detailAgent.history.action -contains 'Status Changed')) 'Ticket audit history records lifecycle'
PatchJson "/api/tickets/$($ticket.id)" @{resolutionCode='Solved (Permanently)';resolutionNotes='Replaced approved scanner cable and verified five outward scans.'} $rahul | Out-Null
$resolved = Invoke-RestMethod "$BaseUrl/api/tickets/$($ticket.id)" -Headers $user; Assert ($resolved.status -eq 'Resolved' -and $resolved.resolutionNotes) 'Agent resolution visible to user'

$hello = PostJson '/api/chat' @{message='Hello, what can you help me with?'} $user
Assert ($hello.stage -eq 'Identify' -and -not $hello.ticketId) 'General greeting remains conversational and creates no ticket'
$jam = PostJson '/api/chat' @{message='My scanner is jammed while doing outward clearing.'} $user
Assert ($jam.stage -eq 'Troubleshooting' -and $jam.articles.Count -gt 0 -and $jam.articles[0].subcategory -eq 'Scanner Jam') 'Scanner Jam bot uses approved KB'
$jamFixed = PostJson '/api/chat' @{message='Yes, it is fixed now.';sessionId=$jam.sessionId} $user; Assert ($jamFixed.stage -eq 'ResolvedWithoutTicket' -and -not $jamFixed.ticketId) 'Resolved bot flow creates no ticket'
$nextIssue = PostJson '/api/chat' @{message="I also can't access CBS.";sessionId=$jam.sessionId} $user
Assert ($nextIssue.stage -eq 'Troubleshooting' -and $nextIssue.articles[0].subcategory -eq 'Catch & Dispatch') 'Resolved session detects a new unrelated CBS issue'

$browser = PostJson '/api/chat' @{message='Chrome is behaving strangely. How can I clear cookies?'} $user
Assert ($browser.stage -eq 'Troubleshooting' -and $browser.articles[0].title -eq 'Clear Browser Cache and Cookies' -and -not $browser.ticketId) 'Browser wording returns cache and cookie troubleshooting without creating a ticket'
$browserFixed = PostJson '/api/chat' @{message='Yes, that resolved it.';sessionId=$browser.sessionId} $user
Assert ($browserFixed.stage -eq 'ResolvedWithoutTicket' -and -not $browserFixed.ticketId) 'Resolved browser flow resets without a ticket'

$outlook = PostJson '/api/chat' @{message='Outlook is not syncing and appears offline.'} $user
Assert ($outlook.stage -eq 'Troubleshooting' -and $outlook.articles[0].title -eq 'Outlook Synchronization and Connectivity') 'Outlook synchronization wording selects connectivity guidance'
$outlookOffer = PostJson '/api/chat' @{message='It is still not working.';sessionId=$outlook.sessionId} $user
Assert ($outlookOffer.stage -eq 'OfferActions' -and $outlookOffer.canEscalate -and $outlookOffer.canCreateTicket) 'Unresolved Outlook flow offers live support and ticket actions'

$outlookCache = PostJson '/api/chat' @{message='How do I clear the Outlook cache?'} $user
Assert ($outlookCache.stage -eq 'Troubleshooting' -and $outlookCache.articles[0].title -eq 'Clear Outlook Cache') 'Outlook cache request selects safe cache guidance'
$teams = PostJson '/api/chat' @{message='Microsoft Teams is frozen and not responding.'} $user
Assert ($teams.stage -eq 'Troubleshooting' -and $teams.articles[0].title -eq 'Microsoft Teams Cache and Basic Troubleshooting') 'Teams natural-language symptom selects Teams troubleshooting'

$bot = PostJson '/api/chat' @{message='My scanner keeps disconnecting.'} $user
$bot = PostJson '/api/chat' @{message='Still not working.';sessionId=$bot.sessionId} $user
Assert ($bot.stage -eq 'OfferActions' -and $bot.canEscalate -and $bot.canCreateTicket -and -not $bot.ticketId) 'Unresolved bot offers live agent and ticket form together'
$botForm = PostJson '/api/chat' @{message='Create a ticket';sessionId=$bot.sessionId;action='create-ticket'} $user
Assert ($botForm.stage -eq 'TicketForm' -and $botForm.ticketDraft.category -eq 'CTS Hardware') 'Chatbot opens a prefilled ticket form'
$botCreated = PostJson '/api/chat/tickets' @{sessionId=$bot.sessionId;type='Incident';title=$botForm.ticketDraft.title;description=$botForm.ticketDraft.description;category=$botForm.ticketDraft.category;subcategory=$botForm.ticketDraft.subcategory;service=$botForm.ticketDraft.service;impact='Single User';urgency='Medium'} $user
Assert ($botCreated.ticketNumber -match '^INC\d{6}$' -and $botCreated.assignmentGroup -eq 'CTS Hardware Support' -and $botCreated.calculatedPriority -eq 'Medium') 'Reviewed chatbot form creates, prioritizes, and routes scanner ticket'

$cbs = PostJson '/api/chat' @{message='I am getting an error on https://cbs.com/error'} $user
Assert ($cbs.stage -eq 'Troubleshooting' -and $cbs.articles[0].subcategory -eq 'Catch & Dispatch') 'Configured CBS domain is recognized without explicit CBS label'
$cbsOffer = PostJson '/api/chat' @{message='It is still not working';sessionId=$cbs.sessionId} $user
$cbsForm = PostJson '/api/chat' @{message='Create a ticket';sessionId=$cbs.sessionId;action='create-ticket'} $user
$cbsCreated = PostJson '/api/chat/tickets' @{sessionId=$cbs.sessionId;type='Incident';title=$cbsForm.ticketDraft.title;description=$cbsForm.ticketDraft.description;category=$cbsForm.ticketDraft.category;subcategory=$cbsForm.ticketDraft.subcategory;service=$cbsForm.ticketDraft.service;impact='Single User';urgency='Medium'} $user
Assert ($cbsOffer.canCreateTicket -and $cbsCreated.assignmentGroup -eq 'CBS Support' -and $cbsCreated.calculatedPriority -eq 'High') 'CBS URL ticket receives CBS routing and High priority'

$general = PostJson '/api/tickets' @{type='Incident';title='General software issue';description='A general desktop application problem';category='General';subcategory='Software';priority='Critical';service='Branch IT';impact='Single User';urgency='Critical'} $user
Assert ($general.priority -eq 'Low') 'Backend ignores user-supplied priority and applies Low fallback'
$creatorDetail = Invoke-RestMethod "$BaseUrl/api/tickets/$($general.id)" -Headers $admin
Assert ($creatorDetail.createdByEmail -eq 'user@oritso.demo' -and $creatorDetail.createdByDisplayName) 'Ticket detail exposes creator contact data to authorized support'
$botStatus = Invoke-RestMethod "$BaseUrl/api/admin/bot-status" -Headers $admin; Assert ($null -ne $botStatus.enabled -and -not $botStatus.keyExposed) 'OpenAI configuration reported without exposing key'
$priorityConfig = Invoke-RestMethod "$BaseUrl/api/admin/configuration" -Headers $admin
Assert (($priorityConfig.priorityRules.name -contains 'CBS Critical') -and ($priorityConfig.priorityRules.name -contains 'Scanner Issue') -and ($priorityConfig.priorityRules.name -contains 'Default')) 'Default configurable priority rules are available'
$managedUsers = Invoke-RestMethod "$BaseUrl/api/admin/users" -Headers $admin
Assert (($managedUsers | Where-Object userName -eq 'user').email -eq 'user@oritso.demo') 'Admin user management returns configured email'
try { PostJson '/api/admin/users' @{userName='invalid-email-fixture';displayName='Invalid Email';email='not-an-email';role='User';password='Fixture@123';isActive=$true} $admin | Out-Null; throw 'Invalid email was accepted' } catch { Assert ($_.Exception.Response.StatusCode.value__ -eq 400) 'Backend rejects invalid user email'
}
$customPriority = PostJson '/api/admin/priority-rules' @{name='Smoke precedence';description='Temporary verification rule';keywords='priority-smoke-key';category='General';service='Branch IT';priority='Critical';order=0;isActive=$true} $admin
$customTicket = PostJson '/api/tickets' @{type='Incident';title='priority-smoke-key application issue';description='Temporary deterministic precedence check';category='General';subcategory='Software';priority='Low';service='Branch IT';impact='Single User';urgency='Low'} $user
Assert ($customTicket.priority -eq 'Critical') 'First matching Admin priority rule wins by configured order'
$updatedRule = @{name=$customPriority.name;description=$customPriority.description;keywords=$customPriority.keywords;category=$customPriority.category;service=$customPriority.service;priority='High';order=0;isActive=$false}
Invoke-RestMethod -Method Put -Uri "$BaseUrl/api/admin/priority-rules/$($customPriority.id)" -Headers $admin -ContentType 'application/json' -Body ($updatedRule|ConvertTo-Json) | Out-Null
$disabledTicket = PostJson '/api/tickets' @{type='Incident';title='priority-smoke-key second issue';description='Disabled rule check';category='General';subcategory='Software';priority='Critical';service='Branch IT';impact='Single User';urgency='Critical'} $user
Assert ($disabledTicket.priority -eq 'Low') 'Disabled priority rule is excluded from evaluation'
try { PostJson '/api/admin/priority-rules' @{name='Unauthorized';description='';keywords='x';category='';service='';priority='High';order=0;isActive=$true} $user | Out-Null; throw 'User configured priority rules' } catch { Assert ($_.Exception.Response.StatusCode.value__ -eq 403) 'User cannot configure priority rules' }
Invoke-RestMethod -Method Delete -Uri "$BaseUrl/api/admin/priority-rules/$($customPriority.id)" -Headers $admin | Out-Null

$liveTransfer = PostJson '/api/chat' @{message='Connect me to a live agent';sessionId=$bot.sessionId;action='request-live'} $user
$live = Invoke-RestMethod "$BaseUrl/api/live-support/$($liveTransfer.liveSessionId)" -Headers $user
Assert ($liveTransfer.liveStatus -eq 'Waiting' -and ($live.messages.sender -contains 'Oritso Assistant')) 'Chatbot transfers context into waiting live conversation'
$agentLive = Invoke-RestMethod "$BaseUrl/api/live-support" -Headers $rahul; Assert ($agentLive.publicId -contains $live.publicId) 'Live request appears in agent queue'
$active = PostJson "/api/live-support/$($live.publicId)/accept" @{} $rahul; Assert ($active.status -eq 'Active' -and $active.acceptedBy -eq 'rahul') 'Agent accepts live conversation'
PostJson "/api/live-support/$($live.publicId)/messages" @{body='Hello, I can help with the scanner.'} $rahul | Out-Null
PostJson "/api/live-support/$($live.publicId)/messages" @{body='The device disconnects after two scans.'} $user | Out-Null
$transcript = Invoke-RestMethod "$BaseUrl/api/live-support/$($live.publicId)" -Headers $user; Assert ($transcript.messages.Count -ge 3) 'Live messages persist with transcript'
$converted = PostJson "/api/live-support/$($live.publicId)/ticket" @{createTicket=$true;title='Live support scanner escalation';description='Scanner disconnect remained unresolved.'} $rahul
Assert ($converted.ticketId -gt 0) 'Live conversation converts to routed ticket'
PostJson "/api/live-support/$($live.publicId)/end" @{} $rahul | Out-Null
$afterLive = PostJson '/api/chat' @{message='I now have a scanner jam';sessionId=$bot.sessionId} $user
Assert ($afterLive.stage -eq 'Troubleshooting' -and $afterLive.articles[0].subcategory -eq 'Scanner Jam') 'Ended live mode returns chatbot to a fresh issue cycle'

$config = Invoke-RestMethod "$BaseUrl/api/admin/configuration" -Headers $admin
$originalGroups = @($config.memberships|Where-Object userName -eq 'rahul'|ForEach-Object assignmentGroupId)
Invoke-RestMethod -Method Put -Uri "$BaseUrl/api/admin/memberships/rahul" -Headers $admin -ContentType 'application/json' -Body (@{userName='rahul';assignmentGroupIds=@($cbsGroup)}|ConvertTo-Json) | Out-Null
$membership = Invoke-RestMethod "$BaseUrl/api/admin/configuration" -Headers $admin
Assert (($membership.memberships|Where-Object userName -eq 'rahul').assignmentGroupId -eq $cbsGroup) 'Admin changes agent assignment group'
Invoke-RestMethod -Method Put -Uri "$BaseUrl/api/admin/memberships/rahul" -Headers $admin -ContentType 'application/json' -Body (@{userName='rahul';assignmentGroupIds=$originalGroups}|ConvertTo-Json) | Out-Null

# Administrative deletion: authorization, cascades, physical attachment cleanup, and retained audit tombstones.
$deleteFixture = PostJson '/api/tickets' @{type='Incident';title='Administrative deletion fixture';description='Ticket used to verify safe cascading deletion.';category='CTS Hardware';subcategory='Connectivity';priority='Medium';service='CTS Scanner';impact='Single User';urgency='Medium'} $user
$fixtureFile = Join-Path $env:TEMP 'oritso-delete-fixture.txt'; [IO.File]::WriteAllText($fixtureFile, 'Oritso attachment deletion fixture')
$uploadJson = & curl.exe -sS -X POST -H "Authorization: $($admin.Authorization)" -F "file=@$fixtureFile;type=text/plain" "$BaseUrl/api/tickets/$($deleteFixture.id)/attachments"
Remove-Item -LiteralPath $fixtureFile -Force
if ($LASTEXITCODE -ne 0) { throw 'Attachment upload fixture failed.' }
$attachment = $uploadJson | ConvertFrom-Json
$attachmentPath = Join-Path (Resolve-Path 'backend/ItSupport.Api/uploads') $attachment.storedName
Assert (Test-Path -LiteralPath $attachmentPath) 'Deletion fixture physical attachment created'

$deleteChat = PostJson '/api/chat' @{message='My scanner is jammed and this conversation will be deleted.'} $user
$deleteLive = PostJson '/api/live-support' @{subject='Administrative live deletion fixture'} $user
$inventory = Invoke-RestMethod "$BaseUrl/api/admin/deletion-inventory" -Headers $admin
$chatRow = $inventory.chatSessions | Where-Object publicId -eq $deleteChat.sessionId
$liveRow = $inventory.liveSessions | Where-Object publicId -eq $deleteLive.publicId
Assert ($chatRow.id -and $liveRow.id) 'Admin deletion inventory exposes operational records'

$targets = @("tickets/$($deleteFixture.id)", "chat-sessions/$($chatRow.id)", "live-sessions/$($liveRow.id)")
foreach ($headers in @($user, $rahul)) {
    foreach ($target in $targets) {
        try { Invoke-RestMethod -Method Delete -Uri "$BaseUrl/api/admin/$target" -Headers $headers | Out-Null; throw "Non-admin deleted $target" }
        catch { Assert ($_.Exception.Response.StatusCode.value__ -eq 403) "Non-admin denied DELETE $target" }
    }
}

Invoke-RestMethod -Method Delete -Uri "$BaseUrl/api/admin/tickets/$($deleteFixture.id)" -Headers $admin | Out-Null
Invoke-RestMethod -Method Delete -Uri "$BaseUrl/api/admin/chat-sessions/$($chatRow.id)" -Headers $admin | Out-Null
Invoke-RestMethod -Method Delete -Uri "$BaseUrl/api/admin/live-sessions/$($liveRow.id)" -Headers $admin | Out-Null
Assert (-not (Test-Path -LiteralPath $attachmentPath)) 'Ticket deletion removes unreferenced physical attachment'
try { Invoke-RestMethod "$BaseUrl/api/tickets/$($deleteFixture.id)" -Headers $admin | Out-Null; throw 'Deleted ticket remained available' } catch { Assert ($_.Exception.Response.StatusCode.value__ -eq 404) 'Deleted ticket is unavailable' }
$afterDelete = Invoke-RestMethod "$BaseUrl/api/admin/deletion-inventory" -Headers $admin
Assert (-not ($afterDelete.tickets.id -contains $deleteFixture.id) -and -not ($afterDelete.chatSessions.id -contains $chatRow.id) -and -not ($afterDelete.liveSessions.id -contains $liveRow.id)) 'Deleted records leave the Admin inventory'

$sqlite = Get-Command sqlite3 -ErrorAction SilentlyContinue
if ($sqlite) {
    $dbPath = (Resolve-Path 'backend/ItSupport.Api/data/itsupport.db').Path
    $orphans = & $sqlite.Source $dbPath "SELECT (SELECT COUNT(*) FROM TicketComments WHERE TicketId=$($deleteFixture.id)) + (SELECT COUNT(*) FROM TicketWorkNotes WHERE TicketId=$($deleteFixture.id)) + (SELECT COUNT(*) FROM TicketHistory WHERE TicketId=$($deleteFixture.id)) + (SELECT COUNT(*) FROM TicketAttachments WHERE TicketId=$($deleteFixture.id)) + (SELECT COUNT(*) FROM ChatMessages WHERE ChatSessionId=$($chatRow.id)) + (SELECT COUNT(*) FROM LiveSupportMessages WHERE LiveSupportSessionId=$($liveRow.id));"
    Assert ([int]$orphans -eq 0) 'Administrative deletion leaves no orphan child rows'
    $tombstones = & $sqlite.Source $dbPath "SELECT COUNT(*) FROM AuditLogs WHERE Method='DELETE' AND Path LIKE 'Admin deleted %';"
    Assert ([int]$tombstones -ge 3) 'Administrative deletion preserves audit tombstones'
}

Write-Host "SMOKE TESTS PASSED: $script:passed assertions"
