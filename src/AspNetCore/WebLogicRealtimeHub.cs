using CL.WebLogic.Configuration;
using CL.WebLogic.Realtime;
using CL.WebLogic.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

namespace CL.WebLogic.AspNetCore;

public sealed class WebLogicRealtimeHub : Hub
{
    private const string UserIdItem = "weblogic.userId";
    private const string AccessGroupsItem = "weblogic.accessGroups";

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        await Groups.AddToGroupAsync(Context.ConnectionId, "audience:public").ConfigureAwait(false);

        if (httpContext is not null)
        {
            var session = await ResolveSessionAsync(httpContext).ConfigureAwait(false);
            if (session is not null)
            {
                // Remembered for Replay: the hub method has no HTTP request to re-check.
                Context.Items[UserIdItem] = session.UserId;
                Context.Items[AccessGroupsItem] = session.AccessGroups.ToArray();

                await Groups.AddToGroupAsync(Context.ConnectionId, "audience:authenticated").ConfigureAwait(false);
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{session.UserId}").ConfigureAwait(false);

                foreach (var accessGroup in session.AccessGroups)
                    await Groups.AddToGroupAsync(Context.ConnectionId, $"access:{accessGroup}").ConfigureAwait(false);
            }
        }

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public Task<WebLogicRealtimeReplayResponse> Replay(WebLogicRealtimeReplayRequest? request)
    {
        var webLogic = WebLogicLibrary.GetRequired();
        var bridge = webLogic.RealtimeBridge
            ?? throw new InvalidOperationException("CL.WebLogic realtime bridge is not available.");

        var take = Math.Clamp(request?.Take ?? 100, 1, 250);
        var snapshot = bridge.CreateSnapshot(take);
        var filter = request?.Filter;

        // The client filter can only narrow the result: every event must first be one this
        // connection would have received live, so User, AccessGroup and Internal events never
        // reach other callers through the replay buffer.
        var visible = snapshot.Events
            .Where(CanReceive)
            .Where(evt => filter is null || filter.Matches(evt))
            .ToArray();

        var visibleSnapshot = snapshot with { Events = visible, Count = visible.Length };
        var messages = visible.Select(static evt => evt.ToHubMessage()).ToArray();

        return Task.FromResult(new WebLogicRealtimeReplayResponse(visibleSnapshot, messages));
    }

    private bool CanReceive(WebLogicRealtimeEnvelope envelope)
    {
        var userId = Context.Items.TryGetValue(UserIdItem, out var id) ? id as string : null;
        var accessGroups = Context.Items.TryGetValue(AccessGroupsItem, out var groups)
            ? groups as string[] ?? []
            : [];

        return WebLogicRealtimeAccess.CanReceive(envelope, userId, accessGroups);
    }

    // Reads the session cookie off the WS upgrade request and looks it up in the
    // session store. No query-string fallback, no ASP.NET-session reads — the only
    // thing the hub trusts is a live session record.
    private static async Task<WebSessionRecord?> ResolveSessionAsync(HttpContext httpContext)
    {
        var webLogic = WebLogicLibrary.GetRequired();
        var store = webLogic.SessionStore;
        if (store is null) return null;

        var config = webLogic.GetConfig();
        var cookieName = config?.Session.CookieName ?? "fh_sid";
        if (!httpContext.Request.Cookies.TryGetValue(cookieName, out var token) || string.IsNullOrWhiteSpace(token))
            return null;

        var session = await store.GetAsync(token).ConfigureAwait(false);
        if (session is null || session.IsExpired(DateTime.UtcNow))
            return null;

        return session;
    }
}

public sealed class SignalRWebLogicRealtimeBroadcaster : IWebLogicRealtimeBroadcaster
{
    private readonly IHubContext<WebLogicRealtimeHub> _hubContext;

    public SignalRWebLogicRealtimeBroadcaster(IHubContext<WebLogicRealtimeHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task BroadcastAsync(WebLogicRealtimeEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var message = envelope.ToHubMessage();

        switch (envelope.Audience)
        {
            case WebLogicRealtimeAudience.Public:
                await _hubContext.Clients.Group("audience:public")
                    .SendAsync("weblogic:event", message, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case WebLogicRealtimeAudience.Authenticated:
                await _hubContext.Clients.Group("audience:authenticated")
                    .SendAsync("weblogic:event", message, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case WebLogicRealtimeAudience.AccessGroup:
                if (envelope.AccessGroups.Count == 0)
                    return;

                foreach (var accessGroup in envelope.AccessGroups)
                {
                    await _hubContext.Clients.Group($"access:{accessGroup}")
                        .SendAsync("weblogic:event", message, cancellationToken)
                        .ConfigureAwait(false);
                }
                break;

            case WebLogicRealtimeAudience.Internal:
                // Internal events (failures, config reloads, request logs) stay on the server.
                return;

            case WebLogicRealtimeAudience.User:
                if (envelope.Users.Count == 0)
                    return;

                foreach (var user in envelope.Users)
                {
                    await _hubContext.Clients.Group($"user:{user}")
                        .SendAsync("weblogic:event", message, cancellationToken)
                        .ConfigureAwait(false);
                }
                break;
        }
    }
}
