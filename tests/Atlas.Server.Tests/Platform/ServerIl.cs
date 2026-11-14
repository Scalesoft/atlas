using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Atlas.Server.Platform;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// The IL of the server assembly, read once per test process: every method a body calls, constructs with or takes the address
/// of, and every string literal it loads, each with the type and the source method the body belongs to. For conventions the
/// compiler does not enforce (PageLockingLoadConventionTests, PageGateCapabilityConventionTests, RowLockTextConventionTests).
/// </summary>
internal static partial class ServerIl
{
    private static readonly Dictionary<short, OpCode> OpCodeByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    private static readonly Lazy<(IReadOnlyList<Call> Calls, IReadOnlyList<Literal> Literals)> All = new(Read);

    public static IReadOnlyList<Call> Calls => All.Value.Calls;

    public static IReadOnlyList<Literal> Literals => All.Value.Literals;

    private static (IReadOnlyList<Call> Calls, IReadOnlyList<Literal> Literals) Read()
    {
        const BindingFlags declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var calls = new List<Call>();
        var literals = new List<Literal>();
        foreach (var type in typeof(PlatformServiceCollectionExtensions).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(declared).Cast<MethodBase>().Concat(type.GetConstructors(declared)))
            {
                var (owner, source) = Owner(method);
                foreach (var (callee, text) in Operands(method))
                {
                    if (callee is not null)
                    {
                        calls.Add(new Call(owner, source, callee));
                    }
                    if (text is not null)
                    {
                        literals.Add(new Literal(owner, source, text));
                    }
                }
            }
        }
        return (calls, literals);
    }

    /// <summary>The type and the source method a body belongs to: a state machine or a closure is named after the method it was made from.</summary>
    private static (Type Type, string Method) Owner(MethodBase method)
    {
        var type = method.DeclaringType!;
        var name = Source().Match(method.Name) is { Success: true } own ? own.Groups[1].Value : null;
        while (type.IsNested && type.Name.StartsWith('<'))
        {
            name ??= Source().Match(type.Name) is { Success: true } made ? made.Groups[1].Value : null;
            type = type.DeclaringType!;
        }
        return (type, name ?? method.Name);
    }

    /// <summary>Every method a body calls, constructs with or takes the address of, resolved in the generic context of the body,
    /// and every string literal it loads.</summary>
    private static IEnumerable<(MethodBase? Callee, string? Text)> Operands(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }
        var typeArguments = method.DeclaringType is { IsGenericType: true } generic ? generic.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            var code = il[position] == 0xFE ? OpCodeByValue[unchecked((short)(0xFE00 | il[position + 1]))] : OpCodeByValue[il[position]];
            position += code.Size;
            if (code.OperandType == OperandType.InlineMethod)
            {
                yield return (method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments)!, null);
            }
            if (code.OperandType == OperandType.InlineString)
            {
                yield return (null, method.Module.ResolveString(BitConverter.ToInt32(il, position)));
            }
            position += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
        }
    }

    // <Handle>d__5 (a state machine), <Handle>b__0 (a lambda), <Handle>g__Local|5_0 (a local function).
    [GeneratedRegex(@"<(\w+)>")]
    private static partial Regex Source();

    /// <summary>One call: the type and the source method of the calling body, and the method or constructor it calls.</summary>
    public sealed record Call(Type Type, string Method, MethodBase Callee)
    {
        public string Site => Type.FullName + "." + Method;
    }

    /// <summary>One string literal: the type and the source method of the body that loads it, and its text.</summary>
    public sealed record Literal(Type Type, string Method, string Text)
    {
        public string Site => Type.FullName + "." + Method;
    }
}
