using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kachinco.Core;

namespace Kachinco.Infrastructure;

public sealed record RecipeOperation(string Kind, string Text, double X, double Y, double Vx, double Vy, double Size, int Count);
public sealed record RecipeIr(ImmutableArray<RecipeOperation> Operations);

public sealed class RecipeCompiler(string? pythonExecutable = null)
{
    public async Task<Result<RecipeIr>> CompileAsync(string source, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 65536) return Fail("RECIPE_SOURCE_LIMIT", "Source must contain 1-65536 characters.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); lifetime.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = MediaProcess.StartInfo(pythonExecutable ?? (OperatingSystem.IsWindows() ? "python" : "python3"),
            ["-I", "-S", Path.Combine(AppContext.BaseDirectory, "recipe-worker.py")]) };
        process.StartInfo.RedirectStandardInput = process.StartInfo.RedirectStandardOutput = true;
        nint job = 0;
        try
        {
            process.Start();
            using var cancel = lifetime.Token.Register(() => MediaProcess.Kill(process));
            // The trusted worker blocks for input. Apply Windows limits before giving it user source.
            if (OperatingSystem.IsWindows()) job = WorkerLimits.Assign(process);
            var stderr = MediaProcess.DrainErrorAsync(process.StandardError, lifetime.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { source }).AsMemory(), lifetime.Token);
            process.StandardInput.Close();
            var output = new StringBuilder(); var buffer = new char[4096]; int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer.AsMemory(), lifetime.Token)) != 0)
            {
                if (output.Length + read > 262144) throw new InvalidDataException("Worker output limit.");
                output.Append(buffer, 0, read);
            }
            await process.WaitForExitAsync(lifetime.Token); await stderr;
            if (process.ExitCode != 0) return Fail("RECIPE_REJECTED", output.Length == 0 ? "Worker failed; install Python 3 and check worker limits." : output.ToString());
            var ir = JsonSerializer.Deserialize<RecipeIr>(output.ToString(), new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true });
            if (ir is null || ir.Operations.IsDefaultOrEmpty || ir.Operations.Length > 128) return Fail("RECIPE_IR_INVALID", "Invalid operation count.");
            int total = 0;
            foreach (var op in ir.Operations)
            {
                if (op is null || op.Kind is not ("text" or "particles") || op.Text is null || op.Text.Length > 4096 ||
                    op.Kind == "text" && string.IsNullOrWhiteSpace(op.Text) || op.Count < 1 || op.Count > 1000 ||
                    !double.IsFinite(op.Size) || op.Size < 1 || op.Size > 512 || new[] { op.X, op.Y, op.Vx, op.Vy }.Any(x => !double.IsFinite(x) || Math.Abs(x) > 10000))
                    return Fail("RECIPE_IR_INVALID", "Invalid primitive arguments.");
                total += op.Count;
            }
            return total <= 2000 ? Result<RecipeIr>.Ok(ir) : Fail("RECIPE_IR_INVALID", "Primitive count limit.");
        }
        catch (OperationCanceledException) { return Fail(token.IsCancellationRequested ? "CANCELLED" : "RECIPE_TIMEOUT", "Recipe worker stopped."); }
        catch (System.ComponentModel.Win32Exception e) { return Fail("RECIPE_WORKER_UNAVAILABLE", e.Message); }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonException) { return Fail("RECIPE_WORKER_FAILED", e.Message); }
        finally { MediaProcess.Kill(process); if (job != 0) WorkerLimits.CloseHandle(job); }
    }
    private static Result<RecipeIr> Fail(string code, string message) => Result<RecipeIr>.Fail(Diagnostic.Error(code, message));
}

internal static class WorkerLimits
{
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int type, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    internal static nint Assign(Process process)
    {
        nint job = CreateJobObject(0, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 | 0x100 | 0x8, ActiveProcesses = 1 }, ProcessMemory = 128 * 1024 * 1024 };
        if (job == 0 || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job, process.Handle))
        { if (job != 0) CloseHandle(job); throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot enforce Recipe worker limits."); }
        return job;
    }
}
