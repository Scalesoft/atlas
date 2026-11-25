namespace Atlas.Server.Features.Boards.Domain;

/// <summary>An item on a board (BoardItemKinds); the rules work on its <see cref="BoardItemState"/>.</summary>
public class BoardItem
{
    public virtual Guid Id { get; set; }
    public virtual Guid BoardId { get; set; }
    public virtual string Kind { get; set; } = "";
    public virtual double X { get; set; }
    public virtual double Y { get; set; }
    public virtual double Width { get; set; }
    public virtual double Height { get; set; }
    public virtual int Z { get; set; }
    public virtual string? Text { get; set; }
    public virtual string? Color { get; set; }
    public virtual Guid? TargetResourceId { get; set; }
    public virtual Guid? FromItemId { get; set; }
    public virtual Guid? ToItemId { get; set; }
    public virtual long Version { get; set; }
    public virtual bool IsDeleted { get; set; }
    /// <summary>jsonb: PostgreSQL keeps its own key order and spacing, so it is read back through BoardProps.Canonical.</summary>
    public virtual string PropsJson { get; set; } = BoardProps.Empty;
    /// <summary>What search reads of the item (BoardProps.SearchText); the board's search document gathers these.</summary>
    public virtual string? SearchText { get; set; }
    /// <summary>The group or frame the item belongs to (explicit membership, objects spec §5.4).</summary>
    public virtual Guid? ParentId { get; set; }

    public virtual BoardItemState ToState()
    {
        return new BoardItemState(Id, Kind, X, Y, Width, Height, Z, Text, Color, TargetResourceId, FromItemId, ToItemId, Version, IsDeleted,
            BoardProps.Canonical(PropsJson), ParentId);
    }

    /// <summary>A new item from the state the rules created.</summary>
    public static BoardItem Create(Guid boardId, BoardItemState state)
    {
        var item = new BoardItem { Id = state.Id, BoardId = boardId, Kind = state.Kind, TargetResourceId = state.TargetResourceId, FromItemId = state.FromItemId, ToItemId = state.ToItemId };
        item.Apply(state);
        return item;
    }

    /// <summary>Takes over a state the rules produced; identity, board, kind and target never change after creation (a connector's ends
    /// do, by a <c>set</c>). True when
    /// what the board's search document or its references read changed: the text, what search reads of the props, or the item's presence.</summary>
    public virtual bool Apply(BoardItemState state)
    {
        var indexed = state.Text != Text || state.IsDeleted != IsDeleted;
        // Only a new text or props changes what search reads; a move does not render a block again.
        if (state.Text != Text || BoardProps.Canonical(PropsJson) != state.Props)
        {
            var searchText = BoardProps.SearchText(Kind, state.Text, state.Props);
            indexed |= searchText != SearchText;
            SearchText = searchText;
        }
        X = state.X;
        Y = state.Y;
        Width = state.Width;
        Height = state.Height;
        Z = state.Z;
        Text = state.Text;
        Color = state.Color;
        Version = state.Version;
        IsDeleted = state.IsDeleted;
        PropsJson = state.Props;
        ParentId = state.ParentId;
        FromItemId = state.FromItemId;
        ToItemId = state.ToItemId;
        return indexed;
    }
}
