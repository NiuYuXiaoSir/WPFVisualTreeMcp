using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WpfVisualTreeMcp.Shared.Ipc;

/// <summary>
/// Serializes and deserializes IPC messages.
/// </summary>
public static class IpcSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize<T>(T message) where T : class
    {
        return JsonSerializer.Serialize(message, Options);
    }

    public static T? Deserialize<T>(string json) where T : class
    {
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    public static string SerializeRequest(IpcRequest request)
    {
        // Wrap with type info for deserialization.
        // data must be declared as object: System.Text.Json serializes by declared type,
        // and typing it as IpcRequest would silently drop every derived-class property
        // (TypeName, ElementHandle, MaxResults, ...).
        var wrapper = new
        {
            type = request.RequestType,
            data = (object)request
        };
        return JsonSerializer.Serialize(wrapper, Options);
    }

    public static (string type, JsonElement data)? DeserializeRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeElement) &&
                root.TryGetProperty("data", out var dataElement))
            {
                return (typeElement.GetString() ?? "", dataElement.Clone());
            }
        }
        catch
        {
            // Invalid JSON
        }

        return null;
    }

    public static T? DeserializeRequestData<T>(JsonElement data) where T : IpcRequest
    {
        return data.Deserialize<T>(Options);
    }

    public static string SerializeResponse(IpcResponse response)
    {
        return JsonSerializer.Serialize(response, response.GetType(), Options);
    }

    public static T? DeserializeResponse<T>(string json) where T : IpcResponse
    {
        return JsonSerializer.Deserialize<T>(json, Options);
    }

    /// <summary>
    /// Merges multiple FindElements-style JSON documents ({"elements":[...],"count":N})
    /// into one document with a concatenated elements array. Each input is parsed as
    /// JSON — no delimiter string surgery, which breaks on string values containing
    /// brackets. Input order is preserved; the output count is the true element total.
    /// </summary>
    public static string MergeElementArrays(IEnumerable<string> documents)
    {
        var buffer = new StringBuilder("{\"elements\":[");
        var total = 0;
        var first = true;

        foreach (var json in documents)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("elements", out var elements) ||
                elements.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var element in elements.EnumerateArray())
            {
                if (!first)
                {
                    buffer.Append(',');
                }

                first = false;
                buffer.Append(element.GetRawText());
                total++;
            }
        }

        buffer.Append("],\"count\":").Append(total).Append('}');
        return buffer.ToString();
    }
}
