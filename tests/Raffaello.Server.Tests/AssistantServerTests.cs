using Raffaello.Core.Assistant;
using Raffaello.Core.Notify;
using Raffaello.Core.Remote;

namespace Raffaello.Server.Tests;

/// <summary>[assistant] Conversations, reminders and notification rules on the server: per user, SITE role allowed, owner enforced.</summary>
public sealed class AssistantServerTests
{
    [PgFact]
    public async Task Each_user_keeps_his_own_history_and_cannot_write_another_users()
    {
        await using var srv = await TestServer.StartAsync();
        using var site = srv.Client("site-a", Roles.Site);
        using var other = srv.Client("site-b", Roles.Site);
        var a = new RemoteAssistantStore(site);
        var b = new RemoteAssistantStore(other);

        var conv = a.Insert(new AssistantConversation { Owner = site.User, Title = "remaining P2-106", CreatedAt = DateTime.Now, LastAt = DateTime.Now });
        a.Insert(new AssistantMessage { ConversationId = conv.Id, Seq = 1, Role = "user", ContentJson = "[{\"type\":\"text\",\"text\":\"hi\"}]", DisplayText = "hi" });
        a.Insert(new NotificationRule { Owner = site.User, EventKind = NotifyEvents.Brief, Channel = NotifyChannels.Email, Address = "a@example.com" });
        Assert.Single(a.Conversations(site.User));
        Assert.Single(a.Messages(conv.Id));

        var ex = Assert.Throws<PermissionDeniedException>(() => b.Insert(new AssistantMessage { ConversationId = conv.Id, Seq = 2, Role = "user", ContentJson = "[]" }));
        Assert.Contains("may not change AssistantMessages", ex.Message);
        Assert.Throws<PermissionDeniedException>(() => b.Insert(new AssistantReminder { Owner = site.User, Text = "x", Due = DateTime.Now }));
        b.Insert(new AssistantReminder { Owner = other.User, Text = "mine", Due = DateTime.Now });
        Assert.Single(b.Reminders(other.User));
    }
}
