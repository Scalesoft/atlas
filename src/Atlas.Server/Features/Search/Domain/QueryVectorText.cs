using System.Globalization;
using System.Text;

namespace Atlas.Server.Features.Search.Domain;

/// <summary>
/// The text of a query vector parameter (SQL: <c>CAST(:embedding AS vector)</c>): the unit vector scaled by 10 000 and rounded to
/// integers. Cosine distance ignores the scale and the rounding moves the cosine by less than 1e-6, while the text shrinks from
/// ~12.6 kB to ~5 kB. That keeps a search in one write of Npgsql's 8 kB buffer: a split write waits for a delayed ACK (~40 ms)
/// behind a forwarding proxy such as Podman's or WSL's port forwarding (measured in tests/Atlas.Search.Benchmarks).
/// </summary>
public static class QueryVectorText
{
    public const float Scale = 10000f;

    public static string Format(ReadOnlySpan<float> vector)
    {
        var text = new StringBuilder(vector.Length * 5 + 2).Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }
            text.Append(((int)MathF.Round(vector[i] * Scale)).ToString(CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }
}
