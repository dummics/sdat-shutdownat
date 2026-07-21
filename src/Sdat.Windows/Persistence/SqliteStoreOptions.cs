namespace Sdat.Windows.Persistence;

public sealed record SqliteStoreOptions
{
    public required string DatabasePath { get; init; }

    public required string BackupDirectory { get; init; }

    public int BackupRetentionCount { get; init; } = 5;

    public static SqliteStoreOptions CreateDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SDAT");

        return new SqliteStoreOptions
        {
            DatabasePath = Path.Combine(root, "sdat.db"),
            BackupDirectory = Path.Combine(root, "backups"),
        };
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(BackupDirectory);

        if (!Path.IsPathFullyQualified(DatabasePath))
        {
            throw new ArgumentException("The SQLite database path must be absolute.", nameof(DatabasePath));
        }

        if (!Path.IsPathFullyQualified(BackupDirectory))
        {
            throw new ArgumentException("The SQLite backup directory must be absolute.", nameof(BackupDirectory));
        }

        if (BackupRetentionCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BackupRetentionCount));
        }
    }
}
