using JasperFx;

// Offline schema export uses the exact same FluentMigrator definitions as startup.
// It deliberately runs before configuration, hosted services and server-token generation.
if (args.Length > 0 && args[0] == "--database-script")
{
    if (args.Length != 2)
    {
        Console.Error.WriteLine("Usage: Atlas.Server --database-script <new-output.sql>");
        Environment.ExitCode = 2;
        return;
    }
    var destination = Path.GetFullPath(args[1]);
    var sql = AtlasMigrationScript.Generate();
    using var output = new StreamWriter(new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None));
    output.Write(sql);
    Console.WriteLine($"Atlas database creation script: {destination}");
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
if (args is ["codegen", ..])
{
    // Production refuses to start without the pre-generated code of every handler it discovers, so the code is generated with
    // every module on: a deployment may switch any of them on without a different image.
    builder.Configuration.AddInMemoryCollection(Atlas.Server.Platform.Modules.AtlasModules.All
        .Select(module => KeyValuePair.Create($"Modules:{module.Descriptor.Id}:Enabled", (string?)"true"))
        // Durable as production, which has a database; generating the code never connects to it.
        .Append(KeyValuePair.Create("Jobs:Durable", (string?)"true")));
}
await builder.AddAtlasServer();
var app = builder.Build();
app.UseAtlasPipeline();
app.MapAtlasEndpoints();
// `codegen write` pre-generates the Wolverine handler code that production loads instead of compiling it at
// runtime (Dockerfile; AtlasServices). Every other invocation starts the server with its arguments unchanged.
if (args is ["codegen", ..])
{
    Environment.ExitCode = await app.RunJasperFxCommands(args);
    return;
}
app.Run();

public partial class Program;
