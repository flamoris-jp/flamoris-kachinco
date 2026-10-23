using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Kachinco.Core;
using Kachinco.Infrastructure;
using Microsoft.Win32;

namespace Kachinco.App;
public partial class MainWindow
{
    private async void KineticEdit_Click(object sender, RoutedEventArgs e)
    {
        if (busy || CurrentSequence() is not { } sequence || session.GetProject().Project is not { } project) return;
        string executable = Path.Combine(AppContext.BaseDirectory, "kinetai", "KineticTypo.App.exe");
        if (!File.Exists(executable)) { Status.Text = EditorText.Choose("キネタイを含む完全なパッケージをビルドしてください。", "Build the complete package including KineticTypo."); return; }
        var source = SelectedAsset(project); bool existing = source?.Kind == MediaKind.KineticText;
        var doc = existing ? KineticComposition.Parse(source!.TextComposition!) : KineticComposition.New(sequence.Settings.Width, sequence.Settings.Height, sequence.Settings.FrameRate);
        var snapshot = session.GetProject(); string documentToken = session.DocumentToken; Guid assetId = existing ? source!.Id : Guid.NewGuid();
        long insertionTick = Timeline.PlayheadTicks;
        var request = KineticHandoff.Create(project.Id, snapshot.Revision, assetId, doc);
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FLAMORIS", "Kachinco", "KineticHandoffs", request["handoffId"]!.GetValue<string>());
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "request.json");
        KineticComposition.WriteAtomicForCompanion(path, request.ToJsonString());
        try
        {
            playback.Pause();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.ArgumentList.Add("--handoff"); start.ArgumentList.Add(path);
            using var process = Process.Start(start) ?? throw new IOException("Cannot launch KineticTypo.");
            Status.Text = EditorText.Choose("キネタイで編集中です。", "Editing in KineticTypo.");
            await process.WaitForExitAsync();
            string responsePath = Path.Combine(directory, "response.json");
            if (process.ExitCode != 0 || !File.Exists(responsePath)) { Status.Text = EditorText.Choose("キネタイを終了しました。下書きは保持しています。", "Companion closed; draft retained."); return; }
            if (new FileInfo(responsePath).Length > KineticComposition.MaximumBytes) throw new InvalidDataException("Companion response too large.");
            var response = JsonNode.Parse(await File.ReadAllTextAsync(responsePath))!.AsObject();
            string composition = KineticHandoff.ValidateResponse(request, response);
            if (session.DocumentToken != documentToken || session.GetProject().Project?.Id != project.Id || session.GetProject().Revision != snapshot.Revision)
            { Status.Text = EditorText.Choose("カチンコが変更されたため適用を保留しました。下書き: ", "Host changed; draft retained: ") + Path.Combine(directory, "draft.ktproj"); return; }
            var commands = new List<EditCommand>();
            if (existing) commands.Add(new SetTextComposition(assetId, composition));
            else
            {
                var duration = KineticComposition.Duration(KineticComposition.Parse(composition));
                var asset = new MediaAsset(assetId, "KineticTypo", Path.Combine(directory, "draft.ktproj"), MediaKind.KineticText, duration) { TextComposition = composition };
                commands.Add(new RegisterMedia(asset));
                var trackId = Guid.NewGuid(); commands.Add(new AddTrack(sequence.Id, trackId, "KineticTypo", TrackKind.Video));
                if (insertionTick + duration > sequence.DurationTicks) commands.Add(new SetSequenceDuration(sequence.Id, checked(insertionTick + duration)));
                commands.Add(new InsertClip(sequence.Id, trackId, new(Guid.NewGuid(), assetId, insertionTick, 0, duration, true, ClipAppearance.Default, AudioProperties.Default)));
            }
            using var operation = BeginHumanOperation();
            Show(session.Execute(new([.. commands], snapshot.Revision)), EditorText.Choose("キネタイの文字演出を適用しました。", "Kinetic text applied."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { Status.Text = ex.Message + "  " + directory; }
    }
    private async void KineticImport_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var picker = new OpenFileDialog { Filter = "KineticTypo|*.ktproj;*.ktpair.json" }; if (picker.ShowDialog(this) != true) return;
        var snapshot = session.GetProject();
        try
        {
            MediaAsset asset;
            if (picker.FileName.EndsWith(".ktpair.json", StringComparison.OrdinalIgnoreCase)) asset = await KineticPair.ImportAsync(picker.FileName);
            else { var doc = KineticComposition.Read(picker.FileName); asset = new(Guid.NewGuid(), Path.GetFileNameWithoutExtension(picker.FileName), picker.FileName, MediaKind.KineticText, KineticComposition.Duration(doc)) { TextComposition = doc.ToJsonString() }; }
            using var operation = BeginHumanOperation(); Show(session.Execute(new([new RegisterMedia(asset)], snapshot.Revision)), EditorText.Choose("キネタイ素材を登録しました。", "Kinetic asset registered."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        { Status.Text = ex.Message; }
    }
}
