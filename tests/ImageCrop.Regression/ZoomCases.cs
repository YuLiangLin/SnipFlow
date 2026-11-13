using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static Rect Viewport(EditorView editor)=>Crop(Draft(editor))??new Rect(0,0,1,1);
    static double ZoomValue(EditorView editor)=>(double)Get(editor,"ImageCropZoom")!;
    static int UndoDepth(EditorView editor)=>((System.Collections.ICollection)typeof(EditorView).GetField("_undo",Any)!.GetValue(editor)!).Count;
    static void Zoom(EditorView editor,double percentage)=>Call(editor,"SetImageCropZoom",percentage);
    static Point Center(Rect rect)=>new(rect.X+rect.Width/2,rect.Y+rect.Height/2);
    static void EqualPoint(Point a,Point b,string message)=>Check(Math.Abs(a.X-b.X)<1e-9&&Math.Abs(a.Y-b.Y)<1e-9,message);
    static void ZoomCases()
    {
        using var source=Quadrants();var sourceHash=PixelHash(source);
        foreach(var percentage in new[]{1.0,10.0,25.0,50.0,100.0,200.0,1600.0})Case("zoom "+percentage+" percent preserves frame and source center",()=> {
            var editor=Editor(source,null);var frame=Bounds(First(editor));Begin(editor);Zoom(editor,percentage);var viewport=Viewport(editor);
            EqualRect(Bounds(Draft(editor)),frame,"Zoom resized crop frame");EqualPoint(Center(viewport),new Point(.5,.5),"Zoom moved source center");Check(Math.Abs(ZoomValue(editor)-percentage)<1e-7,"Zoom getter does not report requested value");
            Check(Math.Abs(Math.Max(viewport.Width,viewport.Height)-100/percentage)<1e-9,"Zoom viewport scale");Check(Crop(First(editor)) is null&&UndoDepth(editor)==0,"Zoom added committed history before completion");if(percentage!=100)Check(editor.CanUndo,"Changed draft should allow Ctrl+Z cancellation");Check(PixelHash(source)==sourceHash,"Zoom modified original source");Call(editor,"CancelImageCrop");
        });
        Case("zoom preserves an existing off-center viewport center and aspect",()=> {
            var original=new Rect(.2,.1,.4,.6);var editor=Editor(source,original);Begin(editor);Zoom(editor,80);var viewport=Viewport(editor);
            EqualPoint(Center(viewport),Center(original),"Off-center zoom moved content focal point");Check(Math.Abs(viewport.Width/viewport.Height-original.Width/original.Height)<1e-9,"Zoom distorted original viewport aspect");EqualRect(Bounds(Draft(editor)),new Rect(80,60,160,120),"Off-center zoom changed frame");Call(editor,"CancelImageCrop");
        });
        Case("zoom 200 percent magnifies real source pixels inside fixed frame",()=> {
            var pixels=new PixelBuffer(80,60);for(int y=0;y<60;y++)for(int x=0;x<80;x++) {
                var c=x<20?Colors.Red:x<40?Colors.Lime:x<60?Colors.Blue:Colors.Yellow;var i=(y*80+x)*4;pixels.Pixels[i]=c.B;pixels.Pixels[i+1]=c.G;pixels.Pixels[i+2]=c.R;pixels.Pixels[i+3]=255;
            }
            using var striped=pixels.ToBitmap();var editor=Editor(striped,null);Begin(editor);Zoom(editor,200);EqualRect(Viewport(editor),new Rect(.25,.25,.5,.5),"Magnified viewport");using var image=RenderItem(Draft(editor),320,240);var p=Pixels(image);
            Check(At(p,90,100)==Colors.Lime&&At(p,230,100)==Colors.Blue,"Zoom did not magnify the center source bands");Check(At(p,90,100)!=Colors.Red&&At(p,230,100)!=Colors.Yellow,"Zoom still shows uncropped outer bands");Save("zoom-200-magnified-center.png",image);Call(editor,"CancelImageCrop");
        });
        Case("zoom 50 percent contains original in center with transparent margins",()=> {
            var editor=Editor(source,null);Begin(editor);Zoom(editor,50);EqualRect(Viewport(editor),new Rect(-.5,-.5,2,2),"Shrunken viewport");using var draft=RenderItem(Draft(editor),320,240);var p=Pixels(draft);
            Check(At(p,90,70).A==0&&At(p,230,170).A==0,"Shrinking stretched the original to the frame or filled margins");Check(At(p,130,100)==Colors.Red&&At(p,190,100)==Colors.Lime&&At(p,130,140)==Colors.Blue&&At(p,190,140)==Colors.Yellow,"Shrunken source is not centered/complete");Save("zoom-50-transparent-margins.png",draft);
            using var preview=PreviewDocument(editor);using var export=editor.ExportImage();Check(PixelHash(preview)==PixelHash(export),"Blank-margin preview/export differs");Save("zoom-50-canvas-export.png",export);
        });
        Case("pan can expose blank margins without changing zoom or frame",()=> {
            var editor=Editor(source,null);var frame=Bounds(First(editor));Begin(editor);Zoom(editor,50);Gesture(editor,"Move",default,new Point(40,30));var viewport=Viewport(editor);
            EqualRect(viewport,new Rect(-1,-1,2,2),"Pan source offset");EqualRect(Bounds(Draft(editor)),frame,"Pan resized frame");Check(Math.Abs(ZoomValue(editor)-50)<1e-9,"Pan changed zoom");using var image=RenderItem(Draft(editor),320,240);Check(At(Pixels(image),100,80).A==0,"Panned blank margin disappeared");Save("zoom-pan-blank-margin.png",image);Call(editor,"CancelImageCrop");
        });
        Case("reset contains full original at true aspect in the unchanged square frame",()=> {
            var frame=new Rect(80,50,160,160);var editor=Editor(source,new Rect(.5,.5,.25,.25),frame);Begin(editor);Zoom(editor,400);Gesture(editor,"Move",default,new Point(10,20));Call(editor,"ResetImageCrop");EqualRect(Bounds(Draft(editor)),frame,"Reset resized/repositioned the frame");
            EqualRect(Viewport(editor),new Rect(0,-1.0/6,1,4.0/3),"Reset did not contain original source aspect");Check(Math.Abs(ZoomValue(editor)-75)<1e-8,"Contain reset zoom should be 75 percent for 4:3 source in square frame");
            using var image=RenderItem(Draft(editor),320,240);var p=Pixels(image);Check(At(p,90,55).A==0&&At(p,90,205).A==0,"Reset should preserve transparent letterbox margins");Check(At(p,100,85)==Colors.Red&&At(p,220,85)==Colors.Lime&&At(p,100,175)==Colors.Blue&&At(p,220,175)==Colors.Yellow,"Reset lost original aspect/content");Save("zoom-reset-square-contain.png",image);Call(editor,"CancelImageCrop");
        });
        Case("zoom 100 percent covers frame after contain reset without stretching source",()=> {
            var frame=new Rect(80,50,160,160);var editor=Editor(source,null,frame);Begin(editor);Call(editor,"ResetImageCrop");Zoom(editor,100);EqualRect(Bounds(Draft(editor)),frame,"Cover zoom changed frame");EqualRect(Viewport(editor),new Rect(.125,0,.75,1),"100-percent cover viewport");
            var draw=(ValueTuple<Rect,Rect>)Static(CropGeometryType!,"GetDrawRects",source,Viewport(editor),frame)!;EqualRect(draw.Item2,frame,"100-percent cover left blank margins");Call(editor,"CancelImageCrop");
        });
        Case("zoom changes can cancel without undo history or source mutation",()=> {
            var editor=Editor(source,null);var before=PixelHash(editor.ExportImage());Begin(editor);Zoom(editor,25);Gesture(editor,"Move",default,new Point(-30,10));Zoom(editor,150);Call(editor,"CancelImageCrop");Check(!editor.HasEdits&&!editor.CanUndo&&!Cropping(editor),"Zoom cancellation mutated history");Check(PixelHash(editor.ExportImage())==before&&PixelHash(source)==sourceHash,"Zoom cancellation changed pixels");
        });
        Case("zoom pan and reset complete as one reversible mutation",()=> {
            var editor=Editor(source,null);var before=PixelHash(editor.ExportImage());var frame=Bounds(First(editor));Begin(editor);Zoom(editor,50);Gesture(editor,"Move",default,new Point(-10,15));Zoom(editor,200);var afterViewport=Viewport(editor);Call(editor,"CommitImageCrop");EqualRect(Bounds(First(editor)),frame,"Commit changed frame");using var after=editor.ExportImage();var hash=PixelHash(after);
            editor.Undo();Check(!editor.CanUndo&&!editor.HasEdits,"Zoom gesture sequence should be one undo");Check(PixelHash(editor.ExportImage())==before,"Zoom undo pixels mismatch");editor.Redo();EqualRect(Crop(First(editor)),afterViewport,"Zoom redo viewport mismatch");Check(PixelHash(editor.ExportImage())==hash,"Zoom redo pixels mismatch");
        });
        Case("zoomed blank-margin project save reopens viewport frame and complete source",()=> {
            var editor=Editor(source,null);var frame=Bounds(First(editor));Begin(editor);Zoom(editor,50);Gesture(editor,"Move",default,new Point(-10,15));var viewport=Viewport(editor);var path=Path.Combine(Output,"zoom-save-reopen.snipflow");Static(StoreType,"SaveProject",editor,path);Check(!Cropping(editor),"Project save did not commit crop draft");
            var expected=PixelHash(editor.ExportImage());var restored=new EditorView();Static(StoreType,"RestoreProject",restored,path);EqualRect(Crop(First(restored)),viewport,"Reopened zoom viewport mismatch");EqualRect(Bounds(First(restored)),frame,"Reopened zoom frame mismatch");Check(PixelHash(restored.ExportImage())==expected&&PixelHash((Bitmap)Get(First(restored),"Image")!)==sourceHash,"Reopened zoom lost output/source pixels");restored.SelectAll();Begin(restored);Check(Math.Abs(ZoomValue(restored)-50)<1e-9,"Reopened zoom getter mismatch");Call(restored,"CancelImageCrop");
        });
        foreach(var value in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity,-1.0,0.0})Case("invalid zoom "+value+" does not change draft or history",()=> {
            var editor=Editor(source,new Rect(-.5,-.5,2,2));Begin(editor);var before=Viewport(editor);var frame=Bounds(Draft(editor));var revision=editor.Revision;
            try{Zoom(editor,value);}catch(ArgumentException){}
            EqualRect(Viewport(editor),before,"Invalid zoom changed viewport");EqualRect(Bounds(Draft(editor)),frame,"Invalid zoom changed frame");Check(Cropping(editor)&&editor.Revision==revision&&!editor.CanUndo,"Invalid zoom changed mode/history");Call(editor,"CancelImageCrop");
        });
        foreach(var value in new[]{double.Epsilon,.000001,double.MaxValue,100000000.0})Case("finite zoom extreme "+value+" keeps finite valid document geometry",()=> {
            var editor=Editor(source,null);Begin(editor);var frame=Bounds(Draft(editor));try{Zoom(editor,value);}catch(ArgumentException){}
            Check((bool)Static(CropGeometryType!,"IsValid",Viewport(editor))!,"Extreme zoom created invalid viewport");Check(double.IsFinite(ZoomValue(editor))&&ZoomValue(editor)>0,"Extreme zoom getter is nonfinite/zero");EqualRect(Bounds(Draft(editor)),frame,"Extreme zoom moved/resized frame");using var image=RenderItem(Draft(editor),320,240);Check(image.PixelSize==new PixelSize(320,240),"Extreme zoom broke rendering");Call(editor,"CancelImageCrop");
        });
        Case("existing extreme zoom remains accessible beyond default slider range",()=> {
            foreach(var viewport in new[]{new Rect(.5,.5,.00001,.00001),new Rect(-9999.5,-9999.5,20000,20000)}) {
                var editor=Editor(source,viewport);Begin(editor);var zoom=100/Math.Max(viewport.Width,viewport.Height);Check(Math.Abs(ZoomValue(editor)-zoom)<Math.Max(1e-9,zoom*1e-12),"Existing viewport zoom unavailable");Zoom(editor,zoom);EqualRect(Viewport(editor),viewport,"Current extreme zoom was clamped to default UI range");Call(editor,"CancelImageCrop");
            }
        });
        Case("legacy 3200 percent range survives 2000 percent and returns to 3200",()=> {
            var initial=new Rect(.484375,.484375,.03125,.03125);var editor=Editor(source,initial);Begin(editor);Check(Math.Abs(ZoomValue(editor)-3200)<1e-8,"Legacy 3200 zoom unavailable");Zoom(editor,2000);Check(Math.Abs(ZoomValue(editor)-2000)<1e-8,"Intermediate 2000 zoom unavailable");Zoom(editor,3200);Check(Math.Abs(ZoomValue(editor)-3200)<1e-8,"Session range shrank after intermediate zoom");EqualRect(Viewport(editor),initial,"Returning legacy scale changed source center/viewport");Call(editor,"CancelImageCrop");
        });
        Case("legacy below-one-percent range survives larger zoom and returns to original",()=> {
            var initial=new Rect(-99.5,-99.5,200,200);var editor=Editor(source,initial);Begin(editor);Check(Math.Abs(ZoomValue(editor)-.5)<1e-9,"Legacy 0.5 percent unavailable");Zoom(editor,50);Zoom(editor,.5);Check(Math.Abs(ZoomValue(editor)-.5)<1e-9,"Session minimum rose after zooming in");EqualRect(Viewport(editor),initial,"Returning legacy shrink scale changed viewport");Call(editor,"CancelImageCrop");
        });
        Case("contain reset below one percent synchronizes slider and retains session minimum",()=> {
            var frame=new Rect(0,60,320,2);var editor=Editor(source,null,frame);Begin(editor);Call(editor,"ResetImageCrop");var viewport=Viewport(editor);var percent=ZoomValue(editor);Check(percent>0&&percent<1,"Fixture reset should contain original below one percent");EqualRect(Bounds(Draft(editor)),frame,"Thin-frame contain reset changed frame");
            var slider=(Avalonia.Controls.Slider)typeof(EditorView).GetField("_cropScaleSlider",Any)!.GetValue(editor)!;Check(Math.Abs(100*Math.Pow(2,slider.Value)-percent)<1e-8,"Slider clamped/desynchronized contain scale");Zoom(editor,100);Zoom(editor,percent);EqualRect(Viewport(editor),viewport,"Reset-created low range was lost during session");Call(editor,"CancelImageCrop");
        });
        Case("same-scale zoom is an exact no-op for saved decimal viewport",()=> {
            var initial=new Rect(.123456789,.234567891,.23456789,.456789123);var editor=Editor(source,initial);Begin(editor);var revision=editor.Revision;var frame=Bounds(Draft(editor));Zoom(editor,ZoomValue(editor));
            Check(Crop(Draft(editor))==initial,"Same-scale request introduced floating-point crop mutation");Check(!editor.HasEdits&&!editor.CanUndo&&UndoDepth(editor)==0&&editor.Revision==revision,"Same-scale request dirtied saved document/history");EqualRect(Bounds(Draft(editor)),frame,"Same-scale request changed frame");Call(editor,"CommitImageCrop");Check(!editor.HasEdits&&!editor.CanUndo,"Same-scale completion produced undo entry");
        });
        Case("expanded session range resets when completed crop returns to full source",()=> {
            var editor=Editor(source,new Rect(.484375,.484375,.03125,.03125));Begin(editor);Zoom(editor,2000);Call(editor,"ResetImageCrop");Call(editor,"CommitImageCrop");Begin(editor);Zoom(editor,3200);Check(Math.Abs(ZoomValue(editor)-1600)<1e-8,"Closed crop session leaked its legacy maximum into next full-image session");Call(editor,"CancelImageCrop");
        });
        Case("zoom slider callback changes draft scale without changing frame or committing history",()=> {
            var editor=Editor(source,null);Begin(editor);var frame=Bounds(Draft(editor));var slider=(Avalonia.Controls.Slider)typeof(EditorView).GetField("_cropScaleSlider",Any)!.GetValue(editor)!;
            var value=slider.Value;slider.Value=value+1;Check(Math.Abs(ZoomValue(editor)-200)<1e-8,"Slider callback did not change image scale");EqualRect(Bounds(Draft(editor)),frame,"Slider moved/resized frame");Check(UndoDepth(editor)==0&&editor.CanUndo,"Slider committed draft or lost cancel-undo state");
            Zoom(editor,50);EqualRect(Viewport(editor),new Rect(-.5,-.5,2,2),"Programmatic zoom synchronization changed viewport");Check(double.IsFinite(slider.Value),"Slider synchronization produced nonfinite value");Call(editor,"CancelImageCrop");Check(!editor.HasEdits&&!editor.CanUndo,"Slider cancel changed document/history");
        });
        Case("Ctrl wheel calls image zoom while preserving crop frame and source center",()=> {
            var editor=Editor(source,null);Begin(editor);var frame=Bounds(Draft(editor));var center=Center(Viewport(editor));using var pointer=new Avalonia.Input.Pointer(778,Avalonia.Input.PointerType.Mouse,true);
            var input=new Avalonia.Input.PointerWheelEventArgs(editor,pointer,editor,default,0,new Avalonia.Input.PointerPointProperties(),Avalonia.Input.KeyModifiers.Control,new Vector(0,1));Call(editor,"OnMouseWheel",editor,input);
            Check(input.Handled&&Math.Abs(ZoomValue(editor)-112)<1e-8,"Ctrl wheel did not use crop zoom");EqualRect(Bounds(Draft(editor)),frame,"Ctrl wheel changed frame");EqualPoint(Center(Viewport(editor)),center,"Ctrl wheel moved source focal point");Check(UndoDepth(editor)==0,"Ctrl wheel committed history");Call(editor,"CancelImageCrop");
        });
        Case("zoom slider navigation keys remain owned by slider",()=> {
            var editor=Editor(source,null);Begin(editor);Zoom(editor,50);var viewport=Viewport(editor);var slider=typeof(EditorView).GetField("_cropScaleSlider",Any)!.GetValue(editor);Check(slider is not null,"Zoom slider missing");
            foreach(var key in new[]{Avalonia.Input.Key.Left,Avalonia.Input.Key.Right,Avalonia.Input.Key.Up,Avalonia.Input.Key.Down,Avalonia.Input.Key.Home,Avalonia.Input.Key.End,Avalonia.Input.Key.PageUp,Avalonia.Input.Key.PageDown}) {
                var input=new Avalonia.Input.KeyEventArgs{Key=key};Set(input,"Source",slider);Check(!(bool)Call(editor,"HandleImageCropKey",input)!&&!input.Handled,"Crop intercepted slider navigation "+key);EqualRect(Viewport(editor),viewport,"Slider-owned key panned image");
            }
            Call(editor,"CancelImageCrop");
        });
        foreach(var command in new[]{"RotateLeft","RotateRight","FlipHorizontal","FlipVertical"})foreach(var entireCanvas in new[]{false,true})Case("blank viewport "+command+(entireCanvas?" canvas":" selection")+" preserves transparency and content",()=>BlankTransform(source,command,entireCanvas));
        foreach(var layout in new[]{"Grid","Vertical","Horizontal"})Case("blank viewport "+layout+" arrangement preserves frame ratio and margins",()=> {
            var viewport=new Rect(-.5,-.25,2,1.5);var editor=Editor(source,viewport,new Rect(70,50,120,160));var type=Product.GetType("SnipFlow.Editor.CollageLayout",true)!;Call(editor,"ArrangeImages",Enum.Parse(type,layout));var frame=Bounds(First(editor));Check(Math.Abs(frame.Width/frame.Height-.75)<1e-9,"Arrangement changed fixed frame aspect");EqualRect(Crop(First(editor)),viewport,"Arrangement changed blank viewport");
        });
    }
    static void BlankTransform(Bitmap source,string command,bool entireCanvas)
    {
        var viewport=new Rect(-.5,-.5,2,2);var editor=Editor(source,viewport);var before=PixelHash(editor.ExportImage());var type=Product.GetType("SnipFlow.Editor.ImageTransformCommand",true)!;Call(editor,"ApplyImageTransform",Enum.Parse(type,command),entireCanvas);var item=First(editor);EqualRect(Crop(item),viewport,"Transform clamped away blank viewport");
        using var raw=RenderItem(item,editor.PixelWidth,editor.PixelHeight);var p=Pixels(raw);var frame=Bounds(item);Color Sample(double x,double y)=>At(p,(int)(frame.Left+frame.Width*x),(int)(frame.Top+frame.Height*y));Check(Sample(.1,.1).A==0&&Sample(.9,.9).A==0,"Transform stretched source across blank margin");
        var expected=command switch {"RotateRight"=>new[]{Colors.Blue,Colors.Red,Colors.Yellow,Colors.Lime},"RotateLeft"=>new[]{Colors.Lime,Colors.Yellow,Colors.Red,Colors.Blue},"FlipHorizontal"=>new[]{Colors.Lime,Colors.Red,Colors.Yellow,Colors.Blue},_=>new[]{Colors.Blue,Colors.Yellow,Colors.Red,Colors.Lime}};
        Check(Sample(.35,.35)==expected[0]&&Sample(.65,.35)==expected[1]&&Sample(.35,.65)==expected[2]&&Sample(.65,.65)==expected[3],"Blank viewport transform changed visible quadrants");var after=PixelHash(editor.ExportImage());editor.Undo();Check(PixelHash(editor.ExportImage())==before,"Blank viewport transform undo mismatch");editor.Redo();Check(PixelHash(editor.ExportImage())==after,"Blank viewport transform redo mismatch");Save("zoom-blank-"+command+(entireCanvas?"-canvas":"-selection")+".png",raw);
    }
}
