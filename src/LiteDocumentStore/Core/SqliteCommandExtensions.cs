using System.Globalization;
using Microsoft.Data.Sqlite;

namespace LiteDocumentStore;

/// <summary>
/// Internal, reflection-free ADO.NET helpers over <see cref="SqliteConnection"/>.
/// These replace the previous Dapper dependency: parameters are bound explicitly and
/// results are read by ordinal, so nothing here relies on runtime reflection or IL
/// generation (AOT/trim safe).
/// </summary>
/// <remarks>
/// <para>
/// Commands are created with <see cref="SqliteConnection.CreateCommand"/>, which assigns
/// the connection's currently active transaction automatically, so callers do not need to
/// pass a transaction explicitly to participate in one.
/// </para>
/// <para>
/// The cancellation token sits <em>before</em> the trailing <c>params</c> array rather than
/// last: C# allows only one params parameter and it must come last, and the alternative —
/// dropping <c>params</c> so the token can trail — would force every call site to spell out
/// an array, including the many that bind no parameters at all.
/// </para>
/// </remarks>
internal static class SqliteCommandExtensions
{
    /// <summary>
    /// Executes a non-query statement and returns the number of affected rows.
    /// </summary>
    public static async Task<int> ExecuteAsync(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a non-query statement synchronously and returns the number of affected rows.
    /// </summary>
    public static int Execute(
        this SqliteConnection connection,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, commandText, parameters);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Executes a query and returns the first column of the first row converted to
    /// <typeparamref name="T"/>, or default when there is no row or the value is NULL.
    /// </summary>
    public static async Task<T?> ExecuteScalarAsync<T>(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return ConvertScalar<T>(result);
    }

    /// <summary>
    /// Executes a query and returns the first column of every row as strings
    /// (NULL values are preserved as null). Used for reading <c>json(data)</c> documents.
    /// </summary>
    public static async Task<List<string?>> QueryStringsAsync(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<string?>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return results;
    }

    /// <summary>
    /// Executes a query and returns the first two columns of every row as strings (NULL values
    /// are preserved as null). Used for reading <c>id, json(data)</c> pairs.
    /// </summary>
    public static async Task<List<(string? First, string? Second)>> QueryStringPairsAsync(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var results = new List<(string? First, string? Second)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add((
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return results;
    }

    /// <summary>
    /// Executes a statement whose first row has a single integer column, and returns null when
    /// it produced no row at all. Used for the version-returning concurrency writes
    /// (<c>RETURNING version</c>, where no row means the guard did not match) and for reading a
    /// stored version.
    /// </summary>
    /// <remarks>
    /// A <c>RETURNING</c> statement must be stepped through a reader, not
    /// <c>ExecuteNonQuery</c>, for SQLite to run it to completion.
    /// </remarks>
    public static async Task<long?> QueryFirstInt64Async(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        return reader.GetInt64(0);
    }

    /// <summary>
    /// Executes a query whose first row has a string first column and an integer second
    /// column (e.g. <c>SELECT json(data), version</c>). Returns null when there is no row.
    /// </summary>
    public static async Task<(string? Text, long Number)?> QueryFirstStringInt64Async(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var text = reader.IsDBNull(0) ? null : reader.GetString(0);
        var number = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
        return (text, number);
    }

    /// <summary>
    /// Executes a statement whose first row has two integer columns, returning null when it
    /// produced no row. Used by the streamed blob write, whose reserve statement hands back the
    /// rowid to fill and the version it stored.
    /// </summary>
    public static async Task<(long First, long Second)?> QueryFirstInt64PairAsync(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>
    /// Executes a query and returns the first column of the first row as a string together with
    /// whether the statement produced a row at all.
    /// </summary>
    /// <remarks>
    /// <see cref="QueryFirstStringAsync"/> collapses "no row" and "row whose column is NULL" into
    /// the same null, which is right for the schema and PRAGMA reads but not for a document read:
    /// a row that exists and reads back as nothing is a corrupt row, not a missing one.
    /// </remarks>
    public static async Task<(string? Text, bool Found)> QueryFirstStringRowAsync(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (null, false);
        }

        return (reader.IsDBNull(0) ? null : reader.GetString(0), true);
    }

    /// <summary>
    /// Executes a query and returns the first column of the first row as a string,
    /// or null when there is no row or the value is NULL.
    /// </summary>
    public static async Task<string?> QueryFirstStringAsync(
        this SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, commandText, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Synchronous variant of <see cref="QueryFirstStringAsync"/>, used on the disposal path.
    /// </summary>
    public static string? QueryFirstString(this SqliteConnection connection, string commandText)
    {
        using var command = CreateCommand(connection, commandText, []);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        string commandText,
        (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;

        foreach (var (name, value) in parameters)
        {
            var parameterName = name.StartsWith('@') ? name : "@" + name;
            command.Parameters.AddWithValue(parameterName, value ?? DBNull.Value);
        }

        return command;
    }

    private static T? ConvertScalar<T>(object? result)
    {
        if (result is null or DBNull)
        {
            return default;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (result.GetType() == targetType)
        {
            return (T)result;
        }

        return (T)Convert.ChangeType(result, targetType, CultureInfo.InvariantCulture);
    }
}
