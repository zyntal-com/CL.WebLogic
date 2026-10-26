namespace CL.WebLogic.Realtime;

/// <summary>
/// Decides whether a realtime connection may receive an event. Mirrors the live SignalR group
/// routing so replayed events reach exactly the connections that would have received them live.
/// Internal events are never delivered to clients.
/// </summary>
public static class WebLogicRealtimeAccess
{
    public static bool CanReceive(
        WebLogicRealtimeEnvelope envelope,
        string? userId,
        IReadOnlyCollection<string> accessGroups)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(accessGroups);

        var isAuthenticated = !string.IsNullOrWhiteSpace(userId);

        return envelope.Audience switch
        {
            WebLogicRealtimeAudience.Public => true,
            WebLogicRealtimeAudience.Authenticated => isAuthenticated,
            WebLogicRealtimeAudience.AccessGroup => isAuthenticated &&
                envelope.AccessGroups.Any(group => accessGroups.Contains(group, StringComparer.OrdinalIgnoreCase)),
            WebLogicRealtimeAudience.User => isAuthenticated &&
                envelope.Users.Contains(userId!, StringComparer.OrdinalIgnoreCase),
            _ => false
        };
    }
}
