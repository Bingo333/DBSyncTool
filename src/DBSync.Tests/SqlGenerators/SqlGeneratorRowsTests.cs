using DBSync.Core.Models;
using DBSync.Core.SqlGenerators;
using DBSync.Tests.Helpers;

namespace DBSync.Tests.SqlGenerators;

/// <summary>
/// 数据行解析辅助类单元测试，覆盖变更行完整数据还原（UPDATE 的 SET 赋值来源）
/// </summary>
public class SqlGeneratorRowsTests
{
    private static TableModel TestTable => TableModelFactory.WithColumns(
        "Users",
        [
            TableModelFactory.IdColumn(),
            TableModelFactory.Col("Name", DbColumnType.Text)
        ]);

    private static DataDiff ChangedDiff(params string[] ids) => new()
    {
        RowsToInsert = [],
        DeletedRows = [],
        ChangedRows = ids.Select(id => new RowHash
        {
            PrimaryKeyValues = new Dictionary<string, string?> { ["Id"] = id },
            Hash = "changed"
        }).ToList()
    };

    [Fact]
    public void ResolveRowsForUpdate_WithFullData_ReturnsCompleteRow()
    {
        var fullData = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>
        {
            [TestTable.FullName] = [
                new Dictionary<string, string?> { ["Id"] = "1", ["Name"] = "张三" },
                new Dictionary<string, string?> { ["Id"] = "2", ["Name"] = "李四" }
            ]
        };

        var (rows, missingCount) = SqlGeneratorRows.ResolveRowsForUpdate(TestTable, ChangedDiff("2"), fullData);

        Assert.Single(rows);
        Assert.Equal(0, missingCount);
        Assert.Equal("李四", rows[0]["Name"]);
    }

    [Fact]
    public void ResolveRowsForUpdate_WithoutFullData_ReturnsAllMissing()
    {
        var (rows, missingCount) = SqlGeneratorRows.ResolveRowsForUpdate(TestTable, ChangedDiff("1", "2"), null);

        Assert.Empty(rows);
        Assert.Equal(2, missingCount);
    }

    [Fact]
    public void ResolveRowsForUpdate_FullDataMissesRow_CountsAsMissing()
    {
        var fullData = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>
        {
            [TestTable.FullName] = [
                new Dictionary<string, string?> { ["Id"] = "1", ["Name"] = "张三" }
            ]
        };

        var (rows, missingCount) = SqlGeneratorRows.ResolveRowsForUpdate(TestTable, ChangedDiff("1", "9"), fullData);

        Assert.Single(rows);
        Assert.Equal(1, missingCount);
    }

    [Fact]
    public void ResolveRowsForUpdate_NoChangedRows_ReturnsEmpty()
    {
        var (rows, missingCount) = SqlGeneratorRows.ResolveRowsForUpdate(TestTable, DataDiff.Empty, null);

        Assert.Empty(rows);
        Assert.Equal(0, missingCount);
    }
}
