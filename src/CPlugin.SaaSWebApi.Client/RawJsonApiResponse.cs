using System.Text.Json.Serialization;
using CPlugin.SaaSWebApi.Models;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>v2 envelope whose <c>data</c> is arbitrary JSON (server-side <c>JsonNode</c>).</summary>
/// <remarks>
/// The OpenAPI document models the server's <c>JsonNode</c> structurally (Options/Parent/...),
/// which NSwag turns into an unusable POCO. Free-form endpoints (e.g. the sidecar
/// <c>ExternalCommandJSON</c>) instead deserialize through this hand-written envelope so the
/// payload stays a real <see cref="System.Text.Json.Nodes.JsonNode"/>.
/// </remarks>
internal sealed class RawJsonApiResponse
{
    [JsonPropertyName("data")] public System.Text.Json.Nodes.JsonNode? Data { get; set; }
    [JsonPropertyName("error")] public Models.ApiError? Error { get; set; }
    [JsonPropertyName("meta")] public ApiMeta? Meta { get; set; }
}
