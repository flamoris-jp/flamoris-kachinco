using System.Windows;
using System.Windows.Media;
using Kachinco.Infrastructure;

namespace Kachinco.App;

internal sealed class WindowsPreviewPlayer : IPreviewPlayer
{
    private readonly MediaPlayer player = new();
    public event EventHandler? Opened;
    public event EventHandler? Ended;
    public event EventHandler<Exception>? Failed;
    public ImageSource? Image { get; private set; }
    public WindowsPreviewPlayer()
    {
        player.Volume = 1;
        player.MediaOpened += (_, _) =>
        {
            Image = new DrawingImage(new VideoDrawing { Player = player,
                Rect = new Rect(0, 0, Math.Max(1, player.NaturalVideoWidth), Math.Max(1, player.NaturalVideoHeight)) });
            Opened?.Invoke(this, EventArgs.Empty);
        };
        player.MediaEnded += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
        player.MediaFailed += (_, e) => Failed?.Invoke(this, e.ErrorException);
    }
    public TimeSpan Position { get => player.Position; set => player.Position = value; }
    public void Open(string path) => player.Open(new Uri(path));
    public void Play() => player.Play();
    public void Pause() => player.Pause();
    public void Dispose() { player.Close(); Image = null; }
}
