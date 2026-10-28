using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SnipFlow.Editor;
using SnipFlow.Imaging;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static void ModelCases()
    {
        using var source = Quadrants();
        var sourceHash = PixelHash(source);
        Case("model crop renders exact source quadrant without modifying original", () => {
            var item = ImageItem(source,new Rect(0,0,120,90),new Rect(.5,0,.5,.5));
            using var image = RenderItem(item,120,90); var p = Pixels(image);
            for(int y=4;y<86;y++) for(int x=4;x<116;x++) Check(At(p,x,y)==Colors.Lime,"Cropped quadrant pixel is not green.");
            Check(ReferenceEquals(Get(item,"Image"),source) && source.PixelSize==new PixelSize(80,60) && PixelHash(source)==sourceHash,"Crop replaced or modified source.");
            Save("model-crop-top-right.png",image);
        });
        Case("model null crop renders complete four-quadrant image", () => {
            using var image=RenderItem(ImageItem(source,new Rect(0,0,160,120),null),160,120);var p=Pixels(image);
            Check(At(p,20,20)==Colors.Red && At(p,140,20)==Colors.Lime && At(p,20,100)==Colors.Blue && At(p,140,100)==Colors.Yellow,"Full image quadrants incorrect.");
            Save("model-null-crop.png",image);
        });
        Case("model clone copy and equality include crop and preserve source reference", () => {
            var crop=new Rect(.1,.2,.6,.5);var item=ImageItem(source,new Rect(0,0,80,60),crop);var clone=Call(item,"Clone")!;
            EqualRect(Crop(clone),crop,"Clone lost crop");Check(ReferenceEquals(Get(clone,"Image"),source),"Clone changed source reference");Check((bool)Call(item,"ContentEquals",clone)!,"Equal clone comparison failed");
            Set(clone,"ImageCrop",new Rect(.2,.2,.5,.5));Check(!(bool)Call(item,"ContentEquals",clone)!,"Crop-only mutation ignored by equality");Call(item,"CopyFrom",clone);EqualRect(Crop(item),Crop(clone),"CopyFrom lost crop");Check((bool)Call(item,"ContentEquals",clone)!,"CopyFrom content mismatch");
            Set(clone,"ImageCrop",null);Check(!(bool)Call(item,"ContentEquals",clone)!,"Null/full crop difference ignored");
        });
        Case("geometry maps normalized crop to physical source pixels at 192 DPI", () => {
            using var highDpi=new WriteableBitmap(new PixelSize(80,60),new Vector(192,192),PixelFormat.Bgra8888,AlphaFormat.Premul);
            using(var locked=highDpi.Lock()){var input=Pixels(source);for(int y=0;y<60;y++)Marshal.Copy(input.Pixels,y*input.Stride,locked.Address+y*locked.RowBytes,input.Stride);}
            var crop=new Rect(.5,0,.5,.5);var actual=(Rect)Static(CropGeometryType!,"SourceRect",highDpi,crop)!;
            EqualRect(actual,new Rect(40,0,40,30),"SourceRect did not use physical pixels");
            using var image=RenderItem(ImageItem(highDpi,new Rect(0,0,120,90),crop),120,90);var p=Pixels(image);
            Save("model-crop-192-dpi.png",image);Check(At(p,10,10)==Colors.Lime && At(p,100,70)==Colors.Lime,"High-DPI crop rendered wrong source; samples="+At(p,10,10)+","+At(p,100,70)+" logical="+highDpi.Size+" pixels="+highDpi.PixelSize);            EqualRect((Rect)Static(CropGeometryType!,"SourceRect",highDpi,null)!,new Rect(0,0,80,60),"High-DPI null crop source rect");
            using var full=RenderItem(ImageItem(highDpi,new Rect(0,0,160,120),null),160,120);var all=Pixels(full);
            Check(At(all,20,20)==Colors.Red&&At(all,140,20)==Colors.Lime&&At(all,20,100)==Colors.Blue&&At(all,140,100)==Colors.Yellow,"High-DPI full image cropped away source pixels");Save("model-full-192-dpi.png",full);
        });
        var invalids=new (string,Rect)[]{("negative-x",new Rect(-.01,0,.5,.5)),("negative-y",new Rect(0,-.01,.5,.5)),("zero-width",new Rect(0,0,0,.5)),("zero-height",new Rect(0,0,.5,0)),("right-overflow",new Rect(.8,0,.3,.5)),("bottom-overflow",new Rect(0,.8,.5,.3)),("nan",new Rect(double.NaN,0,.5,.5)),("infinity",new Rect(0,0,double.PositiveInfinity,.5))};
        foreach(var invalid in invalids) Case("geometry rejects "+invalid.Item1,()=> {
            Check(!(bool)Static(CropGeometryType!,"IsValid",invalid.Item2)!,"Invalid normalized rectangle accepted");
            Rejected(()=>Static(CropGeometryType!,"SourceRect",source,invalid.Item2));
            Rejected(()=>Static(CropGeometryType!,"Transform",invalid.Item2,1));
        });
        Case("geometry transforms crop for all eight orthogonal orientations",()=> {
            var input=new Rect(.125,.25,.5,.375);
            var expected=new[]{input,new Rect(.375,.125,.375,.5),new Rect(.375,.375,.5,.375),new Rect(.25,.375,.375,.5),new Rect(.375,.25,.5,.375),new Rect(.375,.375,.375,.5),new Rect(.125,.375,.5,.375),new Rect(.25,.125,.375,.5)};
            for(int orientation=0;orientation<8;orientation++) {
                EqualRect((Rect?)Static(CropGeometryType!,"Transform",input,orientation),expected[orientation],"D4 orientation "+orientation);
                Check(Static(CropGeometryType!,"Transform",null,orientation) is null,"Null full image crop gained a crop");
            }
        });
        foreach(var command in new[]{"RotateLeft","RotateRight","FlipHorizontal","FlipVertical"})
        {
            Case("selected image "+command+" keeps cropped content and undo redo",()=>TransformCase(source,command,false));
            Case("whole collage "+command+" keeps cropped content and undo redo",()=>TransformCase(source,command,true));
        }
        Case("four clockwise rotations restore crop source and rendered pixels",()=> {
            var editor=Editor(source,new Rect(0,0,.75,.5));var before=PixelHash(editor.ExportImage());var original=Crop(First(editor));
            var type=Product.GetType("SnipFlow.Editor.ImageTransformCommand",true)!;
            for(int n=0;n<4;n++)Call(editor,"ApplyImageTransform",Enum.Parse(type,"RotateRight"),false);
            EqualRect(Crop(First(editor)),original,"Four rotations changed crop");Check(PixelHash(editor.ExportImage())==before,"Four rotations changed rendered content");Check(PixelHash((Bitmap)Get(First(editor),"Image")!)==sourceHash,"Four rotations changed source pixels");
        });
    }

    static void TransformCase(Bitmap source,string command,bool entireCanvas)
    {
        var editor=Editor(source,new Rect(0,0,.75,.5));var original=PixelHash(editor.ExportImage());var originalCrop=Crop(First(editor));
        var type=Product.GetType("SnipFlow.Editor.ImageTransformCommand",true)!;Call(editor,"ApplyImageTransform",Enum.Parse(type,command),entireCanvas);
        var afterItem=First(editor);var bounds=Bounds(afterItem);using var rendered=editor.ExportImage();var p=Pixels(rendered);
        // The selected crop contains red and green, never the hidden blue/yellow half. Sample the interior.
        int red=0,green=0;
        for(int y=(int)bounds.Top+4;y<(int)bounds.Bottom-4;y++)for(int x=(int)bounds.Left+4;x<(int)bounds.Right-4;x++) {
            var c=At(p,x,y);if(c==Colors.Red)red++;else if(c==Colors.Lime)green++;
            Check(c.B==0 && c.R+c.G<=260,"Rotation exposed the hidden blue/yellow source half");
        }
        Check(red>100 && green>100,"Rotation lost a visible source colour");
        Color Sample(double x,double y)=>At(p,(int)(bounds.Left+bounds.Width*x),(int)(bounds.Top+bounds.Height*y));
        var first=command switch { "RotateRight"=>Sample(.5,.2), "RotateLeft"=>Sample(.5,.8), "FlipHorizontal"=>Sample(.8,.5), _=>Sample(.2,.5) };
        var second=command switch { "RotateRight"=>Sample(.5,.8), "RotateLeft"=>Sample(.5,.2), "FlipHorizontal"=>Sample(.2,.5), _=>Sample(.8,.5) };
        Check(first==Colors.Red && second==Colors.Lime && Sample(.5,.5)==Colors.Red,"Transform changed the visible colour proportions/orientation");
        var transformedHash=PixelHash(rendered);
        editor.Undo();EqualRect(Crop(First(editor)),originalCrop,"Undo crop mismatch");Check(PixelHash(editor.ExportImage())==original,"Transform undo pixel mismatch");
        editor.Redo();Check(PixelHash(editor.ExportImage())==transformedHash,"Transform redo pixel mismatch");
        Save((entireCanvas?"canvas-":"selected-")+command+".png",rendered);
    }
}




