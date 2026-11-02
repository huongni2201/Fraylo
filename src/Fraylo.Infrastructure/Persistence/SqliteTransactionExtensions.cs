using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Fraylo.Infrastructure.Persistence;

public static class SqliteTransactionExtensions
{
    /// <summary>
    /// BEGIN IMMEDIATE: chiếm khóa ghi SQLite ngay khi mở transaction để các bước check-then-write không bị chen ngang.
    /// Kết nối phải đã mở.
    /// </summary>
    public static async Task<IDbContextTransaction> BeginImmediateAsync(this DatabaseFacade database, CancellationToken cancellationToken)
    {
        var connection = (SqliteConnection)database.GetDbConnection();
        var transaction = connection.BeginTransaction(deferred: false);
        return await database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Không thể liên kết DbContextTransaction từ SqliteTransaction.");
    }
}
