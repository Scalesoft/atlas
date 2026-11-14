using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// pgvector on the same PostgreSQL 18.6 Debian base as the former postgres:18.6, so the volume stays compatible.
// Migration 2026100520 requires the extension (deploy/postgres builds the Alpine production image).
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "0.8.7-pg18-trixie")
    .WithImageSHA256("9d9c930220cb9bf2f956d10a8f909cf9973d9672ca0278aef2c1e6facccad2e0")
    .WithDataVolume("atlas-postgres-18-data");
var database = postgres.AddDatabase("atlas-db", "atlas");

var bootstrap = builder.AddParameter("bootstrap-token", new GenerateParameterDefault { MinLength = 48, Special = false }, secret: true, persist: true)
    .WithDescription("Installation token for the first Atlas administrator. Copy privately into the onboarding wizard.");

var atlasService = builder.AddProject<Projects.Atlas_Server>("atlas-service")
    .WithReference(database)
    .WithEnvironment("Setup__BootstrapToken", bootstrap)
    .WithEnvironment("Authentication__Builtin__AllowDevelopmentPasswords", "true")
    .WaitFor(database);

var frontend = builder.AddViteApp("frontend", "../Atlas.Web")
    .WithPnpm()
    .WithReference(atlasService)
    .WaitFor(atlasService)
    .WithExternalHttpEndpoints()
    .WithEnvironment("VITE_BACKEND_URL", atlasService.GetEndpoint("http"));

atlasService.PublishWithContainerFiles(frontend, "wwwroot");

builder.Build().Run();
