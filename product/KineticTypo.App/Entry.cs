using System.Windows;
namespace KineticTypo.App;
public static class Entry
{
    [STAThread] public static void Main(string[] args)
    {
        var app = new Application();
        try { app.Run(new EditorWindow(args)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "KineticTypo", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
