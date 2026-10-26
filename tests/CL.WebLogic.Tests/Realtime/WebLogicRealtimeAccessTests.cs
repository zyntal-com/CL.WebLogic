using System.Text.Json;
using CL.WebLogic.Realtime;
using CL.WebLogic.Runtime;
using CodeLogic.Core.Events;
using Xunit;

namespace CL.WebLogic.Tests.Realtime;

public sealed class WebLogicRealtimeAccessTests
{
    private static WebLogicRealtimeEnvelope Event(
        WebLogicRealtimeAudience audience,
        string[]? users = null,
        string[]? groups = null) =>
        WebLogicRealtimeEnvelope.Create(
            WebLogicRealtimeKind.Custom, "test", "title",
            audience: audience, users: users, accessGroups: groups);

    [Fact]
    public void Anonymous_ReceivesOnlyPublicEvents()
    {
        Assert.True(WebLogicRealtimeAccess.CanReceive(Event(WebLogicRealtimeAudience.Public), null, []));
        Assert.False(WebLogicRealtimeAccess.CanReceive(Event(WebLogicRealtimeAudience.Authenticated), null, []));
        Assert.False(WebLogicRealtimeAccess.CanReceive(Event(WebLogicRealtimeAudience.User, users: ["u1"]), null, []));
        Assert.False(WebLogicRealtimeAccess.CanReceive(Event(WebLogicRealtimeAudience.AccessGroup, groups: ["admin"]), null, ["admin"]));
        Assert.False(WebLogicRealtimeAccess.CanReceive(Event(WebLogicRealtimeAudience.Internal), null, []));
    }

    [Fact]
    public void UserEvents_ReachOnlyTheirRecipients()
    {
        var evt = Event(WebLogicRealtimeAudience.User, users: ["u1"]);

        Assert.True(WebLogicRealtimeAccess.CanReceive(evt, "u1", []));
        Assert.True(WebLogicRealtimeAccess.CanReceive(evt, "U1", []));
        Assert.False(WebLogicRealtimeAccess.CanReceive(evt, "u2", []));
    }

    [Fact]
    public void AccessGroupEvents_RequireAMatchingGroup()
    {
        var evt = Event(WebLogicRealtimeAudience.AccessGroup, groups: ["admin"]);

        Assert.True(WebLogicRealtimeAccess.CanReceive(evt, "u1", ["member", "Admin"]));
        Assert.False(WebLogicRealtimeAccess.CanReceive(evt, "u1", ["member"]));
    }

    [Fact]
    public void InternalEvents_NeverReachClients()
    {
        Assert.False(WebLogicRealtimeAccess.CanReceive(Event(WebLogicRealtimeAudience.Internal), "u1", ["admin"]));
    }

    [Fact]
    public void RequestEvents_AreInternalAndCarryNoClientIp()
    {
        var bus = new EventBus();
        using var bridge = new WebLogicRealtimeBridge(new WebLogicRealtimeBuffer());
        bridge.RegisterDefaultMappings(bus);

        bus.Publish(new WebRequestHandledEvent("GET", "/account", "203.0.113.7", 200, 5));
        bus.Publish(new WebRequestBlockedEvent("POST", "/auth/login", "203.0.113.7", 429, "rate limited"));

        var events = bridge.GetRecent();
        Assert.Equal(2, events.Count);
        Assert.All(events, evt =>
        {
            Assert.Equal(WebLogicRealtimeAudience.Internal, evt.Audience);
            Assert.DoesNotContain("203.0.113.7", JsonSerializer.Serialize(evt.Payload));
        });
    }
}
