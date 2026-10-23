using System.Text.Json.Nodes;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public static class KineticHandoff
{
    public static JsonObject Create(Guid projectId, long revision, Guid assetId, JsonObject composition) => new()
    {
        ["version"] = 1, ["handoffId"] = Guid.NewGuid().ToString(), ["projectId"] = projectId.ToString(),
        ["expectedRevision"] = revision.ToString(System.Globalization.CultureInfo.InvariantCulture), ["assetId"] = assetId.ToString(),
        ["composition"] = KineticComposition.Validate(composition)
    };
    public static JsonObject Response(JsonObject request, JsonObject composition)
    {
        var result = (JsonObject)request.DeepClone(); result["composition"] = KineticComposition.Validate(composition); return result;
    }
    public static string ValidateResponse(JsonObject request, JsonObject response)
    {
        foreach (string field in new[] { "version", "handoffId", "projectId", "expectedRevision", "assetId" })
            if (!JsonNode.DeepEquals(request[field], response[field])) throw new InvalidDataException("Companion return identity mismatch.");
        var composition = KineticComposition.Validate(response["composition"]!.AsObject());
        if (!JsonNode.DeepEquals(request["composition"]!["id"], composition["id"])) throw new InvalidDataException("Companion document identity mismatch.");
        return composition.ToJsonString();
    }
}
