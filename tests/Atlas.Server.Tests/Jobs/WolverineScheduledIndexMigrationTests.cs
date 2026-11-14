using Atlas.Server.Platform.Events;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Events.OutboxTests;

namespace Atlas.Server.Tests.Jobs;

/// <summary>Migration 2026111300 (the index WolverineFx 6.64 wants on scheduled envelopes) over the queue an installation already has:
/// a database at the 4.0.x schema holds a scheduled CheckpointLivePage that Wolverine 6.61 wrote (the row below is copied from one,
/// its body byte for byte); the upgraded server migrates, and the installed Wolverine reads the old envelope and delivers it.</summary>
public sealed class WolverineScheduledIndexMigrationTests
{
    private const string EnvelopeId = "08df4193-c234-626d-0009-0ffe00010000";

    /// <summary>The body of the envelope as 6.61.0 stored it: a checkpoint of page …a1, generation …c1, on local://outbox/.</summary>
    private const string Body661 =
        "596234c29341df480c00000006736f757263650c41746c61732e5365727665720c6d6573736167652d747970653f41746c61732e5365727665722e4665617475"
        + "7265732e436f6c6c61626f726174696f6e2e436f6d6d616e64732e436865636b706f696e744c69766550616765097265706c792d757269106c6f63616c3a2f2f"
        + "7265706c6965732f0c636f6e74656e742d74797065106170706c69636174696f6e2f6a736f6e0e636f7272656c6174696f6e2d69642432353039666435392d35"
        + "3363332d343730612d386465372d3537623533353431633933370f636f6e766572736174696f6e2d69642430386466343139332d633233332d636537652d3030"
        + "30392d3066666530303031303030300b64657374696e6174696f6e0f6c6f63616c3a2f2f6f7574626f782f0974656e616e742d6964092a44454641554c542a16"
        + "61636365707465642d636f6e74656e742d7479706573106170706c69636174696f6e2f6a736f6e0269642430386466343139332d633233342d363236642d3030"
        + "30392d3066666530303031303030300867726f75702d69642430303030303030302d303030302d303030302d303030302d3030303030303030303061310c7469"
        + "6d652d746f2d73656e641b323032362d31312d31335431353a33383a31382e34313135383936650000007b22706167654964223a2230303030303030302d3030"
        + "30302d303030302d303030302d303030303030303030306131222c2267656e65726174696f6e223a2230303030303030302d303030302d303030302d30303030"
        + "2d303030303030303030306331227d";

    [PostgreSqlFact]
    public async Task A_scheduled_message_of_4_0_x_takes_the_migration_and_is_delivered()
    {
        var schema = await DisposableSchema.CreateAsync("atlas_wolverine_scheduled_index_");
        await ApplyBeforeAsync(schema.ConnectionString, 2026111300);
        // Its time came while the server was down for the upgrade.
        await ExecuteAsync(schema.ConnectionString, $"""
            INSERT INTO wolverine_incoming_envelopes (id, status, owner_id, execution_time, attempts, body, message_type, received_at)
            VALUES ('{EnvelopeId}', 'Scheduled', 0, now() - interval '1 minute', 0, decode('{Body661}', 'hex'),
              'Atlas.Server.Features.Collaboration.Commands.CheckpointLivePage', '{Outbox.QueueUri}')
            """);
        Assert.Equal(DBNull.Value, await ScalarAsync(schema.ConnectionString, "SELECT to_regclass('idx_wolverine_incoming_envelopes_scheduled')::text"));

        // The start migrates the schema to head, as the server's does. The host owns and drops the schema.
        await using var host = await AtlasTestHost.CreateAsync(schema, outbox: true);

        Assert.Equal("execution_time|((status)::text = 'Scheduled'::text)", await ScalarAsync(host.ConnectionString, """
            SELECT a.attname || '|' || pg_get_expr(i.indpred, i.indrelid) FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[0]
            WHERE i.indexrelid = to_regclass('idx_wolverine_incoming_envelopes_scheduled')
            """));
        // The page is not there, so the checkpoint has nothing to store: delivered means read and handled, not a dead letter.
        await DrainAsync(host);
        Assert.Equal("Handled|0", await ScalarAsync(host.ConnectionString, $"""
            SELECT status || '|' || (SELECT count(*) FROM wolverine_dead_letters) FROM wolverine_incoming_envelopes WHERE id = '{EnvelopeId}'
            """));
    }
}
