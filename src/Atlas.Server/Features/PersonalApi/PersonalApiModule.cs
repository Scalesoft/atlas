using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Atlas.Server.Features.Access.Domain;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.PersonalApi;

public static class PersonalApiModule
{
    public static IServiceCollection AddPersonalApi(this IServiceCollection services)
    {
        services.AddScoped<PersonalApiActor>();
        services.AddScoped<PersonalApiCredentials>();
        services.AddSingleton<PersonalApiCredentialThrottle>();
        services.AddScoped<IAccessKind, TokenAccessKind>();
        services.AddJob<ApiLevelsReviewNotice>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PersonalApiAuthentication>(PersonalApiAuthentication.SchemeName, _ => { });
        services.AddAuthorization(options =>
        {
            // Every policy names the scope and the channel: a token is used on /api/v1 only with the channel rest, on /mcp only with mcp.
            foreach (var scope in ApiScopes.All)
            {
                options.AddPolicy(PersonalApiEndpoints.PolicyFor(scope), policy => policy.AddAuthenticationSchemes(PersonalApiAuthentication.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireClaim(PersonalApiAuthentication.ScopeClaim, scope)
                    .RequireClaim(PersonalApiAuthentication.ChannelClaim, ApiChannels.Rest));
                options.AddPolicy(PersonalApiEndpoints.McpPolicyFor(scope), policy => policy.AddAuthenticationSchemes(PersonalApiAuthentication.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireClaim(PersonalApiAuthentication.ScopeClaim, scope)
                    .RequireClaim(PersonalApiAuthentication.ChannelClaim, ApiChannels.Mcp));
            }
        });
        services.AddOpenApi("v1", options =>
        {
            // Microsoft.AspNetCore.OpenApi 11 writes OpenAPI 3.2 by default; API and MCP clients read the 3.1 document.
            options.OpenApiVersion = OpenApiSpecVersion.OpenApi3_1;
            options.ShouldInclude = description => description.GroupName == "v1";
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new()
                {
                    Title = "Atlas personal API", Version = "v1",
                    Description = "Personal bearer tokens on the channel rest. Every request uses the owner's current wiki access. Scopes: read reads; " +
                        "write also changes content as a person does (create, edit, publish, move, trash, restore); what readers see follows each " +
                        "page's publication setting. A WikiAdmin sets each account's ceiling, and a token acts only within its owner's current ceiling: a lowered ceiling " +
                        "applies to the next request, and a refused scope answers 403 with code scope_missing. Writes return 409 " +
                        "collaboration_unsaved while other users have unsaved live edits; reload and retry later. Browser/admin endpoints are outside " +
                        "this API. Lists use offset/limit/hasMore; writes use expectedVersion for conflict detection."
                };
                document.Components ??= new();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["PersonalToken"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "Personal token created in Profile → API with the channel REST. A WikiAdmin must first set the account's API level." }
                };
                foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations ?? []))
                {
                    operation.Value.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("PersonalToken", document)] = [] }];
                }
                return Task.CompletedTask;
            });
            // The scope note follows the policy the endpoint really requires, so the document cannot drift from authorization.
            options.AddOperationTransformer((operation, context, _) =>
            {
                var policies = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Select(data => data.Policy).ToArray();
                var required = ApiScopes.All.LastOrDefault(scope => policies.Contains(PersonalApiEndpoints.PolicyFor(scope)));
                if (required is ApiScopes.Write)
                {
                    var note = $"Requires {required} scope.";
                    operation.Description = string.IsNullOrEmpty(operation.Description) ? note : operation.Description + " " + note;
                }
                return Task.CompletedTask;
            });
        });
        return services;
    }
}
