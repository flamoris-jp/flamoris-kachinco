using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.IO.Compression;
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
        using (var archive = ZipFile.OpenRead(bundle + ".zip"))
        {
            var files = Directory.GetFiles(bundle, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(bundle, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            var packaged = archive.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName.Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            Check(files.SequenceEqual(packaged), "ZIP differs from reviewed publish directory.");
        }
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
            await using (var connection = new RevokedClient(await Connect(bridge, pipe)))
            {
                var client = connection.Client;
                Check(client.NegotiatedProtocolVersion == "2025-03-26", "Unexpected negotiated protocol.");
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
                int uiTrackCount = TrackCount(state);
                var undone = await Call(client, "undo", new() { ["expectedRevision"] = state.GetProperty("revision").GetString() });
                state = await Query(client);
                Check(undone.GetProperty("success").GetBoolean() && TrackCount(state) == uiTrackCount - 1, "MCP Undo did not reverse the UI edit.");
                var redone = await Call(client, "redo", new() { ["expectedRevision"] = state.GetProperty("revision").GetString() });
                Check(redone.GetProperty("success").GetBoolean() && TrackCount(await Query(client)) == uiTrackCount, "MCP Redo did not restore the UI edit.");
                Console.WriteLine("Packaged MCP edits, automatic projection, shared UI/MCP history, rollback and stale revision: PASS");
                await Menu(main, "McpMenu", "McpReadOnlyMenu");
                await ExpectDisconnected(connection);
            }
            Console.WriteLine("Permission downgrade closed existing client: PASS");
            await OldPipeRejected(pipe);
            string readPipe = await Connection(process.Id);
            Check(pipe != readPipe, "Permission change reused address.");
            await using (var connection = new RevokedClient(await Connect(bridge, readPipe)))
            {
                var client = connection.Client;
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
                await ExpectDisconnected(connection);
            }
            Console.WriteLine("Read-only direct-call rejection and New revocation: PASS");
            await OldPipeRejected(readPipe);
            await Menu(main, "McpMenu", "McpEditMenu");
            string stoppedPipe = await Connection(process.Id);
            await using (var connection = new RevokedClient(await Connect(bridge, stoppedPipe)))
            {
                var client = connection.Client;
                await Query(client);
                await Menu(main, "McpMenu", "McpStopMenu");
                await ExpectDisconnected(connection);
            }
            await OldPipeRejected(stoppedPipe);
            await DocumentLoss(main, process.Id, bridge, readOnly: false, mcpUndo: false);
            await DocumentLoss(main, process.Id, bridge, readOnly: false, mcpUndo: true);
            await DocumentLoss(main, process.Id, bridge, readOnly: true, mcpUndo: false);
            await SameFileReopen(main, process.Id, bridge);
            await Menu(main, "McpMenu", "McpEditMenu");
            string eofPipe = await Connection(process.Id);
            await BridgeInputEof(bridge, eofPipe);
            await BridgeEof(bridge, eofPipe, process);
            Console.WriteLine("Published MCP: official C# SDK 1.0.0 / 2025-03-26 fallback; typed discovery; external track+caption transaction; automatic WPF projection; UI Undo/Redo; UI edit -> MCP query; MCP history; rollback/stale revisions; downgrade/New/Stop revocation; editor EOF: PASS. Editor+bridge PATH contains Windows System32 only.");
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private static Guid ProjectId(JsonElement state) => state.GetProperty("project").GetProperty("project").GetProperty("id").GetGuid();
    private static async Task DocumentLoss(AutomationElement main, int processId, string bridge, bool readOnly, bool mcpUndo)
    {
        var replacing = Menu(main, "FileMenu", "NewLandscapeMenu");
        await ConfirmDiscard(processId); await replacing;
        await Menu(main, "McpMenu", readOnly ? "McpReadOnlyMenu" : "McpEditMenu");
        string pipe = await Connection(processId);
        await using var connection = new RevokedClient(await Connect(bridge, pipe));
        var state = await Query(connection.Client); var originalId = ProjectId(state);
        if (mcpUndo)
        {
            // The commit succeeds, then revocation closes the pipe before its response.
            try { await Call(connection.Client, "undo", new() { ["expectedRevision"] = state.GetProperty("revision").GetString() }); }
            catch (IOException) { }
            catch (ModelContextProtocol.McpException) { }
        }
        else await Invoke(Find(main, "UndoButton"));
        await Until(() => !Find(main, "UndoButton").Current.IsEnabled && Find(main, "RedoButton").Current.IsEnabled);
        await ExpectDisconnected(connection);
        await OldPipeRejected(pipe);
        var mcpMenu = (ExpandCollapsePattern)Find(main, "McpMenu").GetCurrentPattern(ExpandCollapsePattern.Pattern);
        mcpMenu.Expand();
        Check(!Find(main, "McpCopyMenu").Current.IsEnabled, "Document loss retained connection information.");
        mcpMenu.Collapse();
        // Human Redo is preserved, without resurrecting the revoked client or address.
        await Invoke(Find(main, "RedoButton"));
        await Until(() => Find(main, "UndoButton").Current.IsEnabled);
        await ExpectDisconnected(connection); await OldPipeRejected(pipe);
        await Invoke(Find(main, "UndoButton"));
        await Until(() => !Find(main, "UndoButton").Current.IsEnabled);
        await Invoke(Find(main, "AddLandscapeSequenceButton"));
        await Until(() => Find(main, "UndoButton").Current.IsEnabled);
        await ExpectDisconnected(connection); await OldPipeRejected(pipe);
        await Menu(main, "McpMenu", "McpEditMenu");
        string freshPipe = await Connection(processId);
        Check(freshPipe != pipe, "New document reused old grant address.");
        await using (var fresh = new RevokedClient(await Connect(bridge, freshPipe)))
        {
            state = await Query(fresh.Client);
            Check(ProjectId(state) != originalId, "AddSequence did not implicitly create a new Project.");
            var sequenceId = state.GetProperty("project").GetProperty("project").GetProperty("sequences")[0].GetProperty("id").GetGuid();
            var args = new Dictionary<string, object?> { ["expectedRevision"] = state.GetProperty("revision").GetString(),
                ["commands"] = new object[] { new { type = "AddTrack", sequenceId, trackId = Guid.NewGuid(), name = "Reauthorized", kind = "Video" } } };
            bool denied = false;
            try { await Call(connection.Client, "edit_batch", args); }
            catch (IOException) { denied = true; }
            catch (ModelContextProtocol.McpException) { denied = true; }
            Check(denied, "Old client edited the new document with its current revision.");
            Check((await Call(fresh.Client, "edit_batch", args)).GetProperty("success").GetBoolean(), "Explicit re-enable did not allow B editing.");
            await Menu(main, "McpMenu", "McpStopMenu"); await ExpectDisconnected(fresh);
        }
        Console.WriteLine($"Project creation Undo ({(readOnly ? "Read only" : "Edit")}, {(mcpUndo ? "MCP" : "UI")}) -> null -> human Redo -> implicit B; old query/edit/address denied; fresh grant edits B: PASS");
    }
    private static async Task SameFileReopen(AutomationElement main, int processId, string bridge)
    {
        string path = Path.Combine(Path.GetTempPath(), "kachinco-reopen-" + Guid.NewGuid().ToString("N") + ".fkproj");
        try
        {
            await Menu(main, "McpMenu", "McpReadOnlyMenu");
            string pipe = await Connection(processId);
            Guid id;
            await using (var connection = new RevokedClient(await Connect(bridge, pipe)))
            {
                var state = await Query(connection.Client); id = ProjectId(state);
                await File.WriteAllTextAsync(path, state.GetProperty("project").GetRawText());
                var opening = Menu(main, "FileMenu", "OpenProjectMenu");
                await ConfirmDiscard(processId); await ChooseProjectFile(processId, path); await opening;
                await ExpectDisconnected(connection);
            }
            await OldPipeRejected(pipe);
            // The file is now clean; opening the exact same file has no discard prompt.
            await Menu(main, "McpMenu", "McpReadOnlyMenu");
            pipe = await Connection(processId);
            await using (var connection = new RevokedClient(await Connect(bridge, pipe)))
            {
                Check(ProjectId(await Query(connection.Client)) == id, "Open changed persistent identity.");
                var opening = Menu(main, "FileMenu", "OpenProjectMenu");
                await ChooseProjectFile(processId, path); await opening;
                await ExpectDisconnected(connection);
            }
            await OldPipeRejected(pipe);
            Console.WriteLine("Open and same-file reopen with the same persistent ID revoke the old client and address: PASS");
        }
        finally { File.Delete(path); }
    }
    private static async Task ChooseProjectFile(int processId, string path)
    {
        AutomationElement? filename = null;
        await Until(() => (filename = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId), new PropertyCondition(AutomationElement.AutomationIdProperty, "1148")))) is not null);
        var edit = filename!.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? filename :
            filename.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        Check(edit is not null, "Open dialog filename control missing.");
        ((ValuePattern)edit!.GetCurrentPattern(ValuePattern.Pattern)).SetValue(path);
        AutomationElement? dialog = edit;
        while (dialog is not null && dialog.Current.ClassName != "#32770") dialog = TreeWalker.ControlViewWalker.GetParent(dialog);
        Check(dialog is not null, "Open dialog missing.");
        await Invoke(Find(dialog!, "1"));
    }
    private static CancellationToken Deadline() => new CancellationTokenSource(Limit).Token;
    private static async Task<McpClient> Connect(string bridge, string pipe) => await McpClient.CreateAsync(new StdioClientTransport(new()
    {
        Command = bridge, Arguments = ["--pipe", pipe], Name = "Packaged Kachinco",
        EnvironmentVariables = new Dictionary<string, string?> { ["PATH"] = CleanPath },
        StandardErrorLines = line => Console.WriteLine("Bridge: " + line)
    }), cancellationToken: Deadline());
    private static async Task<JsonElement> Call(McpClient client, string name, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(name, args, cancellationToken: Deadline());
        using var json = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text);
        return json.RootElement.Clone();
    }
    private static Task<JsonElement> Query(McpClient client) => Call(client, "get_project", []);
    private static int TrackCount(JsonElement state) => state.GetProperty("project").GetProperty("project").GetProperty("sequences").EnumerateArray()
        .Sum(s => s.GetProperty("tracks").GetArrayLength());
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
    private static async Task ExpectDisconnected(RevokedClient connection)
    {
        bool failed = false;
        try { await Query(connection.Client); } catch (IOException) { failed = true; }
        catch (ModelContextProtocol.McpException) { failed = true; }
        Check(failed, "Revoked client retained access.");
        connection.Revoked = true;
    }
    private sealed class RevokedClient(McpClient client) : IAsyncDisposable
    {
        public McpClient Client { get; } = client;
        public bool Revoked { get; set; }
        public async ValueTask DisposeAsync()
        {
            try { await Client.DisposeAsync(); }
            catch (IOException) when (Revoked) { } // SDK reports the already-verified intentional transport loss again.
        }
    }
    private static async Task OldPipeRejected(string name)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        bool rejected = false;
        try { await pipe.ConnectAsync(300); } catch (TimeoutException) { rejected = true; }
        Check(rejected, "Old address accepted a connection.");
    }
    private static async Task BridgeInputEof(string bridge, string pipe)
    {
        var start = new ProcessStartInfo(bridge) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipe); start.Environment["PATH"] = CleanPath;
        using var child = Process.Start(start)!;
        try
        {
            await child.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
            Check(await child.StandardOutput.ReadLineAsync().WaitAsync(Limit) is not null, "Bridge stdin-EOF fixture did not attach.");
            child.StandardInput.Close();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { if (!child.HasExited) child.Kill(true); }
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
