using System.Text;
using DBSync.Core.Models;

namespace DBSync.Core.Execution;

/// <summary>
/// 按数据库方言把 SQL 脚本切分为单条语句。
/// </summary>
/// <remarks>
/// 不能简单地按分号切分。分号可能出现在字符串字面量、引用标识符、注释或
/// PostgreSQL 的美元引用块（$$ ... $$）内部。若直接按字符硬切，数据行里出现的
/// 分号会把一条 INSERT 拆成两条可独立执行的语句，等于把「数据内容」当成 SQL 来执行，
/// 属于典型的 SQL 注入（数据安全风险）。因此这里做最小化的词法分析，
/// 只在「正常状态」下遇到分号或 GO 批处理分隔符时才切分。
/// </remarks>
internal static class SqlStatementSplitter
{
    /// <summary>
    /// 词法状态
    /// </summary>
    private enum LexState
    {
        /// <summary>
        /// 普通 SQL 文本
        /// </summary>
        Normal,

        /// <summary>
        /// 单引号字符串字面量
        /// </summary>
        SingleQuote,

        /// <summary>
        /// 双引号引用标识符
        /// </summary>
        DoubleQuote,

        /// <summary>
        /// 反引号引用标识符（MySQL）
        /// </summary>
        Backtick,

        /// <summary>
        /// 方括号引用标识符（SQL Server）
        /// </summary>
        Bracket,

        /// <summary>
        /// 行注释（-- 或 #）
        /// </summary>
        LineComment,

        /// <summary>
        /// 块注释（/* */，可嵌套）
        /// </summary>
        BlockComment,

        /// <summary>
        /// 美元引用字符串（PostgreSQL）
        /// </summary>
        DollarQuote
    }

    /// <summary>
    /// 按指定方言切分脚本。
    /// </summary>
    /// <param name="script">SQL 脚本文本</param>
    /// <param name="options">方言相关的词法选项</param>
    /// <returns>去掉空语句与批处理分隔符后的语句列表</returns>
    public static List<string> Split(string script, SqlSplitOptions options)
    {
        var statements = new List<string>();
        if (string.IsNullOrEmpty(script))
            return statements;

        var current = new StringBuilder();
        var state = LexState.Normal;
        var blockCommentDepth = 0;
        string? dollarTag = null;

        var lines = script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            // GO 是客户端批处理分隔符，不是 SQL 语句，遇到时结束当前语句且不下发
            if (state == LexState.Normal && IsBatchSeparator(line))
            {
                AppendStatement(current, statements);
                current.Clear();
                continue;
            }

            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];

                switch (state)
                {
                    case LexState.SingleQuote:
                        current.Append(ch);
                        if (options.BackslashEscapeInString && ch == '\\' && i + 1 < line.Length)
                        {
                            // MySQL 默认把反斜杠视为转义符，需连带跳过下一个字符
                            current.Append(line[i + 1]);
                            i++;
                        }
                        else if (ch == '\'')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '\'')
                            {
                                current.Append('\'');
                                i++;
                            }
                            else
                            {
                                state = LexState.Normal;
                            }
                        }
                        break;

                    case LexState.DoubleQuote:
                        current.Append(ch);
                        if (ch == '"')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '"')
                            {
                                current.Append('"');
                                i++;
                            }
                            else
                            {
                                state = LexState.Normal;
                            }
                        }
                        break;

                    case LexState.Backtick:
                        current.Append(ch);
                        if (ch == '`')
                        {
                            if (i + 1 < line.Length && line[i + 1] == '`')
                            {
                                current.Append('`');
                                i++;
                            }
                            else
                            {
                                state = LexState.Normal;
                            }
                        }
                        break;

                    case LexState.Bracket:
                        current.Append(ch);
                        if (ch == ']')
                        {
                            if (i + 1 < line.Length && line[i + 1] == ']')
                            {
                                current.Append(']');
                                i++;
                            }
                            else
                            {
                                state = LexState.Normal;
                            }
                        }
                        break;

                    case LexState.LineComment:
                        current.Append(ch);
                        break;

                    case LexState.BlockComment:
                        current.Append(ch);
                        if (ch == '*' && i + 1 < line.Length && line[i + 1] == '/')
                        {
                            current.Append('/');
                            i++;
                            blockCommentDepth--;
                            if (blockCommentDepth <= 0)
                                state = LexState.Normal;
                        }
                        else if (ch == '/' && i + 1 < line.Length && line[i + 1] == '*')
                        {
                            current.Append('*');
                            i++;
                            blockCommentDepth++;
                        }
                        break;

                    case LexState.DollarQuote:
                        if (dollarTag is not null && IsTagAt(line, i, dollarTag))
                        {
                            current.Append(dollarTag);
                            i += dollarTag.Length - 1;
                            dollarTag = null;
                            state = LexState.Normal;
                        }
                        else
                        {
                            current.Append(ch);
                        }
                        break;

                    default:
                        if (ch == ';')
                        {
                            AppendStatement(current, statements);
                            current.Clear();
                        }
                        else if (ch == '\'')
                        {
                            state = LexState.SingleQuote;
                            current.Append(ch);
                        }
                        else if (ch == '"')
                        {
                            state = LexState.DoubleQuote;
                            current.Append(ch);
                        }
                        else if (ch == '`')
                        {
                            state = LexState.Backtick;
                            current.Append(ch);
                        }
                        else if (ch == '[' && options.BracketIdentifiers)
                        {
                            state = LexState.Bracket;
                            current.Append(ch);
                        }
                        else if (ch == '-' && i + 1 < line.Length && line[i + 1] == '-')
                        {
                            state = LexState.LineComment;
                            current.Append("--");
                            i++;
                        }
                        else if (ch == '#' && options.HashComment)
                        {
                            state = LexState.LineComment;
                            current.Append(ch);
                        }
                        else if (ch == '/' && i + 1 < line.Length && line[i + 1] == '*')
                        {
                            state = LexState.BlockComment;
                            blockCommentDepth = 1;
                            current.Append("/*");
                            i++;
                        }
                        else if (ch == '$' && options.DollarQuoting && TryReadDollarTag(line, i, out var tag))
                        {
                            dollarTag = tag;
                            state = LexState.DollarQuote;
                            current.Append(tag);
                            i += tag.Length - 1;
                        }
                        else
                        {
                            current.Append(ch);
                        }
                        break;
                }
            }

            // 行注释在行尾结束（字符串/块注释/美元引用可跨行，状态保留）
            if (state == LexState.LineComment)
                state = LexState.Normal;

            current.Append('\n');
        }

        AppendStatement(current, statements);
        return statements;
    }

    /// <summary>
    /// 取语句的首个关键字（跳过空白与注释），用于判断语句类型。
    /// </summary>
    /// <param name="statement">单条语句</param>
    /// <returns>大写关键字，无法识别时返回空字符串</returns>
    public static string ReadFirstKeyword(string statement)
    {
        var index = SkipTrivia(statement, 0, out var hasContent);
        if (!hasContent || index >= statement.Length)
            return string.Empty;

        var start = index;
        while (index < statement.Length && (char.IsLetter(statement[index]) || statement[index] == '_'))
            index++;

        return index == start ? string.Empty : statement[start..index].ToUpperInvariant();
    }

    /// <summary>
    /// 把当前累积的文本作为一条语句收尾。
    /// </summary>
    /// <param name="current">当前累积文本</param>
    /// <param name="statements">语句列表</param>
    private static void AppendStatement(StringBuilder current, List<string> statements)
    {
        var text = current.ToString().Trim();
        if (text.Length == 0)
            return;

        // 纯注释片段不作为语句下发
        SkipTrivia(text, 0, out var hasContent);
        if (!hasContent)
            return;

        statements.Add(text);
    }

    /// <summary>
    /// 跳过空白与注释，返回第一个有效字符下标。
    /// </summary>
    /// <param name="text">待扫描文本</param>
    /// <param name="start">起始下标</param>
    /// <param name="hasContent">是否存在非注释内容</param>
    /// <returns>第一个有效字符下标</returns>
    private static int SkipTrivia(string text, int start, out bool hasContent)
    {
        hasContent = false;
        var i = start;

        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0)
                    return text.Length;
                i = end + 1;
                continue;
            }

            if (text[i] == '#')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0)
                    return text.Length;
                i = end + 1;
                continue;
            }

            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return text.Length;
                i = end + 2;
                continue;
            }

            hasContent = true;
            return i;
        }

        return text.Length;
    }

    /// <summary>
    /// 判断当前行是否为 GO 批处理分隔符。
    /// </summary>
    /// <param name="line">行文本</param>
    /// <returns>是批处理分隔符时返回 true</returns>
    private static bool IsBatchSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Equals("GO", StringComparison.OrdinalIgnoreCase)
               || trimmed.Equals("GO;", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读取 PostgreSQL 美元引用标签（如 $$、$body$）。
    /// </summary>
    /// <param name="line">行文本</param>
    /// <param name="index">起始下标</param>
    /// <param name="tag">读到的标签</param>
    /// <returns>读到合法标签时返回 true</returns>
    private static bool TryReadDollarTag(string line, int index, out string tag)
    {
        tag = string.Empty;
        if (index >= line.Length || line[index] != '$')
            return false;

        var i = index + 1;
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
            i++;

        if (i >= line.Length || line[i] != '$')
            return false;

        // 标签不能以数字开头，避免把 $1 之类的占位符误判为标签
        if (i > index + 1 && char.IsDigit(line[index + 1]))
            return false;

        tag = line[index..(i + 1)];
        return true;
    }

    /// <summary>
    /// 判断指定位置是否出现给定标签。
    /// </summary>
    /// <param name="line">行文本</param>
    /// <param name="index">起始下标</param>
    /// <param name="tag">标签文本</param>
    /// <returns>匹配时返回 true</returns>
    private static bool IsTagAt(string line, int index, string tag)
    {
        return index + tag.Length <= line.Length &&
               string.CompareOrdinal(line, index, tag, 0, tag.Length) == 0;
    }
}

/// <summary>
/// SQL 词法切分所需的方言选项。
/// </summary>
/// <param name="BackslashEscapeInString">字符串内是否使用反斜杠转义（MySQL）</param>
/// <param name="BracketIdentifiers">是否支持方括号引用标识符（SQL Server）</param>
/// <param name="HashComment">是否支持 # 行注释（MySQL）</param>
/// <param name="DollarQuoting">是否支持美元引用字符串（PostgreSQL）</param>
internal sealed record SqlSplitOptions(
    bool BackslashEscapeInString,
    bool BracketIdentifiers,
    bool HashComment,
    bool DollarQuoting)
{
    /// <summary>
    /// 根据数据库类型返回对应方言选项。
    /// </summary>
    /// <param name="dbType">数据库类型</param>
    /// <returns>方言选项</returns>
    public static SqlSplitOptions For(DatabaseType dbType)
    {
        return dbType switch
        {
            DatabaseType.MySql => new SqlSplitOptions(
                BackslashEscapeInString: true,
                BracketIdentifiers: false,
                HashComment: true,
                DollarQuoting: false),
            DatabaseType.PostgreSql => new SqlSplitOptions(
                BackslashEscapeInString: false,
                BracketIdentifiers: false,
                HashComment: false,
                DollarQuoting: true),
            DatabaseType.Sqlite => new SqlSplitOptions(
                BackslashEscapeInString: false,
                BracketIdentifiers: true,
                HashComment: false,
                DollarQuoting: false),
            _ => new SqlSplitOptions(
                BackslashEscapeInString: false,
                BracketIdentifiers: true,
                HashComment: false,
                DollarQuoting: false)
        };
    }
}
