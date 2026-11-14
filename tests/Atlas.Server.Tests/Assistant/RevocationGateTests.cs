using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Assistant.Contracts;
using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Features.SpaceAi.Commands;
using Atlas.Server.Features.SpaceAi.Contracts;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>
/// The revocation gate (plan phase 3 R2, spec RAG §5): current authorization is checked at retrieval, before the model and in one
/// short transaction after it, whose commit is the release. Every path of revocation at every moment of an answer, the ordering of
/// the gate's locks against a concurrent revocation, and a source published again during generation.
/// </summary>
public sealed class RevocationGateTests
{
    private const string AnswerMarker = "ODPOVĚĎ-Z-BRÁNY";

    public enum Revocation
    {
        GrantRemoved,
        GroupMembershipRemoved,
        GrantExpired,
        OwnAccess,
        AccountDeactivated,
        SecurityStampChanged,
        MovedToUnreadableSpace,
        Trashed,
        Unpublished,
        SpaceExcludedFromAi,
    }

    public enum Moment { BeforePreparation, BetweenPreparationAndCompletion, DuringModelCall }

    public static TheoryData<Revocation> Revocations() => new(Enum.GetValues<Revocation>());

    /// <summary>One reader, one space and one published page per moment, so each moment starts from granted access.</summary>
    private sealed record Case(Moment Moment, UserAccount Reader, Guid SpaceId, Guid PageId, Guid? GroupId);

    [Trait("Category", "Smoke")]
    [PostgreSqlTheory]
    [MemberData(nameof(Revocations))]
    public async Task Revocation_before_retrieval_during_the_model_call_and_before_release(Revocation revocation)
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        f.Fake.ChatAnswer = FakeAi.Answers.Answerable((AnswerMarker + " spusťte nástroj.", [1]));
        var cases = new List<Case>();
        foreach (var moment in Enum.GetValues<Moment>())
        {
            cases.Add(await ArrangeAsync(f, revocation, moment));
        }
        await f.IndexAsync();

        foreach (var c in cases)
        {
            var scope = new AiScope("space", c.SpaceId, null);
            var calls = f.ChatCalls.Count;
            string? state;
            switch (c.Moment)
            {
                case Moment.BeforePreparation:
                    await RevokeAsync(f, revocation, c);
                    // Refused at the door (401 revoked credential, 404 a space no longer readable) or nothing left to answer from.
                    try
                    {
                        var prepared = await f.StartAsync(c.Reader, "Jak obnovit data?", scope);
                        Assert.Equal(AiMessageState.NoSources, prepared.State);
                        Assert.Empty(prepared.Sources);
                        state = prepared.State;
                    }
                    catch (ApiException e) when (e.Status is 401 or 404)
                    {
                        state = null;
                    }
                    Assert.Equal(calls, f.ChatCalls.Count);
                    break;
                case Moment.BetweenPreparationAndCompletion:
                {
                    var prepared = await f.StartAsync(c.Reader, "Jak obnovit data?", scope);
                    Assert.Equal(AiMessageState.Preparing, prepared.State);
                    await RevokeAsync(f, revocation, c);
                    var answer = await f.CompleteAsync(c.Reader, prepared.MessageId);
                    // The check before the model: the model never gets the source.
                    Assert.Equal(calls, f.ChatCalls.Count);
                    Assert.Empty(answer.Sentences);
                    Assert.Empty(answer.Sources);
                    state = answer.State;
                    Assert.Equal(AiMessageState.Withheld, (await f.StoredAsync(prepared.MessageId)).Message.State);
                    break;
                }
                default:
                {
                    var prepared = await f.StartAsync(c.Reader, "Jak obnovit data?", scope);
                    Assert.Equal(AiMessageState.Preparing, prepared.State);
                    var hold = f.Fake.Hold(AiProfileKind.Chat);
                    var completion = f.CompleteAsync(c.Reader, prepared.MessageId);
                    await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));
                    await RevokeAsync(f, revocation, c);
                    hold.Release();
                    var answer = await completion;
                    Assert.Equal(calls + 1, f.ChatCalls.Count);
                    Assert.Empty(answer.Sentences);
                    Assert.Empty(answer.Sources);
                    state = answer.State;
                    var (stored, _) = await f.StoredAsync(prepared.MessageId);
                    Assert.Equal((AiMessageState.Withheld, (AiStoredAnswer?)null), (stored.State, stored.Answer));
                    break;
                }
            }
            Assert.NotEqual(AiMessageState.Answered, state);
        }
        // No text of any answer was ever stored.
        Assert.False(await f.AnyStoredAnswerContainsAsync(AnswerMarker));
        Assert.Equal(0, await f.CountAsync("""SELECT count(*) FROM "AiMessage" WHERE "State"='answered'"""));
    }

    [PostgreSqlFact]
    public async Task A_revocation_committed_before_the_gate_locks_withholds_and_one_waiting_on_them_follows_the_release()
    {
        var barrier = new GateBarrier();
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning, services =>
            services.AddScoped<IContentAccess>(provider => new PausedContentAccess(ActivatorUtilities.CreateInstance<ContentAccessService>(provider), barrier)));
        var page = await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        f.Fake.ChatAnswer = FakeAi.Answers.Answerable((AnswerMarker + " spusťte nástroj.", [1]));

        // a) The gate holds the content, space and account locks and has not read the page yet: a trash committed now is seen.
        var completion = await ReleaseUntilPausedAsync(f, barrier, f.Reader);
        await f.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"=@admin,"TrashBatchId"=gen_random_uuid() WHERE "Id"=@id
            """, ("id", page), ("admin", f.Admin.Id));
        barrier.Resume();
        Assert.Equal(AiMessageState.Withheld, (await completion).State);
        Assert.False(await f.AnyStoredAnswerContainsAsync(AnswerMarker));

        // b) A revocation that starts after the gate's locks waits for the release: the answer is released, then the account goes.
        await f.ExecuteAsync("""UPDATE "Page" SET "IsTrashed"=false,"TrashedAt"=NULL,"TrashedById"=NULL,"TrashBatchId"=NULL WHERE "Id"=@id""", ("id", page));
        completion = await ReleaseUntilPausedAsync(f, barrier, f.Reader);
        await using var connection = new NpgsqlConnection(f.Index.Host.ConnectionString);
        await connection.OpenAsync();
        await using var deactivate = new NpgsqlCommand("""UPDATE "UserAccount" SET "IsActive"=false WHERE "Id"=@id""", connection);
        deactivate.Parameters.AddWithValue("id", f.Reader.Id);
        var revocation = deactivate.ExecuteNonQueryAsync();
        await WaitForLockWaitAsync(f, """UPDATE "UserAccount" SET "IsActive"=false""");
        Assert.False(revocation.IsCompleted);
        barrier.Resume();
        var released = await completion;
        Assert.Equal(AiMessageState.Answered, released.State);
        Assert.Equal(1, await revocation.WaitAsync(TimeSpan.FromSeconds(30)));
        // From now on the account is refused at the door.
        await AnswerFlowFixture.FailsAsync(401, null, () => f.StartAsync(f.Reader, "Jak obnovit data?"));
    }

    [PostgreSqlFact]
    public async Task Excluding_a_space_waits_for_the_release_gate_before_changing_its_AI_setting()
    {
        var barrier = new GateBarrier();
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning, services =>
            services.AddScoped<IContentAccess>(provider => new PausedContentAccess(ActivatorUtilities.CreateInstance<ContentAccessService>(provider), barrier)));
        await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSpusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        f.Fake.ChatAnswer = FakeAi.Answers.Answerable((AnswerMarker + " spusťte nástroj.", [1]));
        var completion = await ReleaseUntilPausedAsync(f, barrier, f.Reader);
        var exclusion = f.Index.As<SpaceAiSettingsView>(f.Admin,
            new SaveSpaceAiSettings(f.Index.SpaceId, new SaveSpaceAiSettingsRequest(false, null)));
        try
        {
            // Either the exclusion waits on the gate's space lock, or it commits: the latter violates R2.
            var waiting = WaitForLockWaitAsync(f, "SELECT \"Id\" FROM \"Space\"");
            var first = await Task.WhenAny(exclusion, waiting);
            Assert.Same(waiting, first);
            await waiting;
            Assert.False(exclusion.IsCompleted);
        }
        finally
        {
            barrier.Resume();
        }
        Assert.Equal(AiMessageState.Answered, (await completion).State);
        Assert.False((await exclusion).Included);
        Assert.Equal(AiMessageState.NoSources, (await f.StartAsync(f.Reader, "Jak obnovit data?")).State);
    }

    [PostgreSqlFact]
    public async Task A_republished_source_during_generation_is_not_released()
    {
        await using var f = await AnswerFlowFixture.CreateAsync(AnswerFlowTests.Meaning);
        var page = await f.PublishAsync("Návod pro správce", "## Postup obnovy\nSTARÝ-TEXT spusťte nástroj.", null, ("Postup obnovy", "postup"));
        await f.IndexAsync();
        f.Fake.ChatAnswer = FakeAi.Answers.EchoingSource(1);
        var prepared = await f.StartAsync(f.Reader, "Jak obnovit data?");
        var hold = f.Fake.Hold(AiProfileKind.Chat);
        var completion = f.CompleteAsync(f.Reader, prepared.MessageId);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        // The author removes the text while the model writes from it.
        await f.Index.EditAndPublishAsync(page, "Návod pro správce", "## Postup obnovy\nNový postup.", ("Postup obnovy", "postup"));
        hold.Release();

        var answer = await completion;
        Assert.Equal((AiMessageState.Failed, AiAnswerProblems.SourcesChanged), (answer.State, answer.ErrorCode));
        Assert.Empty(answer.Sentences);
        Assert.Contains("STARÝ-TEXT", Assert.Single(f.ChatCalls).Inputs[^1]);
        var (stored, _) = await f.StoredAsync(prepared.MessageId);
        Assert.Equal((AiMessageState.Failed, (AiStoredAnswer?)null), (stored.State, stored.Answer));
        Assert.False(await f.AnyStoredAnswerContainsAsync("STARÝ-TEXT"));

        // Asked once more (once the worker has indexed the new revision), the answer comes from the current one.
        await f.IndexAsync();
        var again = await f.AskAsync(f.Reader, "Jak obnovit data?");
        Assert.Equal("Nový postup.", Assert.Single(again.Answer.Sentences).Text);
    }

    private static async Task<Case> ArrangeAsync(AnswerFlowFixture f, Revocation revocation, Moment moment)
    {
        var host = f.Index.Host;
        var reader = await host.SeedUserAsync("reader-" + moment);
        await host.GrantAiAsync(reader);
        Guid? group = null;
        Guid space;
        if (revocation == Revocation.GroupMembershipRemoved)
        {
            group = await host.SeedGroupAsync("Skupina " + moment, reader);
            space = await host.SeedSpaceAsync("Prostor " + moment, (f.Writer, SpaceRole.Writer));
            await f.ExecuteAsync("""
                INSERT INTO "SpaceGrant"("Id","SpaceId","GroupId","Role","ActiveFrom") VALUES (gen_random_uuid(),@space,@group,@role,clock_timestamp())
                """, ("space", space), ("group", group.Value), ("role", (int)SpaceRole.Reader));
        }
        else
        {
            space = await host.SeedSpaceAsync("Prostor " + moment, (f.Writer, SpaceRole.Writer), (reader, SpaceRole.Reader));
        }
        var page = await f.PublishAsync("Návod " + moment, "## Postup obnovy\nKrok pro " + moment + ".", space, ("Postup obnovy", "postup"));
        return new Case(moment, reader, space, page, group);
    }

    private static Task RevokeAsync(AnswerFlowFixture f, Revocation revocation, Case c) => revocation switch
    {
        Revocation.GrantRemoved => f.ExecuteAsync("""
            UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "SpaceId"=@space AND "UserId"=@user AND "ActiveTo" IS NULL
            """, ("space", c.SpaceId), ("user", c.Reader.Id)),
        Revocation.GroupMembershipRemoved => f.ExecuteAsync("""
            UPDATE "GroupMember" SET "ActiveTo"=clock_timestamp() WHERE "GroupId"=@group AND "UserId"=@user AND "ActiveTo" IS NULL
            """, ("group", c.GroupId!.Value), ("user", c.Reader.Id)),
        // The grant row is history: the current interval closes and the grant goes on with a validity that has ended.
        Revocation.GrantExpired => f.ExecuteAsync("""
            UPDATE "SpaceGrant" SET "ActiveTo"=clock_timestamp() WHERE "SpaceId"=@space AND "UserId"=@user AND "ActiveTo" IS NULL;
            INSERT INTO "SpaceGrant"("Id","SpaceId","UserId","Role","ActiveFrom","ExpiresAt")
            VALUES (gen_random_uuid(),@space,@user,@role,clock_timestamp(),clock_timestamp()-interval '1 second')
            """, ("space", c.SpaceId), ("user", c.Reader.Id), ("role", (int)SpaceRole.Reader)),
        Revocation.OwnAccess => f.ExecuteAsync("""UPDATE "Page" SET "OwnAccess"=true WHERE "Id"=@id""", ("id", c.PageId)),
        Revocation.AccountDeactivated => f.ExecuteAsync("""UPDATE "UserAccount" SET "IsActive"=false WHERE "Id"=@id""", ("id", c.Reader.Id)),
        Revocation.SecurityStampChanged => f.ExecuteAsync("""UPDATE "UserAccount" SET "SecurityStamp"='revoked' WHERE "Id"=@id""", ("id", c.Reader.Id)),
        Revocation.MovedToUnreadableSpace => f.ExecuteAsync("""UPDATE "Page" SET "SpaceId"=@space WHERE "Id"=@id""",
            ("id", c.PageId), ("space", f.Index.OtherSpaceId)),
        Revocation.Trashed => f.ExecuteAsync("""
            UPDATE "Page" SET "IsTrashed"=true,"TrashedAt"=now(),"TrashedById"=@admin,"TrashBatchId"=gen_random_uuid() WHERE "Id"=@id
            """, ("id", c.PageId), ("admin", f.Admin.Id)),
        Revocation.Unpublished => f.ExecuteAsync("""UPDATE "Page" SET "PublishedRevisionId"=NULL WHERE "Id"=@id""", ("id", c.PageId)),
        Revocation.SpaceExcludedFromAi => f.ExecuteAsync("""
            INSERT INTO "SpaceAiSetting"("Id","SpaceId","Included","ChangedById","ActiveFrom") VALUES (gen_random_uuid(),@space,false,@admin,now())
            """, ("space", c.SpaceId), ("admin", f.Admin.Id)),
        _ => throw new ArgumentOutOfRangeException(nameof(revocation)),
    };

    /// <summary>A prepared answer whose model call returns at once and whose release then pauses right after the gate's actor and
    /// space locks (<see cref="PausedContentAccess"/>).</summary>
    private static async Task<Task<AiMessageView>> ReleaseUntilPausedAsync(AnswerFlowFixture f, GateBarrier barrier, UserAccount actor)
    {
        var prepared = await f.StartAsync(actor, "Jak obnovit data?");
        var hold = f.Fake.Hold(AiProfileKind.Chat);
        var completion = f.CompleteAsync(actor, prepared.MessageId);
        // The check before the model has passed once the model holds; only the release's locks pause.
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        barrier.Arm();
        hold.Release();
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        return completion;
    }

    /// <summary>Until a backend of this schema waits on a lock for the given statement (pg_stat_activity), bounded.</summary>
    private static async Task WaitForLockWaitAsync(AnswerFlowFixture f, string statementStart)
    {
        await using var connection = new NpgsqlConnection(f.Index.Host.ConnectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND starts_with(query,@statement)
                """, connection);
            command.Parameters.AddWithValue("statement", statementStart);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) > 0)
            {
                return;
            }
            Assert.True(DateTime.UtcNow < deadline, "The revocation never waited on the gate's locks.");
            await Task.Yield();
        }
    }

    private sealed class GateBarrier
    {
        private TaskCompletionSource m_entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource m_resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int m_armed;

        public Task Entered => m_entered.Task;

        public void Arm()
        {
            m_entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            m_resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref m_armed, 1);
        }

        public void Resume() => m_resume.TrySetResult();

        internal async Task PauseAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref m_armed, 0) == 1)
            {
                m_entered.TrySetResult();
                await m_resume.Task.WaitAsync(ct);
            }
        }
    }

    /// <summary>The real content access; once armed, the next lock of the actor in spaces (the gate's) pauses after it is taken.</summary>
    private sealed class PausedContentAccess(IContentAccess inner, GateBarrier barrier) : IContentAccess
    {
        public Task<UserAccount> LockActorAsync(Guid? spaceId, CancellationToken ct) => inner.LockActorAsync(spaceId, ct);

        public async Task<UserAccount> LockActorInSpacesAsync(IReadOnlyCollection<Guid> spaceIds, CancellationToken ct)
        {
            var actor = await inner.LockActorInSpacesAsync(spaceIds, ct);
            await barrier.PauseAsync(ct);
            return actor;
        }

        public Task<SpaceRole> RequireSpaceAsync(Guid spaceId, SpaceRole required, CancellationToken ct) => inner.RequireSpaceAsync(spaceId, required, ct);

        public Task<PageAuthorization> AuthorizePageAsync(Page page, AccessOperation operation, CancellationToken ct) => inner.AuthorizePageAsync(page, operation, ct);

        public Task<IReadOnlyList<PageAuthorization>> AuthorizePagesAsync(IReadOnlyList<Page> pages, AccessOperation operation, CancellationToken ct)
            => inner.AuthorizePagesAsync(pages, operation, ct);

        public Task<PageAuthorization> RequirePageAsync(Page page, AccessOperation operation, CancellationToken ct) => inner.RequirePageAsync(page, operation, ct);
    }
}
