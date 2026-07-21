using System.Text.Json;
using Sdat.Core.Commands;
using Xunit;

namespace Sdat.Core.Tests;

public sealed class MachineResponseTests
{
    [Fact]
    public void Success_envelope_has_a_stable_versioned_shape()
    {
        var response = MachineResponse<object>.Succeeded(
            "status",
            new { active = 1 },
            [new MachineWarning("Degraded", "projection warning")],
            success: false);

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("status", root.GetProperty("operation").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("result").GetProperty("active").GetInt32());
        Assert.Equal("Degraded", root.GetProperty("warnings")[0].GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }

    [Fact]
    public void Failure_envelope_separates_error_code_and_message()
    {
        var response = MachineResponse<object>.Failed("schedule", "InvalidTime", "Time is invalid.");

        Assert.False(response.Success);
        Assert.Null(response.Result);
        Assert.Equal("InvalidTime", response.Error!.Code);
        Assert.Empty(response.Warnings);
    }
}
