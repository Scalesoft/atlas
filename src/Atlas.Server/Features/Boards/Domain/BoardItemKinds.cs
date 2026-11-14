namespace Atlas.Server.Features.Boards.Domain;

/// <summary>The kinds of board items (interaction spec §4); their props are in BoardProps. A contact, a chart or a YouTrack issue
/// is a <see cref="Block"/>: the editor's own component, so each use case has one component.</summary>
public static class BoardItemKinds
{
    public const string Note = "note";
    public const string Text = "text";
    public const string Shape = "shape";
    public const string Frame = "frame";
    /// <summary>A logical container with a name and a colour whose members move together; it collapses and nests (§5.4).</summary>
    public const string Group = "group";
    public const string Page = "page";
    public const string Block = "block";
    /// <summary>A live card of any object (objects spec §5.1): props name it (objectId, typeId) and its view; its target is the
    /// object, so the board view and its placements know it.</summary>
    public const string Object = "object";
    public const string Connector = "connector";
    /// <summary>A list the board derives from the relations, never written by hand (P15): the people of the space or of a page
    /// (<c>people</c>), what it waits for (<c>waiting</c>), or where an object is used (<c>usage</c>). Props name only what it
    /// shows; every reader reads the rows through the rights of the relation reads.</summary>
    public const string Links = "links";
}
