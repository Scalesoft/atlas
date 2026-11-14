using Atlas.Server.Platform.Events.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class ServiceLocationLogTests
{
    /// <summary>Atlas resolves its factory services (ISession, AccessChanges, Outbox, …) by service location on purpose; generating a
    /// handler that needs them must not write a warning per service into the system log (~2600 over all handlers before).</summary>
    [PostgreSqlFact]
    public async Task Generating_a_handler_with_factory_services_logs_no_service_location_warning()
    {
        var capture = new CaptureLoggerProvider();
        await using var host = await AtlasTestHost.CreateAsync("service_location_", services => services.AddLogging(logging => logging.AddProvider(capture)));

        // GetOutboxHealthHandler takes ISession, which is an opaque scoped factory.
        ((WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>()).Handlers.HandlerFor(typeof(GetOutboxHealth));

        Assert.DoesNotContain(capture.Entries, entry => entry.Message.Contains("service location", StringComparison.OrdinalIgnoreCase));
    }
}
