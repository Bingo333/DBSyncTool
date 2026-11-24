using DBSync.Core.Models;

namespace DBSync.Core.Snapshot;

/// <summary>
/// 快照内容校验器。
/// </summary>
/// <remarks>
/// .dbsync 文件来自外部（同事传递、共享目录、下载），属于不可信输入。
/// 其中的表名、列名、类型名、默认值会被原样拼进升级脚本，若携带语句分隔符，
/// 就可能生成一条「数据之外」的额外 SQL 语句，进而攻击目标库。
/// 因此加载阶段就把这类内容拦下来，而不是等到执行脚本时才依赖切分器兜底。
/// </remarks>
internal static class SnapshotContentValidator
{
    /// <summary>
    /// 标识符与类型名中禁止出现的字符。
    /// </summary>
    private static readonly char[] ForbiddenChars = [';', '\r', '\n', '\0'];

    /// <summary>
    /// 校验并净化表结构元数据。
    /// </summary>
    /// <param name="table">从快照中反序列化出的表结构</param>
    /// <returns>净化后的表结构</returns>
    /// <exception cref="InvalidOperationException">标识符或类型名包含非法字符时抛出</exception>
    public static TableModel Validate(TableModel table)
    {
        RequireSafeText(table.Name, "表名");
        RequireSafeText(table.Schema, "Schema 名");

        foreach (var column in table.Columns)
        {
            RequireSafeText(column.Name, "列名");
            RequireSafeText(column.DbTypeName, "列类型");
        }

        foreach (var columnName in table.PrimaryKeyColumns)
            RequireSafeText(columnName, "主键列名");

        foreach (var index in table.Indexes)
        {
            RequireSafeText(index.Name, "索引名");
            foreach (var columnName in index.ColumnNames)
                RequireSafeText(columnName, "索引列名");
        }

        foreach (var foreignKey in table.ForeignKeys)
        {
            RequireSafeText(foreignKey.Name, "外键名");
            RequireSafeText(foreignKey.ColumnName, "外键列名");
            RequireSafeText(foreignKey.ReferencedTable, "外键引用表名");
            RequireSafeText(foreignKey.ReferencedColumn, "外键引用列名");
        }

        return table with
        {
            Columns = table.Columns.Select(SanitizeColumn).ToList()
        };
    }

    /// <summary>
    /// 净化单列：默认值表达式若在字符串字面量之外出现语句分隔符，则丢弃该默认值。
    /// </summary>
    /// <param name="column">列定义</param>
    /// <returns>净化后的列定义</returns>
    private static ColumnModel SanitizeColumn(ColumnModel column)
    {
        if (string.IsNullOrEmpty(column.DefaultValue) || !ContainsUnsafeSeparator(column.DefaultValue))
            return column;

        return column with { DefaultValue = null };
    }

    /// <summary>
    /// 校验文本不含语句分隔符等危险字符。
    /// </summary>
    /// <param name="value">待校验文本</param>
    /// <param name="fieldName">字段中文名（用于错误提示）</param>
    /// <exception cref="InvalidOperationException">包含危险字符时抛出</exception>
    private static void RequireSafeText(string? value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
            return;

        if (value.IndexOfAny(ForbiddenChars) >= 0)
            throw new InvalidOperationException($"快照中的{fieldName}包含非法字符，已拒绝加载该文件。");
    }

    /// <summary>
    /// 判断文本是否在字符串字面量之外出现语句分隔符或换行。
    /// </summary>
    /// <param name="value">待检查文本</param>
    /// <returns>存在危险字符时返回 true</returns>
    private static bool ContainsUnsafeSeparator(string value)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;

        foreach (var ch in value)
        {
            if (inSingleQuote)
            {
                if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '"')
                    inDoubleQuote = false;
                continue;
            }

            switch (ch)
            {
                case '\'':
                    inSingleQuote = true;
                    break;
                case '"':
                    inDoubleQuote = true;
                    break;
                case ';':
                case '\r':
                case '\n':
                case '\0':
                    return true;
            }
        }

        return false;
    }
}
