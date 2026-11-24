using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// The database of docker-compose.yml: pgvector on PostgreSQL 19 (Debian), the same digest, text sorted by the builtin provider.
// Migration 2026100520 requires pgvector. A new volume: the former atlas-postgres-18-data and atlas-postgres-19-data (a glibc
// collation) stay as they were.
var postgres = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "0.8.7-pg19-trixie")
    .WithImageSHA256("ace863afa68a13560603dc7366fad0adb4f5ddd0a21c8f06418195f32e03fa08")
    .WithEnvironment("POSTGRES_INITDB_ARGS", "--locale-provider=builtin --builtin-locale=C.UTF-8")
    .WithDataVolume("atlas-postgres-19-builtin-data");
var database = postgres.AddDatabase("atlas-db", "atlas");

var bootstrap = builder.AddParameter("bootstrap-token", new GenerateParameterDefault { MinLength = 48, Special = false }, secret: true, persist: true)
    .WithDescription("Installation token for the first Atlas administrator. Copy privately into the onboarding wizard.");

var atlasService = builder.AddDotnetProject("atlas-service", "../Atlas.Server/Atlas.Server.csproj")
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
