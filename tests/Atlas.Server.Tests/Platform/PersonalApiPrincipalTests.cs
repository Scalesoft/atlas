using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Atlas.Server.Features.PersonalApi.Contracts;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.PersonalApi.Commands;
using Atlas.Server.Features.PersonalApi.Domain;
using Xunit;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Tests.Platform;

public sealed class PersonalApiPrincipalTests
{
    [PostgreSqlFact]
    public async Task Bearer_principal_carries_the_security_stamp_that_HttpCurrentActor_requires()
    {
        // The options the authentication handler reads come with the generic host.
        await using var host = await AtlasTestHost.CreateAsync("atlas_personal_api_", Atlas.Server.Tests.PersonalApi.PersonalApiFixture.Services, sharedAs: "personal-api");
        var user = new UserAccount { Id = Guid.NewGuid(), Username = TestLogin.For("api-owner"), DisplayName = "API Owner" };
        using (var seed = host.OpenSession())
        using (var transaction = seed.BeginTransaction())
        {
            await seed.SaveAsync(user);
            await seed.SaveAsync(new UserApiPermission
            {
                Id = Guid.NewGuid(), UserId = user.Id, Level = "write", ChangedById = user.Id, ActiveFrom = DateTime.UtcNow
            });
            await transaction.CommitAsync();
        }
        var tokens = new PersonalApiCredentials(host.Database);
        host.Actor.Current = user;
        var created = await host.InvokeAsync<CreatedApiToken>(new IssueApiToken(new CreateApiToken("Platform pin", ["read"], 1)));

        using var scope = host.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Path = "/api/v1/spaces";
        context.Request.Headers.Authorization = "Bearer " + created.Secret;
        var handler = new PersonalApiAuthentication(
            scope.ServiceProvider.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            tokens);
        await handler.InitializeAsync(
            new AuthenticationScheme(PersonalApiAuthentication.SchemeName, null, typeof(PersonalApiAuthentication)),
            context);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(user.SecurityStamp, result.Principal!.FindFirstValue("stamp"));
        Assert.Equal(user.Id.ToString(), result.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        // The stamp claim is what makes /api/v1 usable by the same actor resolution as cookie routes.
        context.User = result.Principal;
        var actor = new HttpCurrentActor(
            new HttpContextAccessor { HttpContext = context },
            scope.ServiceProvider.GetRequiredService<ISession>());
        var resolved = await actor.RequireAsync(CancellationToken.None);
        Assert.Equal(user.Id, resolved.Id);
    }
}
