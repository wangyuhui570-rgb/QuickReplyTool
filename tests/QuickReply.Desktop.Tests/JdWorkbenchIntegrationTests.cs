using QuickReply.Desktop.Models;
using QuickReply.Desktop.Services;

namespace QuickReply.Desktop.Tests;

public sealed class JdWorkbenchIntegrationTests
{
    [Fact]
    public void ProfileService_IgnoresIncompleteProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"quickreply-jd-profiles-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bad", ".cefcache", "Network"));
            var goodProfile = Path.Combine(root, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("京东店铺")));
            Directory.CreateDirectory(Path.Combine(goodProfile, ".cefcache", "Network"));
            File.WriteAllText(Path.Combine(goodProfile, ".cefcache", "LocalPrefs.json"), "{}");
            File.WriteAllText(Path.Combine(goodProfile, ".cefcache", "Network", "Cookies"), string.Empty);

            var service = new JdWorkbenchProfileService(root);
            var profiles = service.FindProfiles();

            Assert.Single(profiles);
            Assert.Equal("京东店铺", profiles[0].DisplayName);
            Assert.Equal(goodProfile, profiles[0].ProfilePath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MessageKey_UsesMidWhenPresent_AndStableHashFallback()
    {
        var createdAt = new DateTimeOffset(2026, 5, 18, 10, 30, 0, TimeSpan.FromHours(8));

        var midKey = JdWorkbenchChatClient.BuildMessageKeyForTesting("cid-1", "mid-9", "sid-1", createdAt, "customer", "有货吗");
        var hashKeyA = JdWorkbenchChatClient.BuildMessageKeyForTesting("cid-1", "", "sid-1", createdAt, "customer", "有货吗");
        var hashKeyB = JdWorkbenchChatClient.BuildMessageKeyForTesting("cid-1", "", "sid-1", createdAt, "customer", "有货吗");

        Assert.Equal("cid-1|mid:mid-9", midKey);
        Assert.Equal(hashKeyA, hashKeyB);
        Assert.StartsWith("cid-1|hash:", hashKeyA, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorCustomerTextGate_OnlyAllowsCustomerTextMessages()
    {
        Assert.True(JdConversationMonitor.IsCustomerText(new JdChatMessage
        {
            Speaker = "customer",
            MessageType = "text",
            Content = "什么时候发货"
        }));
        Assert.False(JdConversationMonitor.IsCustomerText(new JdChatMessage
        {
            Speaker = "waiter",
            MessageType = "text",
            Content = "您好"
        }));
        Assert.False(JdConversationMonitor.IsCustomerText(new JdChatMessage
        {
            Speaker = "customer",
            MessageType = "image",
            Content = "https://example.com/a.png"
        }));
    }

    [Fact]
    public void JdContextMapper_MapsLatestMessageToRecommendationSnapshot()
    {
        var context = new JdRecommendationContext
        {
            Session = new JdChatSession
            {
                Cid = "cid-1",
                Customer = "buyer",
                GoodsName = "跑鞋 RS500"
            },
            TriggerMessage = new JdChatMessage
            {
                MessageKey = "m2",
                Speaker = "customer",
                MessageType = "text",
                Content = "这款有黑色吗"
            },
            RecentMessages =
            [
                new JdChatMessage { MessageKey = "m1", Speaker = "waiter", Content = "您好" },
                new JdChatMessage { MessageKey = "m2", Speaker = "customer", Content = "这款有黑色吗" }
            ]
        };

        var snapshot = JdRecommendationContextMapper.ToSnapshot(context);

        Assert.Equal("jd_workbench", snapshot.PlatformHint);
        Assert.Equal("这款有黑色吗", snapshot.RecognizedCustomerText);
        Assert.Equal("跑鞋 RS500", snapshot.RecognizedProductName);
        Assert.Contains("客户：这款有黑色吗", snapshot.RecognizedContextText);
        Assert.Contains(snapshot.Messages, message => message.SelectedForCustomerText);
    }
}
