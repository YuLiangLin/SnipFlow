using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using SnipFlow.Editor;

namespace SnipFlow.ImageCrop.Regression;

internal static partial class Program
{
    static Type HandleType=>Product.GetType("SnipFlow.Editor.SelectionHandle",true)!;
    static object Draft(EditorView editor)=>typeof(EditorView).GetField("_cropDraft",Any)!.GetValue(editor)!;
    static bool Cropping(EditorView editor)=>(bool)Get(editor,"IsImageCropping")!;
    static void Begin(EditorView editor)=>Check((bool)Call(editor,"BeginImageCrop")!,"Could not begin selected image crop");
    static void Gesture(EditorView editor,string handle,Point anchor,Point current)
    {
        Call(editor,"BeginImageCropGesture",Enum.Parse(HandleType,handle),anchor);Call(editor,"UpdateImageCropGesture",current);Call(editor,"EndImageCropGesture");
    }
    static void DraftBottomRightQuarter(EditorView editor)
    {
        Begin(editor);Gesture(editor,"Left",new Point(80,120),new Point(160,120));Gesture(editor,"Top",new Point(200,60),new Point(200,120));
    }
    static void EditorCases()
    {
        using var source=Quadrants();var sourceHash=PixelHash(source);
        Case("editor crop requires one collage image selection",()=> {
            var empty=new EditorView();Check(!(bool)Call(empty,"BeginImageCrop")!,"Empty editor crop started");
            var editor=Editor(source,null);Call(editor,"SelectItem",(object?)null);Check(!(bool)Call(editor,"BeginImageCrop")!,"Crop started without image selection");
            editor.LoadImage(source);Check(!(bool)Call(editor,"BeginImageCrop")!,"Regular single-image document entered collage crop");
        });
        Case("editor no-op begin commit preserves saved revision and undo",()=> {
            var editor=Editor(source,null);var revision=editor.Revision;Begin(editor);Call(editor,"CommitImageCrop");Check(!Cropping(editor) && !editor.CanUndo && !editor.HasEdits && editor.Revision==revision,"No-op crop created a mutation");
        });
        Case("editor resizing frame is isolated in draft and commit creates one undo",()=> {
            var editor=Editor(source,null);var initialBounds=Bounds(First(editor));DraftBottomRightQuarter(editor);
            EqualRect(Bounds(Draft(editor)),new Rect(160,120,80,60),"Crop frame geometry");EqualRect(Crop(Draft(editor)),new Rect(.5,.5,.5,.5),"Normalized source crop geometry");EqualRect(Bounds(First(editor)),initialBounds,"Draft mutated committed frame");Check(Crop(First(editor)) is null,"Draft mutated committed crop");Check(editor.HasEdits,"Pending crop was not marked unsaved");
            Call(editor,"CommitImageCrop");Check(!Cropping(editor) && editor.CanUndo && editor.HasEdits,"Crop commit state");EqualRect(Crop(First(editor)),new Rect(.5,.5,.5,.5),"Commit lost crop");Check(ReferenceEquals(Get(First(editor),"Image"),source) && PixelHash(source)==sourceHash,"Crop was destructive");
            using var image=editor.ExportImage();Check(At(Pixels(image),180,140)==Avalonia.Media.Colors.Yellow,"Committed crop exported wrong quadrant");Save("editor-committed-crop.png",image);var after=PixelHash(image);
            editor.Undo();Check(!editor.CanUndo && editor.CanRedo && !editor.HasEdits,"Multiple drags should be one undo");Check(Crop(First(editor)) is null,"Undo lost full original");EqualRect(Bounds(First(editor)),initialBounds,"Undo frame mismatch");editor.Redo();Check(PixelHash(editor.ExportImage())==after,"Crop redo pixel mismatch");
        });
        Case("editor panning image inside fixed frame changes source offset only",()=> {
            var editor=Editor(source,null);DraftBottomRightQuarter(editor);var frame=Bounds(Draft(editor));Gesture(editor,"Move",new Point(180,140),new Point(220,170));EqualRect(Bounds(Draft(editor)),frame,"Panning moved the crop frame");EqualRect(Crop(Draft(editor)),new Rect(.25,.25,.5,.5),"Panning source mapping");
            Gesture(editor,"Move",default,new Point(100000,100000));EqualRect(Crop(Draft(editor)),new Rect(0,0,.5,.5),"Pan clamp at upper-left");Gesture(editor,"Move",default,new Point(-100000,-100000));EqualRect(Crop(Draft(editor)),new Rect(.5,.5,.5,.5),"Pan clamp at lower-right");Call(editor,"CommitImageCrop");Check(PixelHash(source)==sourceHash,"Pan modified original bitmap");
        });
        Case("editor cancel discards resize and pan without undo mutation",()=> {
            var editor=Editor(source,null);var before=PixelHash(editor.ExportImage());DraftBottomRightQuarter(editor);Gesture(editor,"Move",default,new Point(20,20));Call(editor,"CancelImageCrop");Check(!Cropping(editor) && !editor.CanUndo && !editor.HasEdits,"Cancel created a mutation");Check(PixelHash(editor.ExportImage())==before,"Cancel changed rendered document");
        });
        Case("editor reset is draft-only cancellable and undoable on commit",()=> {
            var initialCrop=new Rect(.5,0,.5,.5);var editor=Editor(source,initialCrop);var initial=PixelHash(editor.ExportImage());Begin(editor);Call(editor,"ResetImageCrop");Check(Crop(Draft(editor)) is null,"Reset did not reveal original image");EqualRect(Crop(First(editor)),initialCrop,"Reset mutated committed image before completion");Call(editor,"CancelImageCrop");Check(PixelHash(editor.ExportImage())==initial,"Cancel reset changed original crop");
            Begin(editor);Call(editor,"ResetImageCrop");var frame=Bounds(Draft(editor));Check(frame.Left>=0 && frame.Top>=0 && frame.Right<=editor.PixelWidth && frame.Bottom<=editor.PixelHeight,"Reset original lies outside collage");Call(editor,"CommitImageCrop");Check(Crop(First(editor)) is null && editor.CanUndo,"Reset commit state");Save("editor-reset-full-source.png",editor.ExportImage());editor.Undo();Check(PixelHash(editor.ExportImage())==initial,"Reset undo lost previous cropped content");
        });
        Case("editor Undo cancels unfinished crop before document history",()=> {
            var editor=Editor(source,null);var original=PixelHash(editor.ExportImage());DraftBottomRightQuarter(editor);editor.Undo();Check(!Cropping(editor) && !editor.CanRedo && !editor.HasEdits,"Undo did not cancel pending crop cleanly");Check(PixelHash(editor.ExportImage())==original,"Undo pending crop changed document");
        });
        Case("editor ExportImage commits pending crop as a single undo",()=> {
            var editor=Editor(source,null);DraftBottomRightQuarter(editor);using var rendered=editor.ExportImage();Check(!Cropping(editor),"Export left crop draft active");EqualRect(Crop(First(editor)),new Rect(.5,.5,.5,.5),"Export ignored crop draft");Check(At(Pixels(rendered),180,140)==Avalonia.Media.Colors.Yellow,"Export crop pixels");editor.Undo();Check(Crop(First(editor)) is null && !editor.CanUndo,"Export commit was not one undo");
        });
        Case("editor CaptureSession commits pending crop and cloned state",()=> {
            var editor=Editor(source,null);DraftBottomRightQuarter(editor);var session=Call(editor,"CaptureSession")!;Check(!Cropping(editor),"Session capture left crop draft active");var items=(System.Collections.IList)Get(session,"Annotations")!;EqualRect(Crop(items[0]!),new Rect(.5,.5,.5,.5),"Session snapshot omitted pending crop");editor.Undo();Check(Crop(First(editor)) is null,"CaptureSession crop not undoable");EqualRect(Crop(items[0]!),new Rect(.5,.5,.5,.5),"Undo mutated captured clone");
        });
        Case("editor LoadImage cancels crop rather than carrying stale draft",()=> {
            var editor=Editor(source,null);DraftBottomRightQuarter(editor);editor.LoadImage(source);Check(!Cropping(editor) && !editor.IsCollage && Items(editor).Count==0,"Loading image retained stale crop state");Check(editor.PixelWidth==80 && editor.PixelHeight==60 && !editor.HasEdits,"New image state corrupted");
        });
        Case("editor crop Enter and Escape handlers commit and cancel without OS input",()=> {
            var editor=Editor(source,null);DraftBottomRightQuarter(editor);var escape=new KeyEventArgs{Key=Key.Escape};Check((bool)Call(editor,"HandleImageCropKey",escape)! && escape.Handled,"Escape not consumed");Check(!Cropping(editor) && Crop(First(editor)) is null,"Escape did not cancel");
            DraftBottomRightQuarter(editor);var enter=new KeyEventArgs{Key=Key.Enter};Check((bool)Call(editor,"HandleImageCropKey",enter)! && enter.Handled,"Enter not consumed");Check(!Cropping(editor),"Enter did not complete crop");EqualRect(Crop(First(editor)),new Rect(.5,.5,.5,.5),"Enter commit crop mismatch");
        });
        Case("editor crop frame clamps at positive size and source boundaries",()=> {
            var editor=Editor(source,null);Begin(editor);Gesture(editor,"Left",new Point(80,120),new Point(10000,120));var draft=Draft(editor);Check(Bounds(draft).Width>0 && (bool)Static(CropGeometryType!,"IsValid",Crop(draft))!,"Frame collapsed or left normalized bounds");
            var previous=Crop(draft);Gesture(editor,"Move",default,new Point(double.NaN,0));EqualRect(Crop(Draft(editor)),previous,"Nonfinite pan changed crop");Call(editor,"CancelImageCrop");
        });
        foreach(var layout in new[]{"Grid","Vertical","Horizontal"})Case("editor "+layout+" arrangement uses visible crop aspect ratio",()=> {
            var editor=Editor(source,new Rect(.5,0,.5,1));var type=Product.GetType("SnipFlow.Editor.CollageLayout",true)!;Call(editor,"ArrangeImages",Enum.Parse(type,layout));var frame=Bounds(First(editor));Check(Math.Abs(frame.Width/frame.Height-40.0/60)<1e-9,"Arrangement used complete source aspect ratio instead of visible crop");EqualRect(Crop(First(editor)),new Rect(.5,0,.5,1),"Arrangement lost crop");
        });
    }
}

