using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// pgvector on PostgreSQL 19 (Debian). Migration 2026100520 requires the extension (deploy/postgres builds the Alpine
// production image). A volume per major: PostgreSQL 19 never opens the former atlas-postgres-18-data, which stays as it was.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "0.8.7-pg19-trixie")
    .WithImageSHA256("ace863afa68a13560603dc7366fad0adb4f5ddd0a21c8f06418195f32e03fa08")
    .WithDataVolume("atlas-postgres-19-data");
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
