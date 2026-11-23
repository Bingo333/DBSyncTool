using DBSync.Core.Models;

namespace DBSync.Core.Data;

/// <summary>
/// 完整行数据读取器：按表结构读取所有列的原始值，
/// 用于生成带字段赋值的 INSERT / UPDATE 语句。
///</summary>
public interface IFullRowReader
{
    /// <summary>
    /// 流式读取表的完整行数据。
    /// </summary>
    /// <param name="connection">数据库连接配置</param>
    /// <param name="table">表元数据</param>
    /// <param name="whereClause">可选 WHERE 子句，不含 WHERE 关键字</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>列名到字符串值的行数据序列（null 表示该列为 NULL）</returns>
    IAsyncEnumerable<IReadOnlyDictionary<string, string?>> ReadRowsAsync(
        DatabaseConnection connection,
        TableModel table,
        string? whereClause = null,
        CancellationToken cancellationToken = default);
}
