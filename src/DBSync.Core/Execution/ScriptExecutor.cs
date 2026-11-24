using DBSync.Core.Models;
using SqlSugar;

namespace DBSync.Core.Execution;

/// <summary>
/// SQL 脚本执行器实现，基于 SqlSugar 执行分段 SQL
/// </summary>
public sealed class ScriptExecutor : IScriptExecutor
{
    /// <summary>
    /// Dry Run：解析脚本，统计 DDL/DML 数量
    /// </summary>
    /// <param name="script">SQL 脚本文本</param>
    /// <param name="dbType">目标数据库类型，用于确定字符串/注释的切分规则</param>
    /// <returns>操作摘要</returns>
    public ScriptExecutionPlan DryRun(string script, DatabaseType dbType = DatabaseType.SqlServer)
    {
        var statements = SqlStatementSplitter.Split(script, SqlSplitOptions.For(dbType));
        var ddl = new List<string>();
        var dmlCount = 0;
        var hasTransaction = false;

        foreach (var stmt in statements)
        {
            var keyword = SqlStatementSplitter.ReadFirstKeyword(stmt);
            if (keyword is "CREATE" or "ALTER" or "DROP")
            {
                ddl.Add(stmt.Length > 120 ? stmt[..120] + "..." : stmt);
            }
            else if (keyword is "INSERT" or "UPDATE" or "DELETE")
            {
                dmlCount++;
            }
            else if (keyword is "BEGIN" or "START")
            {
                hasTransaction = true;
            }
        }

        return new ScriptExecutionPlan
        {
            DdlCount = ddl.Count,
            DmlCount = dmlCount,
            TotalStatements = statements.Count,
            HasTransaction = hasTransaction,
            DdlStatements = ddl,
        };
    }

    /// <summary>
    /// 执行脚本到目标数据库
    /// </summary>
    /// <param name="connection">目标数据库连接</param>
    /// <param name="script">SQL 脚本文本</param>
    /// <param name="progress">进度回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>执行结果</returns>
    public async Task<ScriptExecutionResult> ExecuteAsync(
        DatabaseConnection connection,
        string script,
        IProgress<ScriptExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var statements = SqlStatementSplitter.Split(script, SqlSplitOptions.For(connection.DbType));
        var startTime = DateTime.UtcNow;
        long totalAffected = 0;
        var executed = 0;

        var dbType = connection.DbType switch
        {
            DatabaseType.SqlServer => DbType.SqlServer,
            DatabaseType.MySql => DbType.MySql,
            DatabaseType.PostgreSql => DbType.PostgreSQL,
            DatabaseType.Sqlite => DbType.Sqlite,
            _ => DbType.MySql
        };

        using var db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connection.ConnectionString,
            DbType = dbType,
            IsAutoCloseConnection = false,
        });

        try
        {
            await Task.Run(async () =>
            {
                db.Open();

                foreach (var stmt in statements)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var trimmed = stmt.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    progress?.Report(new ScriptExecutionProgress
                    {
                        Current = executed + 1,
                        Total = statements.Count,
                        CurrentStatement = trimmed.Length > 80 ? trimmed[..80] + "..." : trimmed
                    });

                    var affected = await db.Ado.ExecuteCommandAsync(trimmed);
                    totalAffected += Math.Max(0, affected);
                    executed++;
                }
            }, cancellationToken);

            return new ScriptExecutionResult
            {
                Success = true,
                ExecutedStatements = executed,
                AffectedRows = totalAffected,
                Duration = DateTime.UtcNow - startTime,
            };
        }
        catch (OperationCanceledException)
        {
            return new ScriptExecutionResult
            {
                Success = false,
                ExecutedStatements = executed,
                AffectedRows = totalAffected,
                Duration = DateTime.UtcNow - startTime,
                Error = "用户取消执行",
            };
        }
        catch (Exception ex)
        {
            return new ScriptExecutionResult
            {
                Success = false,
                ExecutedStatements = executed,
                AffectedRows = totalAffected,
                Duration = DateTime.UtcNow - startTime,
                Error = ex.Message,
                FailedStatement = statements.Count > executed ? statements[executed] : null,
                RolledBack = script.Contains("BEGIN", StringComparison.OrdinalIgnoreCase),
            };
        }
    }
}
