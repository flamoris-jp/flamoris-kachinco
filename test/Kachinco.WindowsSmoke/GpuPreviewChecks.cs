using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class GpuPreviewChecks
{
    public static async Task Run(MainWindow main)
    {
        const long t = TimelineTime.TicksPerSecond;
        var session = (EditorSession)Field(main, "session")!;
        var playback = (InteractivePreview)Field(main, "playback")!;
        var source = (InteractivePreviewSource)Field(main, "previewSource")!;
        var selector = (ComboBox)main.FindName("PreviewBackendBox");
        var quality = (ComboBox)main.FindName("PreviewQualityBox");
        var viewer = (Image)main.FindName("PreviewImage");
        var timeline = (TimelineSurface)main.FindName("Timeline");
        int originalBackend = selector.SelectedIndex, originalQuality = quality.SelectedIndex;
        string root = Path.Combine(Path.GetTempPath(), "kachinco-gpu-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Preference must survive selecting a backend before a project is open.
            selector.SelectedIndex = 1;
            Check(playback.BackendPreference == PreviewBackendPreference.Cpu, "CPU selector event sets intent without a context");
            string imagePath = Path.Combine(root, "source.png");
            WriteImage(imagePath);
            var sequence = Guid.NewGuid(); var track = Guid.NewGuid(); var image = Guid.NewGuid(); var clip = Guid.NewGuid();
            Check(session.Execute(new([
                new CreateProject(Guid.NewGuid(), "GPU selector fixture"),
                new CreateSequence(sequence, "Preview", SequenceSettings.Landscape, 4 * t),
                new AddTrack(sequence, track, "V1", TrackKind.Video),
                new RegisterMedia(new(image, "source.png", imagePath, MediaKind.Image, t)),
                new InsertClip(sequence, track, new(clip, image, 0, 0, 4 * t, true,
                    new(new(8, 0, 1, 1, 0), 1, BlendMode.Normal), AudioProperties.Default))])).Success, "transformed image fixture");
            Set(main, "selectedSequenceId", sequence);
            Set(main, "savedJson", ProjectJson.Serialize(session.GetProject().Project!).Value);
            quality.SelectedIndex = 2;
            Refresh(main);
            await Settled(PreviewBackendPreference.Cpu);
            long revision = session.GetProject().Revision;
            byte[] cpu = ViewerPixels();
            Check(cpu.Where((_, i) => i % 4 == 2).Any(value => value > 0), "transformed decoded PNG reaches WPF");
            Check(source.BackendDiagnostics.Active == "CPU" && source.BackendDiagnostics.AllocatedBytes == 0, "forced CPU owns no GPU allocation");

            selector.SelectedIndex = 2;
            await Settled(PreviewBackendPreference.D3D11);
            var gpu = source.BackendDiagnostics;
            Check(gpu.Active is "D3D11" or "CPU", "requested GPU chooses a supported backend");
            Check(gpu.Active == "D3D11" || !string.IsNullOrWhiteSpace(gpu.FallbackReason), "unavailable hardware reports CPU fallback");
            Check(gpu.AllocatedBytes <= PreviewRenderBackend.DefaultGpuBudgetBytes, "GPU allocation remains bounded");
            Check(cpu.SequenceEqual(ViewerPixels()), "backend selection preserves this integer translated frame");
            var status = (TextBlock)main.FindName("PreviewBackendStatus");
            Check(status.Text.StartsWith(gpu.Active, StringComparison.Ordinal), "status displays the active compositor");

            // One dispatcher turn contains competing selections and a seek. The
            // final CPU intent must win after the old frame task has joined.
            selector.SelectedIndex = 0;
            timeline.SetCursorTicks(t / 2);
            selector.SelectedIndex = 2;
            selector.SelectedIndex = 1;
            await Settled(PreviewBackendPreference.Cpu);
            Check(playback.Frame?.Tick == t / 2 && playback.State == InteractivePreviewState.Paused, "rapid paused selection preserves the latest seek");
            Check(source.BackendDiagnostics.AllocatedBytes == 0 && source.BackendDiagnostics.Active == "CPU", "CPU switch releases GPU resources");

            // Exercise the actual Play/selector/Pause events while work is
            // buffering. Pause is issued before dispatcher continuations run,
            // so this requires no physical audio device or timing assumption.
            selector.SelectedIndex = 2;
            await Settled(PreviewBackendPreference.D3D11);
            var play = (Button)main.FindName("PlayButton");
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            selector.SelectedIndex = 1;
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settled(PreviewBackendPreference.Cpu);
            Check(playback.State == InteractivePreviewState.Paused && playback.Frame?.Tick == t / 2, "later Pause survives a backend change during buffering");
            Check(session.GetProject().Revision == revision, "backend and transport preferences do not edit the project");
            Console.WriteLine($"WPF preview backend selector, PNG presentation, latest intent and joined CPU recovery: PASS (requested D3D11 used {gpu.Active})");
        }
        finally
        {
            playback.Pause();
            await playback.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            session.ReplaceProject(null);
            Set(main, "selectedSequenceId", null); Set(main, "selectedClipId", null); Set(main, "savedJson", null);
            Refresh(main);
            await playback.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            selector.SelectedIndex = originalBackend; quality.SelectedIndex = originalQuality;
            await Until(() => timeline.VisualizationWorkers == 0);
            Directory.Delete(root, true);
        }

        async Task Settled(PreviewBackendPreference preference)
        {
            await Until(() => playback.Completion.IsCompleted && !playback.HasPendingRequest &&
                source.BackendDiagnostics.Requested == preference);
            await playback.Completion;
            Check(playback.BackendPreference == preference, "selector keeps the requested preference");
            Check(playback.State != InteractivePreviewState.Failed, playback.Error ?? "frame preparation failed");
            Check(viewer.Source is BitmapSource && playback.Presentation is not null, "prepared presentation reaches WPF");
            Check(ViewerPixels().SequenceEqual(playback.Presentation!.Bgra8), "WPF receives the prepared BGRA bytes");
        }
        byte[] ViewerPixels()
        {
            Check(viewer.Source is BitmapSource, "viewer contains a bitmap");
            var bitmap = (BitmapSource)viewer.Source;
            var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
            return bytes;
        }
    }
    private static void WriteImage(string path)
    {
        var pixels = new byte[64 * 64 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new Exception("GPU preview UI operation timed out.");
            await Task.Delay(10);
        }
    }
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static void Refresh(MainWindow main) => typeof(MainWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, new object?[] { null });
    private static void Check(bool value, string message) { if (!value) throw new Exception("GPU preview: " + message); }
}
