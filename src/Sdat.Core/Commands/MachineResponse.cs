namespace Sdat.Core.Commands;

public sealed record MachineWarning(string Code, string Message);

public sealed record MachineError(string Code, string Message);

public sealed record MachineResponse<T>(
    int SchemaVersion,
    string Operation,
    bool Success,
    T? Result,
    IReadOnlyList<MachineWarning> Warnings,
    MachineError? Error)
{
    public const int CurrentSchemaVersion = 1;

    public static MachineResponse<T> Succeeded(
        string operation,
        T result,
        IReadOnlyList<MachineWarning>? warnings = null,
        bool success = true) => new(
        CurrentSchemaVersion,
        operation,
        success,
        result,
        warnings ?? [],
        null);

    public static MachineResponse<T> Failed(string operation, string code, string message) => new(
        CurrentSchemaVersion,
        operation,
        false,
        default,
        [],
        new MachineError(code, message));
}
