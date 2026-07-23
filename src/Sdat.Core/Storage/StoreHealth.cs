namespace Sdat.Core.Storage;

public enum StoreHealthStatus
{
    Healthy,
    Corrupt,
    Unavailable,
}

public sealed record StoreHealth(StoreHealthStatus Status, string Detail)
{
    public bool CanExecutePowerActions => Status == StoreHealthStatus.Healthy;
}
