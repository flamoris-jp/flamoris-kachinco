using System.Text.Json;
using Flamoris.Mcp.Core;

namespace Kachinco.Infrastructure;

/// <summary>Pure compilation query; persistent edits still use the ordinary native document command.</summary>
public static class KineticScriptMcpTools
{
    public static HostTool Create() => new HostTool<string>("compile_script",
        "Compile bounded Python animation source to native motion IR without editing or saving. Commit program and source via edit_composition.",
        JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false,
            required = new[] { "source" }, properties = new { source = new { type = "string", maxLength = 65536 } } }),
        OperationKind.Query, input => {
            if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Count() != 1) throw new JsonException();
            return input.GetProperty("source").GetString() ?? throw new JsonException();
        }, async (context, source, token) => {
            var ir = await new KineticProgramCompiler().CompileAsync(source, token);
            return await context.ReadAsync(() => {
                var probe = KineticComposition.New(); probe["clips"]![0]!["program"] = ir.DeepClone();
                KineticComposition.Validate(probe);
                return JsonSerializer.SerializeToElement(new { program = ir, source });
            });
        });
}
