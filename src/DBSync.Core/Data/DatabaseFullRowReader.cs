using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using DBSync.Core.Extensions;
using DBSync.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace DBSync.Core.Data;

/// <summary>
/// 按数据库类型读取表完整行数据的实现。
///</summary>
public sealed class DatabaseFullRowReader : IFullRowReader
{
    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyDictionary<string, string?>> ReadRowsAsync(
        DatabaseConnection connection,
        TableModel table,
        string? whereClause = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sql = BuildSelectSql(connection.DbType, table, whereClause);
        if (string.IsNullOrWhiteSpace(sql))
            yield break;

        var columns = table.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => c.Name)
            .ToList();

        await using var db = CreateConnection(connection);
        await db.OpenAsync(cancellationToken);

        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var columnName in columns)
            {
                var value = reader[columnName];
                row[columnName] = value == DBNull.Value ? null : FormatValue(value);
            }

            yield return row;
        }
    }

    /// <summary>
    /// 构建读取完整行数据的查询 SQL。
    /// </summary>
    /// <param name="dbType">数据库类型</param>
    /// <param name="table">表元数据</param>
    /// <param name="whereClause">可选 WHERE 子句</param>
    /// <returns>查询 SQL，无列时返回空字符串</returns>
    public static string BuildSelectSql(DatabaseType dbType, TableModel table, string? whereClause)
    {
        if (table.Columns.Count == 0)
            return string.Empty;

        var columns = string.Join(", ", table.Columns
            .OrderBy(c => c.OrdinalPosition)
            .Select(c => DbDialectSupport.QuoteIdentifier(dbType, c.Name)));
        var sql = $"SELECT {columns} FROM {DbDialectSupport.QuoteTableName(dbType, table.Schema, table.Name)}";
        var cleanedWhere = SqlServerDataFingerprinter.SanitizeWhereClause(whereClause);

        return string.IsNullOrWhiteSpace(cleanedWhere)
            ? sql
            : $"{sql}{Environment.NewLine}WHERE {cleanedWhere}";
    }

    /// <summary>
    /// 将数据库值格式化为可写入 SQL 文本的字符串。
    /// </summary>
    /// <param name="value">数据库原始值</param>
    /// <returns>字符串值</returns>
    private static string FormatValue(object value) => value switch
    {
        DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        TimeSpan timeSpan => timeSpan.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture),
        bool boolean => boolean ? "1" : "0",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    /// <summary>
    /// 按数据库类型创建连接。
    /// </summary>
    /// <param name="connection">连接配置</param>
    /// <returns>数据库连接</returns>
    private static DbConnection CreateConnection(DatabaseConnection connection)
    {
        return connection.DbType switch
        {
            DatabaseType.SqlServer => new SqlConnection(connection.ConnectionString.CheckTrustServerCertificate().CheckEncrypt()),
            DatabaseType.MySql => new MySqlConnection(connection.ConnectionString),
            DatabaseType.PostgreSql => new NpgsqlConnection(connection.ConnectionString),
            DatabaseType.Sqlite => new SqliteConnection(connection.ConnectionString),
            _ => throw new NotSupportedException($"不支持的数据库类型：{connection.DbType}")
        };
    }
}
