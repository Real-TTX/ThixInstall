using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ThixInstall.Cloud.Data;

namespace ThixInstall.Cloud.Business;

/// <summary>
/// The revisions of the actions of a device. Every change of an action takes the next number of the one sequence <c>ActionRevision</c>, inside a
/// transaction that holds the lock of the device (<c>pg_advisory_xact_lock</c>). So the changes of one device are numbered in the order they are
/// committed: once a device has seen revision N, nothing below N can still appear. After the commit the waiting long polls of the device are woken
/// (<see cref="RevisionSignals"/>).
/// </summary>
public static class Revisions
{
    /// <summary>Runs <paramref name="change"/> in a transaction that holds the lock of the device, then wakes the waiters of the device.</summary>
    public static async Task<T> ChangeAsync<T>(AppDbContext db, long deviceId, Func<Task<T>> change, CancellationToken ct = default)
    {
        T result;
        if (db.Database.CurrentTransaction is not null)
        {
            // already inside a transaction (several devices in one change): take the lock in it, the caller commits
            await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(@p0)", ct, LockKey(deviceId));
            result = await change();
            RevisionSignals.NotifyLater(deviceId);
            return result;
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(@p0)", ct, LockKey(deviceId));
            result = await change();
            await transaction.CommitAsync(ct);
        }

        RevisionSignals.Notify(deviceId);
        RevisionSignals.FlushLater();
        return result;
    }

    /// <summary>The next revision (only call it inside <see cref="ChangeAsync{T}"/>).</summary>
    public static async Task<long> NextAsync(AppDbContext db, CancellationToken ct = default)
        => await ScalarAsync(db, $"SELECT nextval('\"{AppDbContext.RevisionSequence}\"')", ct);

    /// <summary>The highest revision of the actions of a device (0 when it has none).</summary>
    public static async Task<long> LatestAsync(AppDbContext db, long deviceId, CancellationToken ct = default)
        => await db.Actions.Where(a => a.DeviceId == deviceId).MaxAsync(a => (long?)a.Revision, ct) ?? 0;

    /// <summary>The key of the advisory lock of a device (its id, in a range of its own so that nothing else collides).</summary>
    private static long LockKey(long deviceId) => 0x5449_0000_0000_0000L | deviceId;

    internal static async Task<long> ScalarAsync(AppDbContext db, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = await CommandAsync(db, sql, parameters, ct);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static async Task ExecuteAsync(AppDbContext db, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = await CommandAsync(db, sql, parameters, ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A command on the connection of the context, in its transaction; the parameters are @p0, @p1 ...</summary>
    private static async Task<DbCommand> CommandAsync(AppDbContext db, string sql, object[] parameters, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "p" + i;
            parameter.Value = parameters[i];
            command.Parameters.Add(parameter);
        }

        return command;
    }
}

/// <summary>
/// The long polls of the devices, in memory: a waiter takes the signal of its device BEFORE it reads the latest revision, and every committed
/// change completes the signal and replaces it, so no change between the read and the wait is lost. One instance of the server.
/// </summary>
public static class RevisionSignals
{
    private static readonly ConcurrentDictionary<long, TaskCompletionSource> Signals = new();
    private static readonly ConcurrentQueue<long> Pending = new();

    /// <summary>The signal the next change of the device completes.</summary>
    public static Task Next(long deviceId)
        => Signals.GetOrAdd(deviceId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public static void Notify(long deviceId)
    {
        if (Signals.TryRemove(deviceId, out var signal))
        {
            signal.TrySetResult();
        }
    }

    /// <summary>A change inside an outer transaction: the device is woken when the outer change has committed (<see cref="FlushLater"/>).</summary>
    internal static void NotifyLater(long deviceId) => Pending.Enqueue(deviceId);

    /// <summary>Wakes every device noted by <see cref="NotifyLater"/> (called after a commit; waking too often only costs a request).</summary>
    public static void FlushLater()
    {
        while (Pending.TryDequeue(out var deviceId))
        {
            Notify(deviceId);
        }
    }
}
