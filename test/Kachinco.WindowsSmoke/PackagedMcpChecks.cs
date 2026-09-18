using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows.Automation;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class PackagedMcpChecks
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);
    private static readonly string CleanPath = Environment.GetFolderPath(Environment.SpecialFolder.System);

    public static async Task Run(string bundle)
    {
        string editor = Path.Combine(bundle, "Kachinco.App.exe"), bridge = Path.Combine(bundle, "mcp", "Kachinco.Mcp.exe");
        Check(File.Exists(editor) && File.Exists(bridge), "Published executables missing.");
        Check(!Directory.GetFiles(bundle, "*", SearchOption.AllDirectories).Any(p =>
            Path.GetFileName(p).StartsWith("ModelContextProtocol", StringComparison.Ordinal) ||
            Path.GetFileName(p).Contains("Tests", StringComparison.Ordinal) ||
            Path.GetFileName(p).Contains("WindowsSmoke", StringComparison.Ordinal)), "Test SDK/runtime entered package.");
        var start = new ProcessStartInfo(editor) { UseShellExecute = false, WorkingDirectory = bundle };
        start.Environment["PATH"] = CleanPath;
        using var process = Process.Start(start)!;
        try
        {
            AutomationElement? window = null;
            await Until(() => { process.Refresh(); if (process.HasExited) throw new Exception("Published editor exited.");
                if (process.MainWindowHandle == 0) return false;
                window = AutomationElement.FromHandle(process.MainWindowHandle); return window is not null; });
            var main = window!;
            await Menu(main, "FileMenu", "NewLandscapeMenu");
            await Menu(main, "McpMenu", "McpEditMenu");
            string pipe = await Connection(process.Id);
            await using (var client = await Connect(bridge, pipe))
            {
                var tools = await client.ListToolsAsync(cancellationToken: Deadline());
                Check(tools.Any(t => t.Name == "edit_batch") && !tools.Any(t => t.Name == "export_start"), "Discovery permissions.");
                var state = await Query(client);
                var sequence = state.GetProperty("project").GetProperty("project").GetProperty("sequences")[0];
                var sequenceId = sequence.GetProperty("id").GetGuid(); var trackId = Guid.NewGuid(); var captionId = Guid.NewGuid();
                string beforeRevision = state.GetProperty("revision").GetString()!;
                object[] commands = [new { type = "AddTrack", sequenceId, trackId, name = "MCP acceptance", kind = "Subtitle" },
                    new { type = "AddCaption", sequenceId, trackId, caption = new { id = captionId, startTicks = "0", durationTicks = "35280000", text = "Shared history", enabled = true } }];
                var edit = await Call(client, "edit_batch", new() { ["expectedRevision"] = beforeRevision, ["commands"] = commands });
                Check(edit.GetProperty("success").GetBoolean(), "External transaction failed.");
                await Until(() => VisibleText(main, "MCP acceptance"));
                await Invoke(Find(main, "UndoButton"));
                Check(!HasTrack(await Query(client), trackId), "UI Undo did not undo both commands.");
                await Until(() => !VisibleText(main, "MCP acceptance"));
                await Invoke(Find(main, "RedoButton"));
                Check(HasTrack(await Query(client), trackId), "UI Redo did not restore MCP transaction.");
                await Until(() => VisibleText(main, "MCP acceptance"));
                var stale = await Call(client, "edit_batch", new() { ["expectedRevision"] = beforeRevision, ["commands"] = commands });
                Check(!stale.GetProperty("success").GetBoolean(), "Stale revision accepted.");
                state = await Query(client); string revision = state.GetProperty("revision").GetString()!;
                var rollback = await Call(client, "edit_batch", new() { ["expectedRevision"] = revision, ["commands"] = new object[] {
                    new { type = "AddTrack", sequenceId, trackId = Guid.NewGuid(), name = "Must roll back", kind = "Video" },
                    new { type = "DeleteClip", sequenceId, clipId = Guid.NewGuid() } } });
                Check(!rollback.GetProperty("success").GetBoolean() && (await Query(client)).GetProperty("revision").GetString() == revision, "Atomic rollback failed.");
                await Menu(main, "SequenceMenu", "AddVideoTrackMenu");
                state = await Query(client); Check(state.GetProperty("revision").GetString() != revision, "UI edit not visible to MCP.");
                await Call(client, "undo", new() { ["expectedRevision"] = state.GetProperty("revision").GetString() });
                state = await Query(client);
                await Call(client, "redo", new() { ["expectedRevision"] = state.GetProperty("revision").GetString() });
                await Menu(main, "McpMenu", "McpReadOnlyMenu");
                await ExpectDisconnected(client);
            }
            await OldPipeRejected(pipe);
            string readPipe = await Connection(process.Id);
            Check(pipe != readPipe, "Permission change reused address.");
            await using (var client = await Connect(bridge, readPipe))
            {
                var tools = await client.ListToolsAsync(cancellationToken: Deadline());
                Check(!tools.Any(t => t.Name == "undo" || t.Name == "edit_batch"), "Read-only discovery exposed edits.");
                bool denied = false;
                try { await Call(client, "undo", new() { ["expectedRevision"] = (await Query(client)).GetProperty("revision").GetString() }); }
                catch (Exception) { denied = true; }
                Check(denied, "Read-only direct history call accepted.");
                // Exercise document replacement through the ordinary New menu and confirmation.
                var replacing = Menu(main, "FileMenu", "NewLandscapeMenu");
                await ConfirmDiscard(process.Id);
                await replacing;
                await ExpectDisconnected(client);
            }
            await OldPipeRejected(readPipe);
            await Menu(main, "McpMenu", "McpEditMenu");
            string stoppedPipe = await Connection(process.Id);
            await using (var client = await Connect(bridge, stoppedPipe))
            {
                await Query(client);
                await Menu(main, "McpMenu", "McpStopMenu");
                await ExpectDisconnected(client);
            }
            await OldPipeRejected(stoppedPipe);
            await Menu(main, "McpMenu", "McpEditMenu");
            string eofPipe = await Connection(process.Id);
            await BridgeEof(bridge, eofPipe, process);
            Console.WriteLine("Published MCP: official C# SDK 1.0.0 / 2025-03-26 fallback; typed discovery; external track+caption transaction; automatic WPF projection; UI Undo/Redo; UI edit -> MCP query; MCP history; rollback/stale revisions; downgrade/New/Stop revocation; editor EOF: PASS. Editor+bridge PATH contains Windows System32 only.");
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private static CancellationToken Deadline() => new CancellationTokenSource(Limit).Token;
    private static async Task<McpClient> Connect(string bridge, string pipe) => await McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = bridge, Arguments = ["--pipe", pipe], Name = "Packaged Kachinco",
        EnvironmentVariables = new Dictionary<string, string?> { ["PATH"] = CleanPath }
    }), cancellationToken: Deadline());
    private static async Task<JsonElement> Call(McpClient client, string name, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(name, args, cancellationToken: Deadline());
        using var json = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        return json.RootElement.Clone();
    }
    private static Task<JsonElement> Query(McpClient client) => Call(client, "get_project", []);
    private static bool HasTrack(JsonElement state, Guid id) => state.GetProperty("project").GetProperty("project").GetProperty("sequences").EnumerateArray()
        .SelectMany(s => s.GetProperty("tracks").EnumerateArray()).Any(t => t.GetProperty("id").GetGuid() == id);
    private static AutomationElement Find(AutomationElement root, string id) => root.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new Exception("Missing UI control: " + id);
    private static Task Invoke(AutomationElement element) => Task.Run(() => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke());
    private static async Task Menu(AutomationElement root, string parent, string child)
    {
        var menu = Find(root, parent);
        ((ExpandCollapsePattern)menu.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        await Invoke(Find(root, child));
    }
    private static bool VisibleText(AutomationElement root, string text) => root.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>().Any(e => e.Current.Name.Contains(text, StringComparison.Ordinal));
    private static async Task<string> Connection(int processId)
    {
        string? pipe = null;
        await Until(() =>
        {
            var edits = AutomationElement.RootElement.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
            foreach (AutomationElement edit in edits)
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                {
                    string command = ((ValuePattern)pattern).Current.Value;
                    int at = command.IndexOf(" --pipe kachinco-", StringComparison.Ordinal);
                    if (at >= 0) { pipe = command[(at + 8)..]; return true; }
                }
            return false;
        });
        return pipe!;
    }
    private static async Task ConfirmDiscard(int processId)
    {
        AutomationElement? yes = null;
        await Until(() => (yes = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId), new PropertyCondition(AutomationElement.AutomationIdProperty, "6")))) is not null);
        await Invoke(yes!);
    }
    private static async Task ExpectDisconnected(McpClient client)
    {
        bool failed = false;
        try { await Query(client); } catch (Exception) { failed = true; }
        Check(failed, "Revoked client retained access.");
    }
    private static async Task OldPipeRejected(string name)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        bool rejected = false;
        try { await pipe.ConnectAsync(300); } catch (TimeoutException) { rejected = true; }
        Check(rejected, "Old address accepted a connection.");
    }
    private static async Task BridgeEof(string bridge, string pipe, Process editor)
    {
        var start = new ProcessStartInfo(bridge) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipe); start.Environment["PATH"] = CleanPath;
        using var child = Process.Start(start)!;
        await child.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
        Check(await child.StandardOutput.ReadLineAsync().WaitAsync(Limit) is not null, "Bridge failed to attach.");
        editor.Kill(true); await editor.WaitForExitAsync();
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { if (!child.HasExited) child.Kill(true); }
    }
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = Stopwatch.StartNew();
        while (!predicate()) { if (deadline.Elapsed > Limit) throw new TimeoutException("Packaged UI condition not observed."); await Task.Delay(100); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
