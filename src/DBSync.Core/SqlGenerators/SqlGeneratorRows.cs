using DBSync.Core.Models;

namespace DBSync.Core.SqlGenerators;

public static class SqlGeneratorRows
{
    /// <summary>
    /// 解析新增行对应的完整行数据（用于生成 INSERT 语句的列值）。
    /// </summary>
    /// <param name="table">表结构定义</param>
    /// <param name="diff">数据差异</param>
    /// <param name="fullData">完整行数据（表名 → 行数据列表），可为空</param>
    /// <param name="tableKey">完整行数据中的表键，默认取 <see cref="TableModel.FullName"/></param>
    /// <returns>可生成 INSERT 的行数据</returns>
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> ResolveRowsToInsert(
        TableModel table,
        DataDiff diff,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>? fullData,
        string? tableKey = null)
    {
        if (fullData is null || !fullData.TryGetValue(tableKey ?? table.FullName, out var fullRows))
            return diff.RowsToInsert.Select(r => r.PrimaryKeyValues).ToList();

        var keys = diff.RowsToInsert
            .Select(r => r.PrimaryKeyString)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var matched = fullRows
            .Where(row => keys.Contains(BuildPrimaryKeyString(table, row)))
            .ToList();

        // 当完整数据匹配失败时，回退到主键数据
        return matched.Count > 0 ? matched : diff.RowsToInsert.Select(r => r.PrimaryKeyValues).ToList();
    }

    /// <summary>
    /// 解析变更行对应的完整行数据（用于生成 UPDATE 语句的 SET 赋值）。
    /// 差异结果里只保存了主键值和哈希，必须靠完整行数据还原各列取值，
    /// 否则 UPDATE 语句会缺少 SET 子句。
    /// </summary>
    /// <param name="table">表结构定义</param>
    /// <param name="diff">数据差异</param>
    /// <param name="fullData">完整行数据（表名 → 行数据列表），可为空</param>
    /// <param name="tableKey">完整行数据中的表键，默认取 <see cref="TableModel.FullName"/></param>
    /// <returns>可生成 UPDATE 的行数据，以及无法还原完整数据的行数</returns>
    public static (IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, int MissingCount) ResolveRowsForUpdate(
        TableModel table,
        DataDiff diff,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string?>>>? fullData,
        string? tableKey = null)
    {
        if (diff.ChangedRows.Count == 0)
            return ([], 0);

        if (fullData is null || !fullData.TryGetValue(tableKey ?? table.FullName, out var fullRows))
            return ([], diff.ChangedRows.Count);

        var rowMap = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in fullRows)
            rowMap[BuildPrimaryKeyString(table, row)] = row;

        var rows = new List<IReadOnlyDictionary<string, string?>>(diff.ChangedRows.Count);
        var missingCount = 0;
        foreach (var changedRow in diff.ChangedRows)
        {
            if (rowMap.TryGetValue(changedRow.PrimaryKeyString, out var row))
                rows.Add(row);
            else
                missingCount++;
        }

        return (rows, missingCount);
    }

    /// <summary>
    /// 解析所有需要生成数据 DML 的表：结构变更的表 + 纯数据差异的表
    /// </summary>
    public static IEnumerable<TableModel> ResolveDataTables(
        SchemaDiff schemaDiff,
        IReadOnlyDictionary<string, DataDiff> dataDiffs,
        IReadOnlyDictionary<string, TableModel>? allTables)
    {
        var schemaTables = schemaDiff.AddedTables
            .Concat(schemaDiff.ModifiedTables.Select(t => t.SourceTable))
            .ToList();

        var schemaTableNames = new HashSet<string>(
            schemaTables.Select(t => t.FullName), StringComparer.OrdinalIgnoreCase);

        // 纯数据差异的表（不在结构变更中）
        if (allTables is not null)
        {
            foreach (var tableName in dataDiffs.Keys)
            {
                if (!schemaTableNames.Contains(tableName) &&
                    allTables.TryGetValue(tableName, out var table))
                {
                    schemaTables.Add(table);
                }
            }
        }

        return schemaTables;
    }

    private static string BuildPrimaryKeyString(TableModel table, IReadOnlyDictionary<string, string?> row)
    {
        return string.Join("|", table.PrimaryKeyColumns
            .OrderBy(name => name)
            .Select(name => $"{name}={(row.TryGetValue(name, out var value) ? value : null) ?? "NULL"}"));
    }
}
