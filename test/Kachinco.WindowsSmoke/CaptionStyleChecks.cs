using System.Collections.Immutable;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kachinco.App;
using Kachinco.Core;
using Kachinco.Infrastructure;

internal static class CaptionStyleChecks
{
    public static async Task Run(string? screenshot = null)
    {
        var raster = new WindowsCaptionRasterizer(Application.Current.Dispatcher);
        var id = Guid.NewGuid(); var track = Guid.NewGuid();
        foreach(var (width,height) in new[] {(1920,1080),(1080,1920),(960,540),(540,960)})
        {
            var caption = new EvaluatedCaption(track,id,"存在薄明 English\nBefore the first");
            var legacy = await raster.RasterizeAsync([caption],width,height,default);
            var explicitDefault = await raster.RasterizeAsync([caption with {Style=CaptionStyle.Default}],width,height,default);
            if(!legacy.SequenceEqual(explicitDefault)) throw new Exception("Explicit default changed legacy caption pixels.");
            var style = new CaptionStyle(FontSize:72,Color:"#FFCC00",Y:.1,VerticalAnchor:"Top",ShadowEnabled:true);
            var upper = await raster.RasterizeAsync([caption with {Style=style}],width,height,default);
            if(upper.SequenceEqual(legacy) || !upper.Where((v,i)=>i%4==3 && i/4/width<height/2).Any(v=>v>0)) throw new Exception("Styled caption position/visible pixels failed.");
            var missing = await raster.RasterizeAsync([caption with {Style=style with {FontFamily="FLAMORIS missing font 95"}}],width,height,default);
            if(!upper.SequenceEqual(missing)) throw new Exception("Missing family fallback differs from Yu Gothic.");
            var longText=string.Join(" ",Enumerable.Repeat("日本語 English",20));
            var wrapped=await raster.RasterizeAsync([caption with {Text=longText,Style=style with {MaxWidth=.5,FontSize=24}}],width,height,default);
            if(!wrapped.Where((v,i)=>i%4==3).Any(v=>v>0)) throw new Exception("Wrapped caption is empty.");
        }
        using(var decoder=new FfmpegMediaDecoder())
        using(var export=new ExportFrameRenderer(decoder,captions:raster,preference:PreviewBackendPreference.Cpu))
        {
            var project=new Project(Guid.NewGuid(),"Caption frame parity",[],[]);
            var frame=new EvaluatedFrame(Guid.NewGuid(),0,SequenceSettings.Landscape,[],[],[new(track,id,"日本語 English\n白文字＋黒縁") {Style=new(FontSize:72,ShadowEnabled:true)}]);
            var preview=await new SharedFrameRenderer(decoder,captions:raster).RenderPreviewAsync(project,frame,PreviewQuality.Full,default);
            var rendered=await export.RenderAsync(project,frame,0,default);
            if(!preview.Success || !rendered.Success || !preview.Value!.Rgba8.SequenceEqual(rendered.Value!.Rgba8)) throw new Exception("Caption preview/export frame parity failed.");
        }
        using var session = new EditorSession(); var sequence=Guid.NewGuid();var second=Guid.NewGuid();var otherTrack=Guid.NewGuid();var third=Guid.NewGuid();
        var result=session.Execute(new([
            new CreateProject(Guid.NewGuid(),"Caption UI"),new CreateSequence(sequence,"S",SequenceSettings.Landscape,10*TimelineTime.TicksPerSecond),
            new AddTrack(sequence,track,"S1",TrackKind.Subtitle),new AddTrack(sequence,otherTrack,"S2",TrackKind.Subtitle),
            new AddCaption(sequence,track,new(id,0,TimelineTime.TicksPerSecond,"字幕 one")),
            new AddCaption(sequence,track,new(second,TimelineTime.TicksPerSecond,TimelineTime.TicksPerSecond,"字幕 two")),
            new AddCaption(sequence,otherTrack,new(third,0,TimelineTime.TicksPerSecond,"字幕 three")),
            new SetCaptionStyle(sequence,second,new(Color:"#00FF00",X:.25))]));
        if(!result.Success) throw new Exception("Caption UI fixture failed.");
        var editor=new CaptionStyleEditor();
        void Configure(IEnumerable<Guid> ids) => editor.Configure(session,sequence,ids,r=> { if(!r.Success) throw new Exception("Caption UI edit failed.");Configure(ids); });
        Configure([id,second]);
        var fields=(Dictionary<string,Control>)typeof(CaptionStyleEditor).GetField("fields",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(editor)!;
        var revision=session.GetProject().Revision;
        var color=(TextBox)fields["color"];
        color.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,color,null) {RoutedEvent=Keyboard.LostKeyboardFocusEvent});
        if(session.GetProject().Revision!=revision) throw new Exception("Focus-only mixed style edit mutated captions.");
        var size=(TextBox)fields["fontSize"];size.Text="64";
        size.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,size,null) {RoutedEvent=Keyboard.LostKeyboardFocusEvent});
        var captions=session.GetProject().Project!.Sequences[0].Tracks[0].Captions;
        if(captions[0].Style!.FontSize!=64 || captions[1].Style!.Color!="#00FF00" || captions[1].Style!.X!=.25) throw new Exception("UI multi-select patch lost individual properties.");
        Configure([id]);var scope=(ComboBox)editor.Children[1];scope.SelectedIndex=2;size.Text="80";
        size.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,size,null) {RoutedEvent=Keyboard.LostKeyboardFocusEvent});
        if(session.GetProject().Project!.Sequences[0].Tracks.SelectMany(t=>t.Captions).Any(c=>c.Style!.FontSize!=80)) throw new Exception("UI sequence scope missed captions.");
        Configure([second]);
        if(screenshot is not null)
        {
            var host=new Border {Background=new SolidColorBrush(Color.FromRgb(43,45,49)),Padding=new(12),Child=editor,Width=360};
            host.Measure(new Size(360,double.PositiveInfinity));host.Arrange(new Rect(new Point(),host.DesiredSize));host.UpdateLayout();
            var image=new RenderTargetBitmap(360,(int)Math.Ceiling(host.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(host);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=System.IO.File.Create(screenshot);encoder.Save(file);
        }
        Console.WriteLine("Caption raster fallback/scaling/wrap and UI multi-select/scope/partial fields: PASS");
    }
}
