using Atlas.Server.Platform.Live;

namespace Atlas.Server.Features.Notifications;

/// <summary>What <c>user:{accountId}</c> (UserLiveChannel) hears when the account's inbox changed: the channel only, so a client reads its
/// inbox again under its own rights and the signal itself tells nothing (inbox spec §6).</summary>
public sealed record NotificationsChanged(string Channel)
{
    public const string Event = "NotificationsChanged";

    public static Task PublishAsync(LiveRelay relay, Guid accountId)
    {
        var channel = UserLiveChannel.Name(accountId);
        return relay.PublishAsync(channel, Event, new NotificationsChanged(channel));
    }
}
