using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mica.IPC;

public class IpcMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; set; }

    [JsonPropertyName("method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IpcError? Error { get; set; }

    public bool IsRequest => Id.HasValue && Method is not null;
    public bool IsResponse => Id.HasValue && Method is null;
    public bool IsNotification => !Id.HasValue && Method is not null;

    public static IpcMessage CreateResponse(int id, object? result)
    {
        return new IpcMessage
        {
            Id = id,
            Result = JsonSerializer.SerializeToElement(result)
        };
    }

    public static IpcMessage CreateError(int id, int code, string message)
    {
        return new IpcMessage
        {
            Id = id,
            Error = new IpcError { Code = code, Message = message }
        };
    }

    public static IpcMessage CreateNotification(string method, object? parameters)
    {
        return new IpcMessage
        {
            Method = method,
            Params = parameters is not null ? JsonSerializer.SerializeToElement(parameters) : null
        };
    }
}

public class IpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
