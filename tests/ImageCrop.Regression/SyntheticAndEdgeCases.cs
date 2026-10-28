using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    // This is the product seam used by OnMouseDown. No input event is constructed or routed.
    static bool DoubleClick(EditorView editor,Point imagePoint,KeyModifiers modifiers=KeyModifiers.None)
        => (bool)Call(editor,"TryBeginImageCropAt",imagePoint,2,modifiers)!;
    static Bitmap PreviewDocument(EditorView editor)
    {
        var layers=typeof(EditorView).GetNestedType("SceneLayers",Any)!;
        return ImageTools.Render(new PixelSize(editor.PixelWidth,editor.PixelHeight),context=>Call(editor,"DrawScene",context,Enum.Parse(layers,"Document"),true));
    }
    static void SyntheticAndEdgeCases()
    {
        using var source=Quadrants();
        Case("image double-click seam enters crop",()=> {
            var editor=Editor(source,null);var handled=DoubleClick(editor,new Point(160,120));Check(Cropping(editor)&&handled,"Image double-click did not start crop");Call(editor,"CancelImageCrop");
        });
        foreach(var modifier in new[]{KeyModifiers.Control,KeyModifiers.Shift})Case(modifier+" image double-click seam declines crop",()=> {
            var editor=Editor(source,null);var handled=DoubleClick(editor,new Point(160,120),modifier);Check(!handled&&!Cropping(editor),"Modified double-click entered crop");Call(editor,"CancelGesture");
        });
        Case("blank collage double-click seam declines crop",()=> {
            var editor=Editor(source,null);var handled=DoubleClick(editor,new Point(10,10));Check(!handled&&!Cropping(editor),"Blank double-click entered crop");Call(editor,"CancelGesture");
        });
        Case("text double-click seam leaves existing text handling intact",()=> {
            var text=Activator.CreateInstance(ItemType,true)!;Set(text,"Tool",AnnotationTool.Text);Set(text,"Start",new Point(80,60));Set(text,"End",new Point(80,60));Set(text,"Color",Colors.Black);Set(text,"Width",3.0);Set(text,"Text","Existing text annotation");Set(text,"FontSize",26.0);Set(text,"TextWidth",220.0);
            var editor=new EditorView();var board=ImageTools.Solid(320,240,Colors.White);var items=Array.CreateInstance(ItemType,1);items.SetValue(text,0);Call(editor,"RestoreSession",board,items,false,true);editor.SetTool(AnnotationTool.Select);
            var handled=DoubleClick(editor,new Point(110,70));Check(!handled&&!Cropping(editor),"Image crop seam intercepted text double-click");
        });
        Case("crop key routing blocks duplicate select-all and delete without OS input",()=> {
            var editor=Editor(source,null);DraftBottomRightQuarter(editor);var actual=First(editor);var crop=Crop(Draft(editor));
            foreach(var key in new[]{new KeyEventArgs{Key=Key.D,KeyModifiers=KeyModifiers.Control},new KeyEventArgs{Key=Key.A,KeyModifiers=KeyModifiers.Control},new KeyEventArgs{Key=Key.Delete},new KeyEventArgs{Key=Key.Back}})Call(editor,"OnKeyDown",editor,key);
            Check(Cropping(editor)&&Items(editor).Count==1&&ReferenceEquals(First(editor),actual),"Crop key shortcuts mutated committed annotation list");EqualRect(Crop(Draft(editor)),crop,"Blocked key changed crop");Call(editor,"CancelImageCrop");
        });
        Case("crop preview and export preserve transparent image and upper annotation layer",()=> {
            var pixels=new PixelBuffer(80,60);
            for(int y=0;y<60;y++)for(int x=0;x<80;x++) {
                var i=(y*80+x)*4;var color=y<30?(x<40?Color.FromArgb(128,255,0,0):Colors.Transparent):(x<40?Colors.Blue:Color.FromArgb(64,255,255,0));var alpha=color.A/255.0;
                pixels.Pixels[i]=(byte)Math.Round(color.B*alpha);pixels.Pixels[i+1]=(byte)Math.Round(color.G*alpha);pixels.Pixels[i+2]=(byte)Math.Round(color.R*alpha);pixels.Pixels[i+3]=color.A;
            }
            using var transparent=pixels.ToBitmap();var original=PixelHash(transparent);var editor=Editor(transparent,null);
            var upper=Activator.CreateInstance(ItemType,true)!;Set(upper,"Tool",AnnotationTool.Rectangle);Set(upper,"Start",new Point(100,80));Set(upper,"End",new Point(220,160));Set(upper,"Color",Colors.Magenta);Set(upper,"Width",6.0);Items(editor).Add(upper);
            Begin(editor);Gesture(editor,"Right",new Point(240,120),new Point(200,120));Gesture(editor,"Move",default,new Point(-40,0));
            using var preview=PreviewDocument(editor);var previewHash=PixelHash(preview);Check(Cropping(editor),"Preview changed crop mode");using var export=editor.ExportImage();Check(!Cropping(editor)&&PixelHash(export)==previewHash,"Crop preview/export alpha or layer-order mismatch");
            Check(PixelHash(transparent)==original,"Transparent original bitmap was modified");Save("transparent-preview-document.png",preview);Save("transparent-export.png",export);
        });
        Case("one-source-pixel crop stays valid at extreme frame drag boundaries",()=> {
            var editor=Editor(source,new Rect(.5,0,1.0/80,1),new Rect(80,60,2,120));Begin(editor);
            foreach(var handle in new[]{"Left","Right","TopLeft","BottomRight"}) {
                Gesture(editor,handle,new Point(80,60),new Point(-100000,-100000));Gesture(editor,handle,new Point(80,60),new Point(100000,100000));var draft=Draft(editor);
                Check((bool)Static(CropGeometryType!,"IsValid",Crop(draft)??new Rect(0,0,1,1))!&&Bounds(draft).Width>0&&Bounds(draft).Height>0,"Narrow crop lost positive source/frame bounds");
            }
            Call(editor,"CommitImageCrop");using var output=editor.ExportImage();Check(output.PixelSize==new PixelSize(320,240),"Narrow crop damaged canvas");Save("narrow-crop.png",output);
        });
        foreach(var frame in new[]{new Rect(0,0,0,50),new Rect(400,300,80,60),new Rect(double.NaN,0,80,60)})Case("crop rejects invalid or fully off-canvas frame "+frame,()=> {
            var editor=Editor(source,null,frame);Check(!(bool)Call(editor,"BeginImageCrop")!,"Invalid frame entered crop");Check(!Cropping(editor),"Invalid frame left crop state");
        });
        Case("partially off-canvas crop clips draft frame and remaps source non-destructively",()=> {
            var editor=Editor(source,null,new Rect(-40,60,160,120));var original=PixelHash(source);Begin(editor);var draft=Draft(editor);
            EqualRect(Bounds(draft),new Rect(0,60,120,120),"Off-canvas frame was not clipped to board");EqualRect(Crop(draft),new Rect(.25,0,.75,1),"Off-canvas source remapping");Call(editor,"CommitImageCrop");Check(PixelHash(source)==original,"Off-canvas crop modified source");editor.Undo();EqualRect(Bounds(First(editor)),new Rect(-40,60,160,120),"Off-canvas undo frame mismatch");
        });
    }
}





