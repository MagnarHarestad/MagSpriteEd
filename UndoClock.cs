namespace MagSpriteEd;

/// <summary>
/// Stamps undo entries from the two separate stacks - MainForm's pixel
/// edits and ConstructPanel's position/Sprite # edits - with one shared,
/// increasing number, so Undo/Redo can pick whichever change is actually
/// most recent now that both kinds happen in the same Construct view.
/// </summary>
internal static class UndoClock
{
    private static long _last;

    /// <summary>Always &gt; 0, so 0 can mean "stack is empty".</summary>
    public static long Next() => ++_last;
}
