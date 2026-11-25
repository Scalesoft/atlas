namespace Atlas.Markdown.Sync;

/// <summary>
/// The one authority over who has which collaborator colour on a live surface: each holder (a connection) present in a scope with the
/// colour <see cref="CollaboratorPalette.Effective"/> gave it at its join. A scope is what several surfaces may show together: the
/// Markdown hub uses <see cref="MarkdownSyncOptions.ColorScope"/> of the document, a host's view session its own key, so a page's
/// carets and its object presence (both <c>page:{id}</c>) hand out colours from one table and a person looks alike in both.
/// </summary>
public sealed class CollaboratorColors
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, (string UserId, string Color)>> _scopes = new(StringComparer.Ordinal);

    /// <summary>The colour of <paramref name="holder"/> in <paramref name="scope"/>: the one it holds, else assigned now from
    /// <paramref name="preferred"/> (a palette colour; null: <see cref="CollaboratorPalette.Auto"/>) among the others present.</summary>
    public string Assign(string scope, string holder, string userId, string? preferred)
    {
        lock (_lock)
        {
            if (!_scopes.TryGetValue(scope, out var held))
            {
                _scopes[scope] = held = new Dictionary<string, (string UserId, string Color)>(StringComparer.Ordinal);
            }
            if (held.TryGetValue(holder, out var own))
            {
                return own.Color;
            }
            var color = CollaboratorPalette.Effective(userId, preferred, held.Values);
            held[holder] = (userId, color);
            return color;
        }
    }

    /// <summary>The colour <paramref name="holder"/> holds in <paramref name="scope"/>, or null.</summary>
    public string? Find(string scope, string holder)
    {
        lock (_lock)
        {
            return _scopes.TryGetValue(scope, out var held) && held.TryGetValue(holder, out var own) ? own.Color : null;
        }
    }

    /// <summary>„Moje barva“ of <paramref name="userId"/> changed: each of their holders takes a colour again from <paramref name="preferred"/>
    /// while everyone else keeps theirs, so live sessions show the new one without reconnecting.</summary>
    public void Restamp(string userId, string? preferred)
    {
        lock (_lock)
        {
            foreach (var held in _scopes.Values)
            {
                var mine = held.Where(pair => pair.Value.UserId == userId).Select(pair => pair.Key).ToList();
                if (mine.Count == 0)
                {
                    continue;
                }
                mine.ForEach(holder => held.Remove(holder));
                var color = CollaboratorPalette.Effective(userId, preferred, held.Values);
                mine.ForEach(holder => held[holder] = (userId, color));
            }
        }
    }

    /// <summary>The holder left the scope: its colour is free again.</summary>
    public void Release(string scope, string holder)
    {
        lock (_lock)
        {
            if (_scopes.TryGetValue(scope, out var held) && held.Remove(holder) && held.Count == 0)
            {
                _scopes.Remove(scope);
            }
        }
    }
}
