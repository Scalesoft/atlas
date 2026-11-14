using System.Globalization;
using System.Text;

namespace Atlas.Server.Features.KnowledgeMaintenance;

public static class AiReportCsv
{
    public static string Write(AiUsageReport report)
    {
        var csv = new StringBuilder("section,day,provider,location,model,task,kind,operation,calls,errors,canceled,input_tokens,output_tokens,input_known_calls,output_known_calls,cost_known_calls,estimated_cost,cost_basis,p50_ms,p95_ms,last_call_at\r\n");
        foreach (var row in report.Providers)
        {
            Append(csv, "provider", null, row.Provider, row.Location, row.Model, null, null, null,
                row.Calls, row.Errors, row.Canceled, row.InputTokens, row.OutputTokens,
                row.InputKnownCalls, row.OutputKnownCalls, row.CostKnownCalls, row.EstimatedCost,
                "fully known calls", null, null, row.LastCallAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }
        foreach (var row in report.Days)
        {
            Append(csv, "day", row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null, null, null, null, null, null,
                row.Calls, row.Errors, row.Canceled, row.InputTokens, row.OutputTokens,
                row.InputKnownCalls, row.OutputKnownCalls, row.CostKnownCalls, row.EstimatedCost,
                "fully known calls", null, null, null);
        }
        foreach (var row in report.Items)
        {
            Append(csv, "task", null, null, null, null, row.Task, row.Kind, row.Operation,
                row.Calls, row.Errors, null, row.InputTokens, row.OutputTokens, row.InputKnownCalls,
                row.OutputKnownCalls, row.CostKnownCalls, row.EstimatedCost, "fully known calls", row.P50Ms, row.P95Ms, null);
        }
        return csv.ToString();
    }

    private static void Append(StringBuilder csv, params object?[] values)
    {
        csv.AppendJoin(',', values.Select(Cell));
        csv.Append("\r\n");
    }

    private static string Cell(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (text.Length > 0 && "=+-@\t\r\n".Contains(text[0]))
        {
            text = "'" + text;
        }
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
