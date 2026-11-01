namespace Fraylo.Infrastructure.Persistence;

public record DatabaseBootstrapResult(
    string DatabasePath,
    string SqliteVersion,
    string JournalMode,
    bool ForeignKeysEnabled,
    int BusyTimeout,
    int SynchronousMode,
    int SchemaVersion,
    bool DatabaseReset,
    bool IsBootstrapSuccessful);
