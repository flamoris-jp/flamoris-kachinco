using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kachinco.Infrastructure;

/// <summary>Same trusted AST-only worker and process limits as ordinary Recipes.
/// Produces typed IR; native composition validation is required before commit.</summary>
public sealed class KineticProgramCompiler
{
    public const string ExitExample = """
        def animate(t, duration, i, n, seed):
            span = min(0.35, duration)
            p = clamp((t - duration + span) / span, 0, 1)
            return {
                'x': noise(seed, i, 0) * 100 * p,
                'y': (30 + noise(seed, i, 1) * 50) * p,
                'rotation': noise(seed, i, 2) * 35 * p,
                'opacity': 1 - p,
            }
        """;

    public async Task<JsonObject> CompileAsync(string source, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(source) || Encoding.UTF8.GetByteCount(source) > 65536) throw new InvalidDataException("Script source limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = MediaProcess.StartInfo(OperatingSystem.IsWindows() ? "python" : "python3", ["-I", "-S", Path.Combine(AppContext.BaseDirectory, "recipe-worker.py")]) };
        process.StartInfo.RedirectStandardInput = process.StartInfo.RedirectStandardOutput = true;
        nint job = 0;
        try
        {
            process.Start(); using var cancel = timeout.Token.Register(() => MediaProcess.Kill(process));
            if (OperatingSystem.IsWindows()) job = WorkerLimits.Assign(process);
            var errors = MediaProcess.DrainErrorAsync(process.StandardError, CancellationToken.None);
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { source, mode = "kinetic" }).AsMemory(), timeout.Token); process.StandardInput.Close();
                var output = new StringBuilder(); var chars = new char[4096]; int count;
                while ((count = await process.StandardOutput.ReadAsync(chars.AsMemory(), timeout.Token)) != 0) { if (output.Length + count > 262144) throw new InvalidDataException("Script output limit."); output.Append(chars, 0, count); }
                await process.WaitForExitAsync(timeout.Token); await errors;
                var result = JsonNode.Parse(output.ToString())!.AsObject();
                if (process.ExitCode != 0) throw new InvalidDataException(result["error"]?.GetValue<string>() ?? "Script rejected.");
                return result;
            }
            finally { MediaProcess.Kill(process); await process.WaitForExitAsync(); await errors; }
        }
        finally { MediaProcess.Kill(process); if (job != 0) WorkerLimits.CloseHandle(job); }
    }
}
