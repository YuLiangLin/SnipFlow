namespace SnipFlow.Editor;

/// <summary>Preserves the single-image document while its editable collage is a separate document.</summary>
internal sealed class CollageNavigation
{
    EditorSessionSnapshot? returnDocument;

    internal bool HasReturnDocument => returnDocument is not null;
    internal bool IsTransitioning { get; private set; }

    internal void Enter(EditorView editor, bool blankCanvas)
    {
        editor.VerifyAccess();
        IsTransitioning = true;
        try
        {
            var wasCollage = editor.IsCollage;
            var previous = !wasCollage && editor.HasImage ? editor.CaptureSession() : null;
            if (blankCanvas) editor.NewCollage();
            else editor.CreateCollage();
            if (!wasCollage) returnDocument = previous;
        }
        finally { IsTransitioning = false; }
    }

    // The window saves or obtains an explicit discard decision before calling this operation.
    internal bool Exit(EditorView editor)
    {
        editor.VerifyAccess();
        if (!editor.IsCollage) return false;
        IsTransitioning = true;
        try
        {
            var previous = returnDocument;
            if (previous is not null)
                editor.RestoreSession(previous.Image, previous.Annotations, previous.HasEdits, previous.IsCollage);
            else
                editor.ClearDocument();
            returnDocument = null;
            return previous is not null;
        }
        finally { IsTransitioning = false; }
    }

    internal void Forget() => returnDocument = null;

    internal static bool ShouldOpenHistory(bool isCollage, int clickCount, bool isRecording)
        => clickCount == 1 ? isRecording || !isCollage : clickCount == 2 && isCollage;
}
