namespace Sdat.Core.Storage;

public sealed record DatabaseRecoveryResult(
    string RestoredBackupPath,
    string? EvidenceDirectory,
    StoreHealth Health);
