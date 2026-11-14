using Atlas.Server.Features.ContentExport.Domain;
using Xunit;
namespace Atlas.Server.Tests;
public sealed class ContentExportBudgetTests
{
    [Fact]
    public void Exact_byte_limit_is_allowed_but_next_byte_and_overflow_are_rejected()
    {
        var budget=new ExportBudget();
        budget.AddBytes(ExportBudget.MaximumBytes);
        Assert.Equal(413,Assert.Throws<ApiException>(()=>budget.AddBytes(1)).Status);
        Assert.Equal(413,Assert.Throws<ApiException>(()=>new ExportBudget().AddBytes(long.MaxValue)).Status);
        Assert.Throws<ArgumentOutOfRangeException>(()=>new ExportBudget().AddBytes(-1));
    }
    [Fact]
    public void File_limit_counts_manifest_too()
    {
        var budget=new ExportBudget();
        for(var i=0;i<ExportBudget.MaximumFiles;i++)budget.AddFile();
        Assert.Equal(413,Assert.Throws<ApiException>(budget.AddFile).Status);
    }
}
