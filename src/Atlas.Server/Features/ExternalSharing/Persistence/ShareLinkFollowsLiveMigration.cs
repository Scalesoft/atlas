using FluentMigrator;

namespace Atlas.Server.Features.ExternalSharing.Persistence;

[Migration(2026110660, "3.6-I: a share link follows a live page only when it was created on a live page")]
public sealed class ShareLinkFollowsLiveMigration() : EmbeddedSchemaMigration("2026110660_share_link_follows_live.sql");
