using System.Reflection;
using Atlas.Server.Features.PageTemplates;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Tests.Platform;
using Xunit;
// The folder's namespace hides the type of the same name.
using Gate = Atlas.Server.Platform.Identity.PageGate;

namespace Atlas.Server.Tests.PageGate;

[Trait("Category", "Guard")]
public sealed class PageGateCapabilityConventionTests
{
    /// <summary>
    /// A LockedSpace, a LockedPage, an EditablePage and a PageOpening say that PageGate took the locks and decided the page: what
    /// takes one as a parameter trusts it. Their constructors are internal, which keeps other assemblies out, but the server is
    /// one assembly, so any feature could write new EditablePage(page, actor) and pass it on. This test reads the IL of the
    /// server for every constructor call of the four types and allows it only in PageGate and in the four themselves.
    /// It also requires PageGate to construct each of the four, so a scan that sees nothing cannot pass.
    /// What it cannot see: an instance made through reflection.
    /// The Smoke tag is deliberate: a forged capability would be written in a feature, and tests/changed.ps1 runs only that
    /// feature's tests for such a change. The tag makes the guard run in tests/smoke.ps1, before a commit or handoff, and not
    /// only in the full suite.
    /// </summary>
    [Trait("Category", "Smoke")]
    [Fact]
    public void Only_PageGate_makes_a_locked_space_a_page_capability_or_an_opening()
    {
        Type[] capabilities = [typeof(LockedSpace), typeof(LockedPage), typeof(EditablePage), typeof(PageOpening<>)];
        var made = ServerIl.Calls
            .Where(call => call.Callee is ConstructorInfo && capabilities.Contains(Definition(call.Callee.DeclaringType!)))
            .Select(call => (Maker: Definition(call.Type), Capability: Definition(call.Callee.DeclaringType!), call.Site))
            .ToArray();

        var forged = made.Where(x => x.Maker != typeof(Gate) && !capabilities.Contains(x.Maker))
            .Select(x => $"{x.Site} constructs {x.Capability.Name}: only PageGate hands out the proof that a page or a space is locked and decided.")
            .Distinct()
            .ToArray();
        Assert.True(forged.Length == 0, string.Join("\n", forged));
        Assert.All(capabilities, capability => Assert.Contains(made, x => x.Maker == typeof(Gate) && x.Capability == capability));
    }

    /// <summary>
    /// PageReader.ReadEditedWithoutProofAsync reads a page as its editor reads it on the caller's word alone: no EditablePage
    /// stands behind it and nothing decides the page again. It is public, because its callers are handlers of other features
    /// in the one server assembly, so until now only its comment kept a new handler from answering with it. This test reads
    /// the IL of the server for every call of it, and for every delegate made from it, and allows them only in the two
    /// handlers that still open their page themselves. A new command opens its page through PageGate and reads it back with
    /// ReadEditedAsync. The list is exact: a handler on it that no longer calls the method fails the test too, so the list
    /// shrinks when a handler moves to the gate, and the method goes with the last of them.
    /// What it cannot see: a call through reflection.
    /// The Smoke tag is deliberate, for the reason given above: the call would be written in a feature.
    /// </summary>
    [Trait("Category", "Smoke")]
    [Fact]
    public void Only_the_handlers_that_open_their_page_themselves_read_it_back_without_proof()
    {
        Type[] allowed = [typeof(PublishPageHandler), typeof(SetPageTemplateHandler)];
        var callers = ServerIl.Calls
            .Where(call => call.Callee.DeclaringType == typeof(PageReader) && call.Callee.Name == nameof(PageReader.ReadEditedWithoutProofAsync))
            .Select(call => (Caller: Definition(call.Type), call.Site))
            .ToArray();

        var unproven = callers.Where(x => !allowed.Contains(x.Caller))
            .Select(x => $"{x.Site} reads a page back without proof: open the page through PageGate and read it back with PageReader.ReadEditedAsync.")
            .Distinct()
            .ToArray();
        Assert.True(unproven.Length == 0, string.Join("\n", unproven));
        var gone = allowed.Where(handler => callers.All(x => x.Caller != handler))
            .Select(handler => $"{handler.FullName} is allowed to read a page back without proof, but no longer does: take it off the list.")
            .ToArray();
        Assert.True(gone.Length == 0, string.Join("\n", gone));
    }

    /// <summary>
    /// RequireHeld and RequireHeldForChange ask the gate that handed the capability out, about that gate's session. That is
    /// the wrong question for what takes a capability as its proof: it works on the session of its own scope, and a
    /// capability of another scope is held over there while that scope's transaction runs. PageReader read a page back that
    /// way, as an editor for an account nothing had decided. The right question goes to the gate of the own scope,
    /// PageGate.RequireOwn or RequireOwnForChange, which refuses a capability of another gate first. Nothing makes a
    /// consumer ask there: the checks of the capabilities are public. So this test reads the IL of the server for every
    /// call of them and allows it only in the capabilities, which pass the question on to their space, and in the RequireOwn
    /// methods of PageGate. It also requires that PageGate asks them there, so a scan that sees nothing cannot pass.
    /// What it cannot see: a call through reflection.
    /// The Smoke tag is deliberate, for the reason given above: the call would be written in a feature.
    /// </summary>
    [Trait("Category", "Smoke")]
    [Fact]
    public void Only_the_own_gate_asks_a_capability_whether_it_is_held()
    {
        Type[] capabilities = [typeof(LockedSpace), typeof(LockedPage), typeof(EditablePage)];
        string[] checks = [nameof(LockedSpace.RequireHeld), nameof(LockedSpace.RequireHeldForChange)];
        var asked = ServerIl.Calls
            .Where(call => capabilities.Contains(call.Callee.DeclaringType) && checks.Contains(call.Callee.Name))
            .ToArray();

        // RequireOwn is internal, so its name is spelled out: both it and RequireOwnForChange begin with it.
        var elsewhere = asked.Where(call => !capabilities.Contains(call.Type) && !(call.Type == typeof(Gate) && call.Method.StartsWith("RequireOwn", StringComparison.Ordinal)))
            .Select(call => $"{call.Site} calls {call.Callee.DeclaringType!.Name}.{call.Callee.Name}, which asks the gate that opened the page or entered the space: ask the gate of the own scope, PageGate.RequireOwn or RequireOwnForChange.")
            .Distinct()
            .ToArray();
        Assert.True(elsewhere.Length == 0, string.Join("\n", elsewhere));
        Assert.Contains(asked, call => call.Type == typeof(Gate));
    }

    private static Type Definition(Type type)
    {
        return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
    }
}
