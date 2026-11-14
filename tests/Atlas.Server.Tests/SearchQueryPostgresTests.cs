using Npgsql;
using Atlas.Server.Features.PageOrganization.Commands;
using Atlas.Server.Features.PageOrganization.Contracts;
using Atlas.Server.Features.PageOrganization.Queries;
using Atlas.Server.Features.Search;
using Atlas.Server.Features.Search.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Tests.Platform;
using Atlas.Server.Tests.Search;
using Xunit;
namespace Atlas.Server.Tests;
public sealed class SearchQueryPostgresTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Draft_search_filters_private_ownership_roles_and_revocation_before_pagination()
    {
        await using var f = await SearchFixture.CreateAsync();
        var own = await f.Create("Needle own draft");
        var other = await f.Pages.CreateAsync(new(f.SpaceId, null, "Needle other draft", SearchFixture.Rich("Needle secret"), "Needle secret"), f.OtherWriter, default);
        var shared = await f.Create("Needle shared publication");
        await f.Pages.PublishAsync(shared.Id, new(1), f.Writer, default);
        await f.Pages.SaveAsync(shared.Id, new("Needle shared working", SearchFixture.Rich("Needle working"), "Needle working", 1), f.Writer, default);
        var unchanged = await f.Create("Needle unchanged publication");
        await f.Pages.PublishAsync(unchanged.Id, new(1), f.Writer, default);
        var options = new SearchOptions(Content: "drafts");
        var reader = await f.Find(f.Reader, "needle", options, limit: 1);
        Assert.Empty(reader.Items); Assert.Empty(reader.Spaces); Assert.False(reader.HasMore);
        var first = await f.Find(f.Writer, "needle", options, limit: 1);
        var second = await f.Find(f.Writer, "needle", options, offset: 1, limit: 1);
        Assert.True(first.HasMore); Assert.False(second.HasMore);
        Assert.Equal(new[] { own.Id, shared.Id }.Order(), first.Items.Concat(second.Items).Select(hit => hit.Id).Order());
        Assert.All(first.Items.Concat(second.Items), hit => { Assert.True(hit.IsDraft); Assert.NotNull(hit.ChangedAt); });
        var otherResults = await f.Find(f.OtherWriter, "needle", options);
        Assert.Equal(new[] { other.Id, shared.Id }.Order(), otherResults.Items.Select(hit => hit.Id).Order());
        Assert.Equal(3, (await f.Find(f.Admin, "needle", options, f.SpaceId)).Items.Count);
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=@id AND \"ActiveTo\" IS NULL", f.Writer.Id, []);
        Assert.Empty((await f.Find(f.Writer, "needle", options)).Items);
        Assert.DoesNotContain((await f.Find(f.Reader, "needle")).Items, hit => hit.Id == own.Id || hit.Id == other.Id);
    }

    [PostgreSqlFact]
    public async Task Search_change_filters_and_latest_order_apply_before_pagination_for_both_scopes()
    {
        await using var f = await SearchFixture.CreateAsync();
        var old = await f.Create("Needle");
        await f.Pages.PublishAsync(old.Id, new(1), f.Writer, default);
        await f.Execute("""
            UPDATE "PageRevision" SET "ActiveFrom"=clock_timestamp()-interval '40 days 1 minute' WHERE "PageId"=@id;
            UPDATE "PagePublication" SET "ActiveFrom"=clock_timestamp()-interval '40 days' WHERE "PageId"=@id;
            UPDATE "SearchDocument" SET "Body"="Body" WHERE "PageId"=@id;
            """, old.Id);
        var recent = await f.Create("Needle recently published");
        await f.Pages.PublishAsync(recent.Id, new(1), f.Writer, default);
        Assert.Equal(old.Id, (await f.Find(f.Reader, "needle")).Items[0].Id);
        var latest = await f.Find(f.Reader, "needle", new SearchOptions(Sort: "published"), limit: 1);
        Assert.Equal(recent.Id, Assert.Single(latest.Items).Id); Assert.True(latest.HasMore);
        foreach (var interval in new[] { "day", "week", "month" })
        {
            var filtered = await f.Find(f.Reader, "needle", new SearchOptions(ChangedWithin: interval), limit: 1);
            Assert.Equal(recent.Id, Assert.Single(filtered.Items).Id); Assert.False(filtered.HasMore);
        }
        var oldDraft = await f.Create("Needle old draft");
        await f.Execute("UPDATE \"PageRevision\" SET \"ActiveFrom\"=clock_timestamp()-interval '40 days' WHERE \"PageId\"=@id", oldDraft.Id);
        var recentDraft = await f.Create("Needle current draft");
        var drafts = await f.Find(f.Writer, "needle", new SearchOptions(Content: "drafts", ChangedWithin: "week", Sort: "published"), limit: 1);
        Assert.Equal(recentDraft.Id, Assert.Single(drafts.Items).Id); Assert.False(drafts.HasMore);
        Assert.True(drafts.Items[0].IsDraft);
    }

    [PostgreSqlFact]
    public async Task Search_rejects_unknown_filter_values_instead_of_silently_broadening_results()
    {
        await using var f = await SearchFixture.CreateAsync();
        foreach (var options in new[] { new SearchOptions(Content: "trashed"), new SearchOptions(ChangedWithin: "year"), new SearchOptions(Sort: "random") })
            await Denied(400, () => f.Find(f.Admin, "needle", options));
    }

    /// <summary>Review U3-2: ts_headline cannot see an accented word, so the fragment of a requirement page is its summary at the top;
    /// the hit's snippet is the matched block, published and working.</summary>
    [PostgreSqlFact]
    public async Task A_requirement_page_hit_snippets_the_matched_block_not_its_summary()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("KBN — Production");
        var draft = await f.Create("Kostelec — Production");
        const string Write = """
            UPDATE "PageRevision" SET "Markdown"=replace($md$**Stav nasazení§** 0 % hotovo (0 z 2) · nedodané vstupy§ SMS brána (IT KBN), Strukturované požadavky

            ## Portál

            Produkční nasazení portálu pro nemocnici, se vším, co k němu patří§ servery, přístupy, certifikáty a vstupy od zákazníka.

            - [ ] **SMS brána** — čeká na vstup · vstup · od§ IT KBN · klíč§ portal.sms
            - [ ] **Strukturované požadavky** — neřešeno · vstup · klíč§ portal.requirements$md$, '§', chr(58)) WHERE "PageId"=@id
            """;
        await f.Execute(Write, page.Id);
        await f.Execute(Write, draft.Id);
        await f.Pages.PublishAsync(page.Id, new PublishPageRequest(page.Version), f.Writer, default);

        foreach (var (content, found) in new[] { ("published", page.Id), ("drafts", draft.Id) })
        {
            var sms = Assert.Single((await f.Find(f.Writer, "SMS brána", new SearchOptions(Content: content))).Items);
            var structured = Assert.Single((await f.Find(f.Writer, "Strukturované", new SearchOptions(Content: content))).Items);
            Assert.Equal((found, found), (sms.Id, structured.Id));
            Assert.Equal(("…SMS brána — čeká na vstup · vstup · od: IT KBN…", "…Strukturované požadavky — neřešeno · vstup"), (sms.Snippet, structured.Snippet));
        }
    }

    [PostgreSqlFact]
    public async Task Draft_group_access_and_headings_use_only_current_nontrashed_revisions()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("Published handbook");
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        const string rich = """{"format":"atlas-rich","version":1,"plugins":[{"id":"heading-anchors","version":1}],"document":{"type":"doc","content":[{"type":"heading","attrs":{"level":2,"headingId":"needle-anchor"},"content":[{"type":"text","text":"Needle restoration"}]}]}}""";
        var working = await f.Pages.SaveAsync(page.Id, new("Working handbook", rich, "## Needle restoration", 1), f.Writer, default);
        await f.Create("Needle private draft");
        await f.Execute("""
            INSERT INTO "UserGroup"("Id","Name") VALUES(gen_random_uuid(),'Draft writers '||CAST(@id AS text));
            INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom")
              SELECT gen_random_uuid(),"Id",@id,clock_timestamp() FROM "UserGroup" WHERE "Name"='Draft writers '||CAST(@id AS text);
            INSERT INTO "SpaceGrant"("Id","SpaceId","GroupId","Role","ActiveFrom")
              SELECT gen_random_uuid(),g."SpaceId",u."Id",2,clock_timestamp()
              FROM "SpaceGrant" g CROSS JOIN "UserGroup" u WHERE g."UserId"=@id AND g."ActiveTo" IS NULL AND u."Name"='Draft writers '||CAST(@id AS text);
            """, f.Reader.Id, []);
        var options = new SearchOptions(Content: "drafts");
        var hit = Assert.Single((await f.Find(f.Reader, "needle", options)).Items);
        Assert.Equal(page.Id, hit.Id); Assert.Equal("needle-anchor", hit.HeadingId); Assert.Equal("Working handbook", hit.Title);
        Assert.Empty((await f.Find(f.Reader, "needle", options, Guid.NewGuid())).Items);
        // The group grant is what makes this draft readable, so the unified default must show it too - while
        // the published scope still must not, because the term exists only in the working revision.
        Assert.Equal(page.Id, Assert.Single((await f.Find(f.Reader, "needle")).Items).Id);
        Assert.Empty((await f.Find(f.Reader, "needle", new SearchOptions(Content: "published"))).Items);
        await f.Execute("UPDATE \"GroupMember\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=@id AND \"ActiveTo\" IS NULL", f.Reader.Id, []);
        Assert.Empty((await f.Find(f.Reader, "needle", options)).Items);
        var tree = await f.As<PageTreeDto>(f.Writer, new GetPageTree(f.SpaceId));
        await f.As<OrganizationChange>(f.Writer, new TrashPage(page.Id, new TrashPageRequest(false, tree.Version, working.Version)));
        Assert.DoesNotContain((await f.Find(f.Admin, "needle", options, f.SpaceId)).Items, item => item.Id == page.Id);
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=@id", f.Writer.Id, []);
        await Denied(401, () => f.Find(f.Writer, "needle", options));
    }

    [PostgreSqlFact]
    public async Task Draft_quoted_phrases_do_not_cross_the_title_and_body_boundary()
    {
        await using var f = await SearchFixture.CreateAsync();
        var split = await f.Pages.CreateAsync(new(f.SpaceId, null, "Operations silver", SearchFixture.Rich("falcon handbook"), "falcon handbook"), f.Writer, default);
        var body = await f.Pages.CreateAsync(new(f.SpaceId, null, "Operations handbook", SearchFixture.Rich("silver falcon"), "silver falcon"), f.Writer, default);
        var result = await f.Find(f.Writer, "\"silver falcon\"", new SearchOptions(Content: "drafts"));
        Assert.Equal(body.Id, Assert.Single(result.Items).Id);
        Assert.DoesNotContain(result.Items, hit => hit.Id == split.Id);
    }

    [PostgreSqlFact]
    public async Task Draft_titles_outrank_real_markdown_headings_which_outrank_body_matches()
    {
        await using var f = await SearchFixture.CreateAsync();
        var title = await f.Create("Needle restoration");
        var heading = await f.Pages.CreateAsync(new(f.SpaceId, null, "Zzz handbook", """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Needle restoration"}]}]}}""", "## Needle restoration"), f.Writer, default);
        var body = await f.Pages.CreateAsync(new(f.SpaceId, null, "Aaa handbook", SearchFixture.Rich("Needle restoration"), "Needle restoration"), f.Writer, default);
        var result = await f.Find(f.Writer, "needle restoration", new SearchOptions(Content: "drafts"));
        Assert.Equal(new[] { title.Id, heading.Id, body.Id }, result.Items.Select(hit => hit.Id));
    }

    [PostgreSqlFact]
    public async Task Draft_fuzzy_fallback_keeps_later_pages_and_has_more_after_primary_matches()
    {
        await using var f = await SearchFixture.CreateAsync();
        var first = await f.Create("Zaloha alpha");
        var second = await f.Create("Zaloha beta");
        var fuzzy = await f.Create("Zalohy gamma");
        var options = new SearchOptions(Content: "drafts");
        var page1 = await f.Find(f.Writer, "zaloha", options, limit: 1);
        Assert.Equal(first.Id, Assert.Single(page1.Items).Id); Assert.True(page1.HasMore);
        var page2 = await f.Find(f.Writer, "zaloha", options, offset: 1, limit: 1);
        Assert.Equal(second.Id, Assert.Single(page2.Items).Id); Assert.True(page2.HasMore);
        var page3 = await f.Find(f.Writer, "zaloha", options, offset: 2, limit: 1);
        Assert.Equal(fuzzy.Id, Assert.Single(page3.Items).Id); Assert.False(page3.HasMore);
    }

    [PostgreSqlFact]
    public async Task Working_search_projection_follows_revision_transactions_and_can_be_rebuilt()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("Original working title");
        var original = await f.WorkingProjection(page.Id);
        Assert.Equal("original working title", original.Title);
        await f.Pages.SaveAsync(page.Id, new("Changed working title", SearchFixture.Rich("Current body"), "Current body", 1), f.Writer, default);
        var current = await f.WorkingProjection(page.Id);
        Assert.NotEqual(original.RevisionId, current.RevisionId);
        Assert.Equal("changed working title", current.Title);
        await f.Execute("UPDATE \"PageRevision\" SET \"ActiveTo\"=\"ActiveTo\" WHERE \"PageId\"=@id AND \"ActiveTo\" IS NOT NULL", page.Id);
        Assert.Equal(current, await f.WorkingProjection(page.Id));
        // The refresh runs in the writer's transaction (SearchDocuments): a page that lost its open revision loses its document, and a
        // rollback keeps both.
        using (var session = f.Database.OpenSession())
        using (var transaction = session.BeginTransaction())
        {
            await session.CreateSQLQuery("UPDATE \"PageRevision\" SET \"ActiveTo\"=:boundary WHERE \"PageId\"=:id AND \"ActiveTo\" IS NULL")
                .SetParameter("boundary", DateTime.UtcNow).SetParameter("id", page.Id).ExecuteUpdateAsync();
            await SearchDocuments.RefreshWorkingAsync(session, [page.Id], default);
            Assert.Equal(0L, await session.CreateSQLQuery("SELECT COUNT(*) FROM \"WorkingSearchDocument\" WHERE \"PageId\"=:id")
                .SetParameter("id", page.Id).UniqueResultAsync<long>());
            await transaction.RollbackAsync();
        }
        Assert.Equal(current, await f.WorkingProjection(page.Id));
        // A lost document comes back from the refresh of its page (the statement the rebuild job runs for each batch).
        await f.Execute("""DELETE FROM "WorkingSearchDocument" WHERE "PageId"=@id""", page.Id);
        Assert.Equal(current, await f.WorkingProjection(page.Id));
    }

    [PostgreSqlFact]
    public async Task Dense_draft_fuzzy_matches_keep_exact_order_and_later_pages_in_both_sort_modes()
    {
        await using var f = await SearchFixture.CreateAsync();
        foreach (var owner in new[] { f.Writer, f.OtherWriter })
        await f.ExecuteInSpace("""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT CAST(md5('dense-'||CAST(@id AS text)||n) AS uuid),g."SpaceId",@id,1 FROM generate_series(1,80) n
              CROSS JOIN "SpaceGrant" g WHERE g."UserId"=@id AND g."ActiveTo" IS NULL;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT gen_random_uuid(),CAST(md5('dense-'||CAST(@id AS text)||n) AS uuid),1,'Handbook '||lpad(CAST(n AS text),2,'0'),
                '{}','zalohy',@id,clock_timestamp()-interval '1 day'+n*interval '1 minute','web' FROM generate_series(1,80) n;
            """, owner.Id);
        var overlap = await f.Create("Zalohy overlap");
        var primary = await f.Create("Zaloha");
        await f.Pages.CreateAsync(new(f.SpaceId, null, "Zaloha private", SearchFixture.Rich("zalohy"), "zalohy"), f.OtherWriter, default);
        foreach (var sort in new[] { "relevance", "published" })
        {
            var options = new SearchOptions(Content: "drafts", Sort: sort);
            var first = await f.Find(f.Writer, "zaloha", options, limit: 50);
            var last = await f.Find(f.Writer, "zaloha", options, offset: 50, limit: 50);
            Assert.True(first.HasMore); Assert.False(last.HasMore);
            var items = first.Items.Concat(last.Items).ToArray();
            Assert.Equal(82, items.Length); Assert.Equal(82, items.Select(hit => hit.Id).Distinct().Count());
            Assert.Equal(primary.Id, items[0].Id);
            Assert.Equal(overlap.Id, items[1].Id);
            var expected = Enumerable.Range(1, 80).Select(n => $"Handbook {n:00}");
            if (sort == "published")
            {
                expected = expected.Reverse();
            }
            Assert.Equal(expected, items.Skip(2).Select(hit => hit.Title));
        }
    }

    [PostgreSqlFact]
    public async Task Saved_draft_corpus_returns_bounded_results_for_common_and_absent_terms()
    {
        await using var f = await SearchFixture.CreateAsync(pooling: true);
        await f.ExecuteInSpace("""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT CAST(md5('draft-perf-'||CAST(@id AS text)||n) AS uuid),g."SpaceId",@id,1 FROM generate_series(1,1000) n
              CROSS JOIN "SpaceGrant" g WHERE g."UserId"=@id AND g."ActiveTo" IS NULL;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT gen_random_uuid(),CAST(md5('draft-perf-'||CAST(@id AS text)||n) AS uuid),1,'Provozní příručka '||n,
                CAST(jsonb_build_object('format','atlas-rich','version',1,'document',jsonb_build_object('type','doc','content',jsonb_build_array(jsonb_build_object('type','paragraph','content',jsonb_build_array(jsonb_build_object('type','text','text',body)))))) AS text),
                body,@id,clock_timestamp(),'web' FROM generate_series(1,1000) n CROSS JOIN (
                  SELECT repeat('Postup obnovy zálohy popisuje kontrolu souborů a ověření výsledku. Při chybě HTTP 401 zkontrolujte přihlášení a oprávnění účtu. ',40) AS body
                ) content;
            ANALYZE "Page";
            ANALYZE "PageRevision";
            ANALYZE "WorkingSearchDocument";
            """, f.Writer.Id);
        var timings = new List<object>();
        foreach (var query in new[] { "obnovy zalohy", "HTTP 401", "unmatchedzephyr" })
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await f.Find(f.Writer, query, new SearchOptions(Content: "drafts"));
            Assert.InRange(result.Items.Count, 0, 20);
            if (query == "unmatchedzephyr")
            {
                Assert.Empty(result.Items);
            }
            else { Assert.Equal(20, result.Items.Count); Assert.True(result.HasMore); }
            timings.Add(new { query, milliseconds = watch.Elapsed.TotalMilliseconds, count = result.Items.Count });
        }
        if (Environment.GetEnvironmentVariable("ATLAS_DRAFT_PERF_OUTPUT") is { Length: > 0 } output)
        {
            var repeated = new List<object>();
            foreach (var sort in new[] { "relevance", "published" })
            foreach (var query in new[] { "obnovy zalohy", "HTTP 401", "unmatchedzephyr", "obnovyy zalohy" })
            {
                var samples = new List<double>();
                for (var i = 0; i < 20; i++)
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var result = await f.Find(f.Writer, query, new SearchOptions(Content: "drafts", Sort: sort));
                    samples.Add(timer.Elapsed.TotalMilliseconds);
                    Assert.Equal(query == "unmatchedzephyr" ? 0 : 20, result.Items.Count);
                }
                repeated.Add(new { query, sort, samples, p95Milliseconds = samples.Order().ElementAt(18) });
            }
            var plans = new Dictionary<string, string>();
            foreach (var query in new[] { "obnovy zalohy", "unmatchedzephyr", "obnovyy zalohy" }) plans[query] = await f.ExplainDraftSearch(query);
            plans["obnovyy zalohy/latest"] = await f.ExplainDraftSearch("obnovyy zalohy", latest: true);
            var writeBody = string.Concat(Enumerable.Repeat("Postup obnovy zálohy vyžaduje ověření souborů a kontrolu oprávnění. ", 80));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var saved = await f.Pages.CreateAsync(new(f.SpaceId, null, "Write latency probe", SearchFixture.Rich(writeBody), writeBody), f.Writer, default);
            var createMilliseconds = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            await f.Pages.SaveAsync(saved.Id, new("Updated latency probe", SearchFixture.Rich(writeBody), writeBody, 1), f.Writer, default);
            await File.WriteAllTextAsync(output, System.Text.Json.JsonSerializer.Serialize(new { documents = 1000, paragraphRepetitions = 40,
                pooling = new NpgsqlConnectionStringBuilder(f.ConnectionString).Pooling,
                timings, repeated, createMilliseconds, saveMilliseconds = watch.Elapsed.TotalMilliseconds, plans }));
        }
    }

    [PostgreSqlFact]
    public async Task Pagination_filters_spaces_without_duplicates_and_returns_accessible_space_matches()
    {
        await using var f = await SearchFixture.CreateAsync();
        await f.Execute("UPDATE \"Space\" SET \"Name\"='Příručky' WHERE \"Id\"=@id", f.SpaceId, []);
        for (var i=0;i<6;i++)
        {
            var page = await f.Create("Příručky " + i);
            await f.Pages.PublishAsync(page.Id,new(1),f.Writer,default);
        }
        var first=await f.Find(f.Reader,"prirucky",limit:2);
        Assert.Equal(2,first.Items.Count); Assert.True(first.HasMore); Assert.Equal(2,first.NextOffset);
        Assert.Equal(f.SpaceId,Assert.Single(first.Spaces).Id);
        var next=await f.Find(f.Reader,"prirucky",offset:2,limit:2);
        Assert.Empty(first.Items.Select(x=>x.Id).Intersect(next.Items.Select(x=>x.Id)));
        var last=await f.Find(f.Reader,"prirucky",offset:4,limit:2);
        Assert.False(last.HasMore); Assert.Null(last.NextOffset);
        var filtered=await f.Find(f.Reader,"prirucky",null,Guid.NewGuid(),0,2);
        Assert.Empty(filtered.Items); Assert.Empty(filtered.Spaces); Assert.False(filtered.HasMore);
        await Denied(400,()=>f.Find(f.Reader,"prirucky",offset:-1));
        await Denied(400,()=>f.Find(f.Reader,"prirucky",limit:51));
        // No last page any more (infinite results): an offset past the old 1000 is an empty page, only an overflowing one is refused.
        Assert.Empty((await f.Find(f.Reader,"prirucky",offset:1001)).Items);
        await Denied(400,()=>f.Find(f.Reader,"prirucky",offset:int.MaxValue));
    }

    [PostgreSqlFact]
    public async Task Paging_continues_past_a_thousand_results_until_they_end()
    {
        await using var f = await SearchFixture.CreateAsync();
        await f.ExecuteInSpace("""
            INSERT INTO "Page"("Id","SpaceId","OwnerId","Version")
              SELECT CAST(md5('paging-'||CAST(@id AS text)||n) AS uuid),g."SpaceId",@id,1 FROM generate_series(1,1003) n
              CROSS JOIN "SpaceGrant" g WHERE g."UserId"=@id AND g."ActiveTo" IS NULL;
            INSERT INTO "PageRevision"("Id","PageId","Version","Title","ContentJson","Markdown","AuthorId","ActiveFrom","Channel")
              SELECT gen_random_uuid(),CAST(md5('paging-'||CAST(@id AS text)||n) AS uuid),1,'Stránkování '||n,
                CAST(jsonb_build_object('format','atlas-rich','version',1,'document',jsonb_build_object('type','doc','content',jsonb_build_array())) AS text),
                '',@id,clock_timestamp(),'web' FROM generate_series(1,1003) n;
            """, f.Writer.Id);
        var options = new SearchOptions(Content: "drafts");
        var deep = await f.Find(f.Writer, "strankovani", options, offset: 1000, limit: 2);
        Assert.Equal(2, deep.Items.Count); Assert.True(deep.HasMore); Assert.Equal(1002, deep.NextOffset);
        var last = await f.Find(f.Writer, "strankovani", options, offset: 1002, limit: 2);
        Assert.Single(last.Items); Assert.False(last.HasMore); Assert.Null(last.NextOffset);
    }
    [PostgreSqlFact]
    public async Task Search_revalidates_current_admin_status_stamp_and_space_grants()
    {
        await using var f=await SearchFixture.CreateAsync();
        var page=await f.Create("Secrets"); await f.Pages.PublishAsync(page.Id,new(1),f.Writer,default);
        Assert.Single((await f.Find(f.Admin,"Secrets",null,f.SpaceId)).Items);
        await f.Execute("UPDATE \"UserAccount\" SET \"IsWikiAdmin\"=false WHERE \"Id\"=@id",f.Admin.Id,[]);
        Assert.Empty((await f.Find(f.Admin,"Secrets",null,f.SpaceId)).Items);
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='revoked' WHERE \"Id\"=@id",f.Reader.Id,[]);
        await Denied(401,()=>f.Find(f.Reader,"Secrets"));
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=@id AND \"ActiveTo\" IS NULL",f.Writer.Id,[]);
        Assert.Empty((await f.Find(f.Writer,"Secrets")).Items);
    }
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Titles_outrank_headings_which_outrank_body_and_only_real_published_anchors_are_returned()
    {
        await using var f = await SearchFixture.CreateAsync();
        var title = await f.Create("Obnova zálohy");
        const string rich = """{"format":"atlas-rich","version":1,"plugins":[{"id":"heading-anchors","version":1}],"document":{"type":"doc","content":[""" +
            """{"type":"heading","attrs":{"level":2,"headingId":"restore-backup"},"content":[{"type":"text","text":"Obnova zálohy"}]},""" +
            """{"type":"paragraph","content":[{"type":"text","text":"Postup obnovy."}]}]}}""";
        var heading = await f.Pages.CreateAsync(new(f.SpaceId,null,"Databáze",rich,"## Obnova zálohy\nPostup obnovy."),f.Writer,default);
        var body = await f.Pages.CreateAsync(new(f.SpaceId,null,"Provoz",SearchFixture.Rich(string.Join(' ',Enumerable.Repeat("Obnova zálohy",500))),null),f.Writer,default);
        foreach(var page in new[] {title,heading,body}) await f.Pages.PublishAsync(page.Id,new(1),f.Writer,default);
        var result = await f.Find(f.Reader,"obnova zalohy");
        Assert.Equal(new[] {title.Id,heading.Id,body.Id},result.Items.Select(x=>x.Id));
        var section=result.Items[1];
        Assert.Equal("Obnova zálohy",section.HeadingTitle); Assert.Equal("restore-backup",section.HeadingId);
        Assert.Equal(DateTimeKind.Utc,section.PublishedAt!.Value.Kind);
        Assert.DoesNotContain(",Obnova,",section.Snippet);
        Assert.Null(result.Items[2].HeadingId);
        await f.Pages.SaveAsync(heading.Id,new("Unpublished",SearchFixture.Rich("Draft"),"## Unpublished secret",1),f.Writer,default);
        Assert.Equal("restore-backup",(await f.Find(f.Reader,"obnova zalohy")).Items.Single(x=>x.Id==heading.Id).HeadingId);
        Assert.Empty((await f.Find(f.Reader,"unpublished secret")).Items);
        var typo=await f.Find(f.Reader,"zalohi");
        Assert.Contains(typo.Items,x=>x.Id==title.Id);
        using var session=f.Database.OpenSession();
        var extracted=await session.CreateSQLQuery("SELECT CAST(" + SearchSqlText.Headings(":markdown", ":rich") + " AS text)")
            .SetParameter("markdown","```cs\n## Fake heading\n```\n## Obnova zálohy")
            .SetParameter("rich",rich).UniqueResultAsync<string>();
        using var json=System.Text.Json.JsonDocument.Parse(extracted);
        Assert.Equal(1,json.RootElement.GetArrayLength());
        Assert.Equal("restore-backup",json.RootElement[0].GetProperty("headingId").GetString());
    }

    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Restricted_spaces_drafts_trash_and_counts_never_escape_filters()
    {
        await using var f=await SearchFixture.CreateAsync();
        var visible=await f.Create("Needle visible"); await f.Pages.PublishAsync(visible.Id,new(1),f.Writer,default);
        var draft=await f.Create("Needle draft");
        var restrictedSpace=Guid.NewGuid();
        using(var session=f.Database.OpenSession())
        using(var tx=session.BeginTransaction())
        {
            await session.SaveAsync(new Space {Id=restrictedSpace,Name="Needle restricted"});
            await tx.CommitAsync();
        }
        var hidden=await f.Pages.CreateAsync(new(restrictedSpace,null,"Needle private",SearchFixture.Rich("secret"),"secret"),f.Admin,default);
        await f.Pages.PublishAsync(hidden.Id,new(1),f.Admin,default);
        var result=await f.Find(f.Reader,"needle",limit:1);
        Assert.Equal(visible.Id,Assert.Single(result.Items).Id); Assert.False(result.HasMore); Assert.Empty(result.Spaces);
        Assert.Empty((await f.Find(f.Reader,"needle",null,restrictedSpace)).Items);
        var tree=await f.As<PageTreeDto>(f.Writer,new GetPageTree(f.SpaceId));
        var change=await f.As<OrganizationChange>(f.Writer,new TrashPage(visible.Id,new TrashPageRequest(false,tree.Version,1)));
        Assert.Empty((await f.Find(f.Reader,"needle")).Items);
        await f.As<OrganizationChange>(f.Admin,new UntrashPage(visible.Id,new UntrashPageRequest(null,false,change.Version)));
        Assert.Equal(visible.Id,Assert.Single((await f.Find(f.Reader,"needle",null,f.SpaceId)).Items).Id);
    }

    [PostgreSqlFact]
    public async Task Quoted_phrase_requires_word_order_and_disables_fuzzy_fallback()
    {
        await using var f=await SearchFixture.CreateAsync();
        var exact=await f.Create("HTTP 401");
        var separated=await f.Create("HTTP odpověď 401");
        var reversed=await f.Create("401 HTTP");
        foreach(var page in new[] {exact,separated,reversed}) await f.Pages.PublishAsync(page.Id,new(1),f.Writer,default);
        var result=await f.Find(f.Reader,"\"HTTP 401\"");
        Assert.Equal(exact.Id,Assert.Single(result.Items).Id);
        Assert.Equal(3,(await f.Find(f.Reader,"HTTP 401")).Items.Count);
    }

    [PostgreSqlFact]
    public async Task Previous_published_title_finds_current_article_without_exposing_a_draft_title()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("Nimbus legacy handbook");
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        var renamed = await f.Pages.SaveAsync(page.Id, new("Current documentation", SearchFixture.Rich("Current text"), "Current text", 1), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(renamed.Version), f.Writer, default);
        await f.Pages.SaveAsync(page.Id, new("Confidential volcano draft", SearchFixture.Rich("Draft"), "Draft", renamed.Version), f.Writer, default);
        var result = await f.Find(f.Reader, "\"Nimbus legacy\"");
        var hit = Assert.Single(result.Items);
        Assert.Equal(page.Id, hit.Id); Assert.Equal("Current documentation", hit.Title);
        Assert.Equal("Nimbus legacy handbook", hit.MatchedAlias);
        Assert.Empty((await f.Find(f.Reader, "\"Confidential volcano\"")).Items);
        var exact = await f.Create("Nimbus legacy");
        await f.Pages.PublishAsync(exact.Id, new(1), f.Writer, default);
        Assert.Equal(exact.Id, (await f.Find(f.Reader, "Nimbus legacy")).Items[0].Id);
        await f.Execute("UPDATE \"SpaceGrant\" SET \"ActiveTo\"=clock_timestamp() WHERE \"UserId\"=@id AND \"ActiveTo\" IS NULL", f.Reader.Id, []);
        Assert.Empty((await f.Find(f.Reader, "Nimbus legacy")).Items);
    }

    [PostgreSqlFact]
    public async Task Imported_path_is_searchable_only_after_its_checkpoint_has_been_published()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("Ordinary documentation");
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        var imported = await f.Pages.SaveAsync(page.Id, new("Imported documentation", SearchFixture.Rich("Imported"), "Imported", 1), f.Writer, default);
        // A source of this test's own: the shared database holds other tests' import sources.
        var source = $"alias-test-{Guid.NewGuid():N}";
        await f.Execute($"""
            INSERT INTO "WikiImportSource" ("Id","SourceKey") VALUES(gen_random_uuid(),'{source}');
            INSERT INTO "WikiImportPage" ("Id","SourceId","SourcePath","PageId")
              SELECT gen_random_uuid(),"Id",'engineering/zephyr-installation',@id FROM "WikiImportSource" WHERE "SourceKey"='{source}';
            INSERT INTO "WikiImportCheckpoint" ("Id","ImportPageId","RevisionId","SourceSha256","ActiveFrom")
              SELECT gen_random_uuid(),m."Id",r."Id",repeat('a',64),clock_timestamp()
              FROM "WikiImportPage" m JOIN "PageRevision" r ON r."PageId"=m."PageId" AND r."ActiveTo" IS NULL WHERE m."PageId"=@id;
            """, page.Id);
        Assert.Empty((await f.Find(f.Reader, "zephyr installation")).Items);
        await f.Pages.PublishAsync(page.Id, new(imported.Version), f.Writer, default);
        Assert.Equal("engineering/zephyr-installation", Assert.Single((await f.Find(f.Reader, "zephyr installation")).Items).MatchedAlias);
        var later = await f.Pages.SaveAsync(page.Id, new("Reviewed documentation", SearchFixture.Rich("Reviewed"), "Reviewed", imported.Version), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(later.Version), f.Writer, default);
        Assert.Equal("engineering/zephyr-installation", Assert.Single((await f.Find(f.Reader, "zephyr installation")).Items).MatchedAlias);
    }

    [PostgreSqlFact]
    public async Task Alias_phrases_do_not_cross_two_historical_titles()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Create("Falcon");
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        var second = await f.Pages.SaveAsync(page.Id, new("Silver", SearchFixture.Rich("Second"), "Second", 1), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(second.Version), f.Writer, default);
        var current = await f.Pages.SaveAsync(page.Id, new("Operations", SearchFixture.Rich("Routine"), "Routine", second.Version), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(current.Version), f.Writer, default);
        Assert.Single((await f.Find(f.Reader, "falcon")).Items);
        Assert.Single((await f.Find(f.Reader, "silver")).Items);
        Assert.Empty((await f.Find(f.Reader, "\"silver falcon\"")).Items);
        Assert.Empty((await f.Find(f.Reader, "\"falcon silver\"")).Items);
    }

    [PostgreSqlFact]
    public async Task Rotated_stamp_answers_401_before_any_filter_or_query_validation()
    {
        await using var f = await SearchFixture.CreateAsync();
        await f.Execute("UPDATE \"UserAccount\" SET \"SecurityStamp\"='rotated' WHERE \"Id\"=@id", f.Reader.Id, []);
        // The endpoint used to resolve the actor before it validated anything, so a revoked credential
        // answers 401 - never 400 for the window and never an empty 200 for a too-short query.
        await Denied(401, () => f.Find(f.Reader, "needle", limit: 51));
        await Denied(401, () => f.Find(f.Reader, "a"));
    }

    [PostgreSqlFact]
    public async Task A_word_inside_a_published_title_is_found_without_being_the_titles_prefix()
    {
        await using var f = await SearchFixture.CreateAsync();
        // The body deliberately omits the term, so only the title lexeme in "Vector" can match:
        // "NormalizedTitle" LIKE 'demo%' is anchored and never fires for a word this far into the title.
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Deployment in DEMO", SearchFixture.Rich("Rollout notes"), "Rollout notes"), f.Writer, default);
        await f.Pages.PublishAsync(page.Id, new(1), f.Writer, default);
        Assert.Equal(page.Id, Assert.Single((await f.Find(f.Reader, "demo")).Items).Id);
        Assert.Equal(page.Id, Assert.Single((await f.Find(f.Reader, "Deployment")).Items).Id);
    }

    [PostgreSqlFact]
    public async Task An_unpublished_title_is_absent_from_the_published_scope_and_present_in_drafts()
    {
        await using var f = await SearchFixture.CreateAsync();
        var page = await f.Pages.CreateAsync(new(f.SpaceId, null, "Deployment in DEMO", SearchFixture.Rich("Rollout notes"), "Rollout notes"), f.Writer, default);
        // atlas_refresh_search_document keeps "SearchDocument" empty while "PublishedRevisionId" is NULL,
        // so the default scope cannot see this page at all and only "WorkingSearchDocument" carries it.
        // Pinned because the two scopes answer to different rights - read for published, write for drafts.
        Assert.Empty((await f.Find(f.Writer, "demo", new SearchOptions(Content: "published"))).Items);
        Assert.Equal(page.Id, Assert.Single((await f.Find(f.Writer, "demo", new SearchOptions(Content: "drafts"))).Items).Id);
    }

    private static async Task Denied(int status, Func<Task> action)
        => Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
}
