using System.Data;
using System.Data.Common;
using ArgusEngine.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArgusEngine.Infrastructure.Orchestration;

internal static class ReconDbCommands
{
    private static DbCommand CreateCommand(ArgusDbContext db, string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            conn.Open();
        }

        var command = conn.CreateCommand();
        command.CommandText = sql;
        ApplyCurrentTransaction(db, command);
        AddParameters(command, parameters);
        return command;
    }

    public static async Task<int> ExecuteAsync(
        ArgusDbContext db,
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(db, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ScalarAsync<T>(
        ArgusDbContext db,
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(db, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return default;
        }

        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        ArgusDbContext db,
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(db, sql, parameters);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static void ApplyCurrentTransaction(ArgusDbContext db, DbCommand command)
    {
        var currentTransaction = db.Database.CurrentTransaction;
        if (currentTransaction is not null)
        {
            command.Transaction = currentTransaction.GetDbTransaction();
        }
    }

    private static void AddParameters(DbCommand command, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    public static string? GetNullableString(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static DateTimeOffset GetDateTimeOffset(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.GetFieldValue<DateTimeOffset>(ordinal);
    }

    public static DateTimeOffset? GetNullableDateTimeOffset(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }
}
