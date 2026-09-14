using System.Collections.Immutable;
using System.Windows;
using System.Windows.Threading;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new Application(); int exit = 1;
        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var main = new MainWindow(); main.Show();
                var compiler = new RecipeCompiler();
                var compiled = await compiler.CompileAsync("text(text='グエー', x=10, y=20)\nparticles(count=3,x=30,y=100,size=3)");
                if (!compiled.Success) throw new Exception(string.Join(";",compiled.Diagnostics.Select(d=>d.Message)));
                var clapper = new Clapper(Guid.NewGuid(),"A-1",0,TimelineTime.TicksPerSecond,new(ClapperGeometryKind.Rectangle,0,0,500,500),null,null,"");
                var recipe = new Recipe(Guid.NewGuid(),clapper.Id,"text(text='グエー')",1,42,"1","1");
                var renderer = new WindowsRecipeRasterizer(app.Dispatcher);
                var a = await renderer.RenderAsync(compiled.Value!,recipe,clapper,SequenceSettings.Landscape,TimelineTime.TicksPerSecond/2,default);
                var b = await renderer.RenderAsync(compiled.Value!,recipe,clapper,SequenceSettings.Landscape,TimelineTime.TicksPerSecond/2,default);
                if (!a.SequenceEqual(b) || !a.Where((_,i)=>i%4==3).Any(x=>x>0)) throw new Exception("Recipe RGBA determinism/visible output failed.");
                var caption = await new WindowsCaptionRasterizer(app.Dispatcher).RasterizeAsync([new(Guid.NewGuid(),Guid.NewGuid(),"存在薄明")],1920,1080,default);
                if (!caption.Where((_,i)=>i%4==3).Any(x=>x>0)) throw new Exception("Caption rasterization is empty.");
                main.Close(); exit=0; Console.WriteLine("Windows Recipe worker limits, repeatable RGBA, captions and shell: PASS");
            }
            catch(Exception e) { Console.Error.WriteLine(e); }
            finally { app.Shutdown(); }
        });
        app.Run(); return exit;
    }
}
