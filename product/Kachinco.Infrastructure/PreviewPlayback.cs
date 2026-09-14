using Kachinco.Core;

namespace Kachinco.Infrastructure;

public enum PreviewState { Stopped, Preparing, Rendering, Playing, Paused, Failed }

public interface IPreviewPlayer : IDisposable
{
    event EventHandler? Opened;
    event EventHandler? Ended;
    event EventHandler<Exception>? Failed;
    TimeSpan Position { get; set; }
    void Open(string path);
    void Play();
    void Pause();
}

public delegate Task<ExportResult> PreviewRender(ProjectSnapshot snapshot, Guid sequenceId, string path,
    IProgress<ExportProgress> progress, CancellationToken token);

// Transient transport adapter. No project mutations, evaluator or independent clock.
// Calls and callbacks are serialized by the host (WPF Dispatcher in production).
public sealed class PreviewPlayback(Func<IPreviewPlayer> playerFactory, PreviewRender render) : IDisposable
{
    public event EventHandler? Changed;
    public PreviewState State { get; private set; } = PreviewState.Stopped;
    public ExportProgress? Progress { get; private set; }
    public string? Error { get; private set; }
    public long Revision { get; private set; } = -1;
    public Guid? SequenceId { get; private set; }
    public IPreviewPlayer? Player { get; private set; }
    public bool IsPreparing => State is PreviewState.Preparing or PreviewState.Rendering;
    private CancellationTokenSource? preparing;
    private string? previewFile;
    private long generation;
    private long durationTicks;
    private long requestedTicks;
    private bool opened;
    private bool disposed;

    public bool Matches(long revision, Guid? sequenceId) => Revision == revision && SequenceId == sequenceId;

    public async Task PrepareAsync(ProjectSnapshot snapshot, Guid sequenceId, long startTicks, bool autoPlay)
    {
        if (disposed) return;
        var sequence = snapshot.Project?.Sequences.FirstOrDefault(s => s.Id == sequenceId);
        if (sequence is null) { Fail("シーケンスを作成してください。 / Create a sequence first."); return; }
        Invalidate();
        long request = generation;
        Revision = snapshot.Revision; SequenceId = sequenceId;
        durationTicks = sequence.DurationTicks;
        requestedTicks = Math.Clamp(startTicks, 0, durationTicks);
        if (autoPlay && requestedTicks == durationTicks) requestedTicks = 0;
        var cancellation = new CancellationTokenSource(); preparing = cancellation;
        string path = Path.Combine(Path.GetTempPath(), "kachinco-preview-" + Guid.NewGuid().ToString("N") + ".mp4");
        bool retained = false;
        SetState(PreviewState.Preparing);
        var progress = new Progress<ExportProgress>(p =>
        {
            if (request != generation || !IsPreparing) return;
            Progress = p;
            if (p.Stage is ExportStage.Rendering or ExportStage.Encoding) SetState(PreviewState.Rendering);
        });
        try
        {
            var result = await render(snapshot, sequenceId, path, progress, cancellation.Token);
            if (request != generation || cancellation.IsCancellationRequested) return;
            if (result.Stage == ExportStage.Cancelled) { Invalidate(); return; }
            if (result.Stage != ExportStage.Completed)
            {
                Fail(string.Join(" / ", result.Diagnostics.Select(d => $"[{d.Code}] {d.Message}")));
                return;
            }
            var player = playerFactory(); Player = player;
            previewFile = path; retained = true; Progress = null;
            SetState(PreviewState.Preparing);
            player.Opened += (_, _) =>
            {
                if (request != generation || Player != player || !IsPreparing) return;
                opened = true;
                try
                {
                    Seek(requestedTicks);
                    if (autoPlay) { player.Play(); SetState(PreviewState.Playing); }
                    else SetState(PreviewState.Paused);
                }
                catch (Exception e) { Fail(e.Message); }
            };
            player.Ended += (_, _) =>
            {
                if (request != generation || Player != player) return;
                player.Pause(); player.Position = TimeSpan.Zero;
                SetState(PreviewState.Stopped);
            };
            player.Failed += (_, e) => { if (request == generation && Player == player) Fail(e.Message); };
            player.Open(path);
            // Native MediaOpened/MediaFailed must eventually arrive; a silent decoder is a failure.
            await Task.Delay(TimeSpan.FromSeconds(20), cancellation.Token);
            if (request == generation && !opened) Fail("プレビューを開けませんでした（応答待ち時間超過）。 / Preview decoder timed out.");
        }
        catch (OperationCanceledException) { if (request == generation) Invalidate(); }
        catch (Exception e) { if (request == generation) Fail(e.Message); }
        finally
        {
            if (!retained) DeletePreview(path);
            if (ReferenceEquals(preparing, cancellation)) preparing = null;
            cancellation.Dispose();
        }
    }

    public void Toggle()
    {
        if (!opened || Player is null) return;
        try
        {
            if (State == PreviewState.Playing) { Player.Pause(); SetState(PreviewState.Paused); }
            else if (State is PreviewState.Paused or PreviewState.Stopped)
            {
                if (ReadPositionTicks() >= durationTicks) Seek(0);
                Player.Play(); SetState(PreviewState.Playing);
            }
        }
        catch (Exception e) { Fail(e.Message); }
    }

    public void Pause()
    {
        if (!opened || Player is null) return;
        Player.Pause(); SetState(PreviewState.Paused);
    }
    public void Stop()
    {
        if (IsPreparing) { Invalidate(); return; }
        if (Player is not null) { Player.Pause(); Player.Position = TimeSpan.Zero; }
        requestedTicks = 0; SetState(PreviewState.Stopped);
    }
    public void Seek(long ticks)
    {
        requestedTicks = Math.Clamp(ticks, 0, durationTicks);
        if (opened && Player is not null)
            Player.Position = TimeSpan.FromTicks(TimelineTime.RoundHalfUp((System.Numerics.BigInteger)requestedTicks * TimeSpan.TicksPerSecond, TimelineTime.TicksPerSecond));
    }
    public long ReadPositionTicks() => opened && Player is not null ?
        Math.Clamp(TimelineTime.SecondsToTicks((decimal)Math.Max(0, Player.Position.Ticks) / TimeSpan.TicksPerSecond), 0, durationTicks) : requestedTicks;

    public void Invalidate()
    {
        generation++; preparing?.Cancel(); preparing = null;
        opened = false; Player?.Dispose(); Player = null;
        if (previewFile is not null) DeletePreview(previewFile);
        previewFile = null; Revision = -1; SequenceId = null; requestedTicks = 0;
        Progress = null; Error = null; SetState(PreviewState.Stopped);
    }
    private void Fail(string error)
    {
        Invalidate(); Error = string.IsNullOrWhiteSpace(error) ? "Preview failed." : error;
        SetState(PreviewState.Failed);
    }
    private void SetState(PreviewState state) { State = state; Changed?.Invoke(this, EventArgs.Empty); }
    private static void DeletePreview(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose() { if (disposed) return; disposed = true; Invalidate(); }
}
