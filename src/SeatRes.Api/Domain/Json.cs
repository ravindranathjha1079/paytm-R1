using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SeatRes.Api.Domain;

public static class Json
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.NumberHandling = JsonNumberHandling.Strict;
        return o;
    }

    public static JsonObject ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!.AsObject();
}
