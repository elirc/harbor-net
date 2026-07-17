using Harbor.Domain;
using Harbor.Domain.Entities;

namespace Harbor.Infrastructure;

/// <summary>Seeds a demo workspace for local development. Idempotent.</summary>
public static class DataSeeder
{
    // Well-known development API keys (hashed at rest like real keys).
    public const string AdaApiKey = "hbk_dev_ada_admin";
    public const string GraceApiKey = "hbk_dev_grace_agent";
    public const string LinusApiKey = "hbk_dev_linus_agent";

    public static void Seed(HarborDbContext db)
    {
        if (db.Workspaces.Any())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var workspace = new Workspace { Name = "Acme Support", CreatedAt = now.AddDays(-30) };

        var support = new Inbox
        {
            WorkspaceId = workspace.Id,
            Name = "Support",
            FirstResponseSlaMinutes = 60,
            AutoAssign = true,
            EmailAddress = "support@acme.test",
            CreatedAt = now.AddDays(-30),
        };
        // Chat-only, so the inbound-routing 422 path is reachable in the demo.
        var sales = new Inbox
        {
            WorkspaceId = workspace.Id,
            Name = "Sales",
            FirstResponseSlaMinutes = 240,
            CreatedAt = now.AddDays(-30),
        };

        var ada = new Teammate
        {
            WorkspaceId = workspace.Id,
            Name = "Ada Lovelace",
            Email = "ada@acme.test",
            Role = TeammateRole.Admin,
            ApiKeyHash = ApiKeys.Hash(AdaApiKey),
        };
        var grace = new Teammate
        {
            WorkspaceId = workspace.Id,
            Name = "Grace Hopper",
            Email = "grace@acme.test",
            CapacityLimit = 5,
            ApiKeyHash = ApiKeys.Hash(GraceApiKey),
        };
        // Away, so the Support inbox's round-robin skips him until he returns.
        var linus = new Teammate
        {
            WorkspaceId = workspace.Id,
            Name = "Linus Pauling",
            Email = "linus@acme.test",
            Availability = TeammateAvailability.Away,
            ApiKeyHash = ApiKeys.Hash(LinusApiKey),
        };

        var frontline = new Team { WorkspaceId = workspace.Id, Name = "Frontline" };
        var billingTeam = new Team { WorkspaceId = workspace.Id, Name = "Billing" };
        var memberships = new[]
        {
            new TeamMembership { TeamId = frontline.Id, TeammateId = ada.Id },
            new TeamMembership { TeamId = frontline.Id, TeammateId = grace.Id },
            new TeamMembership { TeamId = billingTeam.Id, TeammateId = linus.Id },
        };

        var mario = new Contact { WorkspaceId = workspace.Id, Name = "Mario Rossi", Email = "mario@example.com", LastSeenAt = now.AddHours(-2) };
        var jane = new Contact { WorkspaceId = workspace.Id, Name = "Jane Doe", Email = "jane@example.com", ExternalId = "cust-1042", LastSeenAt = now.AddDays(-1) };
        var kenji = new Contact { WorkspaceId = workspace.Id, Name = "Kenji Sato", Email = "kenji@example.com", LastSeenAt = now.AddMinutes(-30) };

        var tagBilling = new Tag { WorkspaceId = workspace.Id, Name = "billing" };
        var tagBug = new Tag { WorkspaceId = workspace.Id, Name = "bug" };
        var tagVip = new Tag { WorkspaceId = workspace.Id, Name = "vip" };

        var cannedReplies = new[]
        {
            new CannedReply
            {
                WorkspaceId = workspace.Id,
                Shortcut = "refund-policy",
                Title = "Refund policy",
                Body = "We offer full refunds within 30 days of purchase. I've started the process for you.",
            },
            new CannedReply
            {
                WorkspaceId = workspace.Id,
                Shortcut = "password-reset",
                Title = "Password reset steps",
                Body = "You can reset your password from the sign-in page via 'Forgot password'.",
            },
        };

        // Help center: a published collection plus one draft, so both the
        // public view and the drafts-are-hidden path are live in the demo.
        var gettingStarted = new ArticleCollection
        {
            WorkspaceId = workspace.Id,
            Name = "Getting started",
            Slug = "getting-started",
            Description = "Set-up and account basics.",
            CreatedAt = now.AddDays(-29),
        };
        var articles = new[]
        {
            new Article
            {
                WorkspaceId = workspace.Id,
                CollectionId = gettingStarted.Id,
                Title = "Resetting your password",
                Slug = "resetting-your-password",
                Body = "If you cannot log in, use 'Forgot password' on the sign-in page. "
                    + "The reset link expires after one hour. If your password is still "
                    + "invalid afterwards, clear your browser cache and try again.",
                Status = ArticleStatus.Published,
                PublishedAt = now.AddDays(-28),
                CreatedAt = now.AddDays(-29),
                UpdatedAt = now.AddDays(-28),
            },
            new Article
            {
                WorkspaceId = workspace.Id,
                CollectionId = gettingStarted.Id,
                Title = "Exporting your data",
                Slug = "exporting-your-data",
                Body = "You can export all of your data as CSV from Settings > Data > Export. "
                    + "Large exports are emailed to you when they finish.",
                Status = ArticleStatus.Published,
                PublishedAt = now.AddDays(-20),
                CreatedAt = now.AddDays(-21),
                UpdatedAt = now.AddDays(-20),
            },
            new Article
            {
                WorkspaceId = workspace.Id,
                CollectionId = gettingStarted.Id,
                Title = "Understanding duplicate charges",
                Slug = "understanding-duplicate-charges",
                Body = "A duplicate charge is usually an authorization hold that has not "
                    + "settled yet. It drops off within a few days; contact us for a refund "
                    + "if it does not.",
                Status = ArticleStatus.Draft,
                CreatedAt = now.AddDays(-2),
                UpdatedAt = now.AddDays(-2),
            },
        };

        // SLA policies for Support. Sales has no policy, so it falls back to the
        // inbox's own first-response minutes — the pre-policy behavior.
        var standardSupport = new SlaPolicy
        {
            WorkspaceId = workspace.Id,
            Name = "Standard support",
            InboxId = support.Id,
            FirstResponseMinutes = 60,
            ResolutionMinutes = 2_880,
            CreatedAt = now.AddDays(-30),
        };
        var urgentSupport = new SlaPolicy
        {
            WorkspaceId = workspace.Id,
            Name = "Urgent support",
            InboxId = support.Id,
            Priority = ConversationPriority.Urgent,
            FirstResponseMinutes = 15,
            ResolutionMinutes = 240,
            CreatedAt = now.AddDays(-30),
        };

        // Conversation 1: open, unassigned, urgent, first-response SLA breached
        // but still inside its resolution target.
        var convo1 = new Conversation
        {
            WorkspaceId = workspace.Id,
            InboxId = support.Id,
            ContactId = mario.Id,
            Subject = "Cannot log in to my account",
            Priority = ConversationPriority.Urgent,
            CreatedAt = now.AddHours(-3),
            UpdatedAt = now.AddHours(-3),
            LastMessageAt = now.AddHours(-3),
            SlaPolicyId = urgentSupport.Id,
            FirstResponseDueAt = now.AddHours(-3).AddMinutes(15),
            ResolutionDueAt = now.AddHours(-3).AddMinutes(240),
        };
        var convo1Messages = new[]
        {
            new Message
            {
                ConversationId = convo1.Id,
                AuthorType = AuthorType.Contact,
                AuthorContactId = mario.Id,
                Body = "Hi, I can't log in — it says my password is invalid even after resetting it.",
                CreatedAt = now.AddHours(-3),
            },
        };

        // Conversation 2: open, assigned to Ada, first response inside SLA.
        var convo2 = new Conversation
        {
            WorkspaceId = workspace.Id,
            InboxId = support.Id,
            ContactId = jane.Id,
            Subject = "Charged twice this month",
            AssignedTeammateId = ada.Id,
            CreatedAt = now.AddHours(-26),
            UpdatedAt = now.AddHours(-25),
            LastMessageAt = now.AddHours(-25),
            SlaPolicyId = standardSupport.Id,
            FirstResponseDueAt = now.AddHours(-26).AddMinutes(60),
            FirstRespondedAt = now.AddHours(-25.5),
            ResolutionDueAt = now.AddHours(-26).AddMinutes(2_880),
        };
        var convo2Messages = new[]
        {
            new Message
            {
                ConversationId = convo2.Id,
                AuthorType = AuthorType.Contact,
                AuthorContactId = jane.Id,
                Body = "I was charged twice for my subscription this month. Please refund one charge.",
                CreatedAt = now.AddHours(-26),
            },
            new Message
            {
                ConversationId = convo2.Id,
                AuthorType = AuthorType.Teammate,
                AuthorTeammateId = ada.Id,
                Body = "Sorry about that! I can see the duplicate charge and have queued a refund.",
                CreatedAt = now.AddHours(-25.5),
            },
            new Message
            {
                ConversationId = convo2.Id,
                Kind = MessageKind.Note,
                AuthorType = AuthorType.Teammate,
                AuthorTeammateId = ada.Id,
                Body = "Refund ref #R-2210 raised in Stripe; watch for webhook confirmation.",
                CreatedAt = now.AddHours(-25),
            },
        };

        // Conversation 3: snoozed, assigned to Billing team.
        var convo3 = new Conversation
        {
            WorkspaceId = workspace.Id,
            InboxId = sales.Id,
            ContactId = kenji.Id,
            Subject = "Enterprise plan quote",
            State = ConversationState.Snoozed,
            SnoozedUntil = now.AddDays(2),
            AssignedTeamId = billingTeam.Id,
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-1),
            LastMessageAt = now.AddDays(-1),
            FirstResponseDueAt = now.AddDays(-2).AddHours(4),
            FirstRespondedAt = now.AddDays(-2).AddHours(1),
        };
        var convo3Messages = new[]
        {
            new Message
            {
                ConversationId = convo3.Id,
                AuthorType = AuthorType.Contact,
                AuthorContactId = kenji.Id,
                Body = "Could you send a quote for the enterprise plan for ~50 seats?",
                CreatedAt = now.AddDays(-2),
            },
            new Message
            {
                ConversationId = convo3.Id,
                AuthorType = AuthorType.Teammate,
                AuthorTeammateId = linus.Id,
                Body = "Absolutely — putting the quote together, you'll have it this week.",
                CreatedAt = now.AddDays(-2).AddHours(1),
            },
        };

        // Conversation 4: closed.
        var convo4 = new Conversation
        {
            WorkspaceId = workspace.Id,
            InboxId = support.Id,
            ContactId = jane.Id,
            Subject = "How do I export my data?",
            State = ConversationState.Closed,
            ClosedAt = now.AddDays(-5),
            AssignedTeammateId = grace.Id,
            CreatedAt = now.AddDays(-6),
            UpdatedAt = now.AddDays(-5),
            LastMessageAt = now.AddDays(-5),
            SlaPolicyId = standardSupport.Id,
            FirstResponseDueAt = now.AddDays(-6).AddMinutes(60),
            FirstRespondedAt = now.AddDays(-6).AddMinutes(20),
            ResolutionDueAt = now.AddDays(-6).AddMinutes(2_880),
            FirstResolvedAt = now.AddDays(-5),
        };
        var convo4Messages = new[]
        {
            new Message
            {
                ConversationId = convo4.Id,
                AuthorType = AuthorType.Contact,
                AuthorContactId = jane.Id,
                Body = "Is there a way to export all my data as CSV?",
                CreatedAt = now.AddDays(-6),
            },
            new Message
            {
                ConversationId = convo4.Id,
                AuthorType = AuthorType.Teammate,
                AuthorTeammateId = grace.Id,
                Body = "Yes — Settings > Data > Export. Let me know if you need anything else!",
                CreatedAt = now.AddDays(-6).AddMinutes(20),
            },
        };

        var conversationTags = new[]
        {
            new ConversationTag { ConversationId = convo2.Id, TagId = tagBilling.Id },
            new ConversationTag { ConversationId = convo2.Id, TagId = tagVip.Id },
            new ConversationTag { ConversationId = convo1.Id, TagId = tagBug.Id },
            new ConversationTag { ConversationId = convo3.Id, TagId = tagBilling.Id },
        };

        db.Workspaces.Add(workspace);
        db.Inboxes.AddRange(support, sales);
        db.Teammates.AddRange(ada, grace, linus);
        db.Teams.AddRange(frontline, billingTeam);
        db.TeamMemberships.AddRange(memberships);
        db.Contacts.AddRange(mario, jane, kenji);
        db.Tags.AddRange(tagBilling, tagBug, tagVip);
        db.SlaPolicies.AddRange(standardSupport, urgentSupport);
        db.ArticleCollections.Add(gettingStarted);
        db.Articles.AddRange(articles);
        db.CannedReplies.AddRange(cannedReplies);
        db.Conversations.AddRange(convo1, convo2, convo3, convo4);
        db.Messages.AddRange(convo1Messages);
        db.Messages.AddRange(convo2Messages);
        db.Messages.AddRange(convo3Messages);
        db.Messages.AddRange(convo4Messages);
        db.ConversationTags.AddRange(conversationTags);

        db.SaveChanges();
    }
}
