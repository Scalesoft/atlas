using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Atlas.Yjs.ProseMirror;

/// <summary>
/// The writing half of the y-prosemirror binding as @tiptap/y-tiptap 3.0.9 ships it (src/lib.js and src/plugins/sync-plugin.js),
/// ported function by function: prosemirrorToYDoc, and updateYFragment with its helpers, into a <see cref="YDocument"/>.
/// Atlas calls updateYFragment with a fresh binding mapping (new Map()): a Y type is mapped only while its own parent is
/// processed, so every mappedIdentity check is false and reconcileMappedChildren finds no anchors. The port therefore leaves
/// the mapping out.
/// </summary>
internal static partial class YProseMirror
{
    /// <summary>
    /// y-tiptap 3.0.9 prosemirrorToYDoc: a fresh Y.Doc (with <paramref name="clientId"/>), its fragment filled by
    /// prosemirrorToYXmlFragment (updateYFragment into the empty fragment), as Y.encodeStateAsUpdate.
    /// </summary>
    public static byte[] ProsemirrorToYDoc(PmNode doc, string xmlFragment, uint clientId)
    {
        var y = new YDocument(new YjsDocumentTree(text: true), clientId);
        UpdateYFragment(y, y.GetXmlFragment(xmlFragment), doc, 1);
        return y.EncodeUpdate();
    }

    /// <summary>y-tiptap 3.0.9 updateYFragment.</summary>
    public static void UpdateYFragment(YDocument y, YTreeType yDomFragment, PmNode pNode) => UpdateYFragment(y, yDomFragment, pNode, 1);

    private static void UpdateYFragment(YDocument y, YTreeType yDomFragment, PmNode pNode, int depth)
    {
        if (depth > 512) throw new ContentRejectedException(ContentRejectionReason.LimitExceeded, "nested too deep");
        var isElement = yDomFragment.Kind == YTypeRef.XmlElement;
        if (isElement && yDomFragment.Name != pNode.TypeName) throw new InvalidOperationException("node name mismatch!");
        if (isElement)
        {
            var yDomAttrs = y.TypeMapGetAll(yDomFragment);
            var pAttrs = pNode.Attrs;
            foreach (var key in pAttrs.Keys)
            {
                if (pAttrs[key] is not null)
                {
                    if (!Js.StrictEquals(Js.Get(yDomAttrs, key), pAttrs[key]) && key != "ychange") y.TypeMapSet(yDomFragment, key, pAttrs[key]);
                }
                else y.TypeMapDelete(yDomFragment, key);
            }
            foreach (var key in yDomAttrs.Keys)
                if (!pAttrs.ContainsKey(key)) y.TypeMapDelete(yDomFragment, key);
        }
        var pChildren = NormalizePNodeContent(pNode);
        var pChildCnt = pChildren.Count;
        var yChildren = y.TypeListToArray(yDomFragment);
        var yChildCnt = yChildren.Count;
        var minCnt = Math.Min(pChildCnt, yChildCnt);
        var left = 0;
        var right = 0;
        for (; left < minCnt; left++)
            if (!EqualYTypePNode(y, yChildren[left], pChildren[left])) break;
        for (; right + left + 1 < minCnt; right++)
            if (!EqualYTypePNode(y, yChildren[yChildCnt - right - 1], pChildren[pChildCnt - right - 1])) break;
        while (yChildCnt - left - right > 0 && pChildCnt - left - right > 0)
        {
            var leftY = yChildren[left];
            var leftP = pChildren[left];
            var rightY = yChildren[yChildCnt - right - 1];
            var rightP = pChildren[pChildCnt - right - 1];
            if (leftY.Kind == YTypeRef.XmlText && leftP.Texts is { } texts)
            {
                if (!EqualYTextPText(y, leftY, texts)) UpdateYText(y, leftY, texts);
                left += 1;
            }
            else
            {
                var updateLeft = leftY.Kind == YTypeRef.XmlElement && MatchNodeName(leftY, leftP);
                var updateRight = rightY.Kind == YTypeRef.XmlElement && MatchNodeName(rightY, rightP);
                if (updateLeft && updateRight)
                {
                    var equalityLeft = ComputeChildEqualityFactor(y, leftY, leftP.Node!);
                    var equalityRight = ComputeChildEqualityFactor(y, rightY, rightP.Node!);
                    if (equalityLeft < equalityRight) updateLeft = false;
                    else updateRight = false;
                }
                if (updateLeft)
                {
                    UpdateYFragment(y, leftY, leftP.Node!, depth + 1);
                    left += 1;
                }
                else if (updateRight)
                {
                    UpdateYFragment(y, rightY, rightP.Node!, depth + 1);
                    right += 1;
                }
                else
                {
                    y.TypeListDelete(yDomFragment, left, 1);
                    y.TypeListInsertGenerics(yDomFragment, left, [CreateTypeFromTextOrElementNode(leftP)]);
                    left += 1;
                }
            }
        }
        var yDelLen = yChildCnt - left - right;
        if (yChildCnt == 1 && pChildCnt == 0 && yChildren[0].Kind == YTypeRef.XmlText)
        {
            // Edge case handling https://github.com/yjs/y-prosemirror/issues/108: only the text's content is deleted.
            y.TextDelete(yChildren[0], 0, YDocument.Length(yChildren[0]));
        }
        else if (yDelLen > 0) y.TypeListDelete(yDomFragment, left, yDelLen);
        if (left + right < pChildCnt)
        {
            var ins = new List<YPrelim>();
            for (var i = left; i < pChildCnt - right; i++) ins.Add(CreateTypeFromTextOrElementNode(pChildren[i]));
            y.TypeListInsertGenerics(yDomFragment, left, ins);
        }
    }

    /// <summary>A child of normalizePNodeContent: a node, or a run of adjacent text nodes.</summary>
    internal readonly record struct PChild(PmNode? Node, List<PmNode>? Texts);

    /// <summary>y-tiptap 3.0.9 normalizePNodeContent.</summary>
    internal static List<PChild> NormalizePNodeContent(PmNode pnode)
    {
        var c = pnode.Content;
        var res = new List<PChild>();
        for (var i = 0; i < c.Count; i++)
        {
            if (c[i].IsText)
            {
                var textNodes = new List<PmNode>();
                for (; i < c.Count && c[i].IsText; i++) textNodes.Add(c[i]);
                i--;
                res.Add(new PChild(null, textNodes));
            }
            else res.Add(new PChild(c[i], null));
        }
        return res;
    }

    /// <summary>y-tiptap 3.0.9 matchNodeName.</summary>
    private static bool MatchNodeName(YTreeType yElement, PChild pNode) => pNode.Node is { } node && yElement.Name == node.TypeName;

    /// <summary>y-tiptap 3.0.9 createTypeFromTextOrElementNode.</summary>
    private static YPrelim CreateTypeFromTextOrElementNode(PChild node)
        => node.Texts is { } texts ? CreateTypeFromTextNodes(texts) : CreateTypeFromElementNode(node.Node!);

    /// <summary>y-tiptap 3.0.9 createTypeFromTextNodes: new Y.XmlText and applyDelta of the text nodes (pending until integrated).</summary>
    private static YPrelimText CreateTypeFromTextNodes(List<PmNode> nodes)
    {
        var type = new YPrelimText();
        type.Pending.Add([.. nodes.Select(node => new YDeltaOp(node.Text, 0, MarksToAttributes(node.Marks)))]);
        return type;
    }

    /// <summary>y-tiptap 3.0.9 createTypeFromElementNode: attributes but null ones and ychange, then the content.</summary>
    private static YPrelimElement CreateTypeFromElementNode(PmNode node)
    {
        var type = new YPrelimElement(node.TypeName);
        foreach (var key in node.Attrs.Keys)
        {
            var val = node.Attrs[key];
            if (val is not null && key != "ychange") type.Attributes[key] = val;
        }
        type.Content.InsertRange(0, NormalizePNodeContent(node).Select(CreateTypeFromTextOrElementNode));
        return type;
    }

    /// <summary>y-tiptap 3.0.9 equalAttrs (keys with null values ignored, objects compared recursively).</summary>
    private static bool EqualAttrs(object? pattrs, object? yattrs)
    {
        var keys = Js.Keys(pattrs).Where(key => Js.Get(pattrs, key) is not null).ToArray();
        var eq = keys.Length == Js.Keys(yattrs).Count(key => Js.Get(yattrs, key) is not null);
        for (var i = 0; i < keys.Length && eq; i++)
        {
            var key = keys[i];
            var l = Js.Get(pattrs, key);
            var r = Js.Get(yattrs, key);
            eq = key == "ychange" || Js.StrictEquals(l, r) || (Js.IsObject(l) && Js.IsObject(r) && EqualAttrs(l, r));
        }
        return eq;
    }

    /// <summary>y-tiptap 3.0.9 equalYTextPText.</summary>
    private static bool EqualYTextPText(YDocument y, YTreeType ytext, List<PmNode> ptexts)
    {
        var delta = y.ToDelta(ytext);
        return delta.Count == ptexts.Count && delta.Select((d, i) => (d, i)).All(entry =>
        {
            var (d, i) = entry;
            var marks = ptexts[i].Marks;
            return d.Insert == ptexts[i].Text && (d.Attributes?.Count ?? 0) == marks.Count
                && (d.Attributes?.Keys ?? []).All(yattrname =>
                {
                    var markname = Yattr2markname(yattrname);
                    var pmark = marks.FirstOrDefault(mark => mark.Type.Name == markname);
                    return pmark is not null && EqualAttrs(d.Attributes![yattrname], pmark.Attrs);
                });
        });
    }

    /// <summary>y-tiptap 3.0.9 equalYTypePNode.</summary>
    private static bool EqualYTypePNode(YDocument y, YTreeType ytype, PChild pnode)
    {
        if (ytype.Kind == YTypeRef.XmlElement && pnode.Node is { } node && MatchNodeName(ytype, pnode))
        {
            var normalizedContent = NormalizePNodeContent(node);
            return YDocument.Length(ytype) == normalizedContent.Count && EqualAttrs(y.TypeMapGetAll(ytype), node.Attrs)
                && y.TypeListToArray(ytype).Select((ychild, i) => (ychild, i)).All(entry => EqualYTypePNode(y, entry.ychild, normalizedContent[entry.i]));
        }
        return ytype.Kind == YTypeRef.XmlText && pnode.Texts is { } texts && EqualYTextPText(y, ytype, texts);
    }

    /// <summary>y-tiptap 3.0.9 computeChildEqualityFactor (its equalityFactor; foundMappedChild is always false here).</summary>
    private static int ComputeChildEqualityFactor(YDocument y, YTreeType ytype, PmNode pnode)
    {
        var yChildren = y.TypeListToArray(ytype);
        var pChildren = NormalizePNodeContent(pnode);
        var pChildCnt = pChildren.Count;
        var yChildCnt = yChildren.Count;
        var minCnt = Math.Min(yChildCnt, pChildCnt);
        var left = 0;
        var right = 0;
        for (; left < minCnt; left++)
            if (!EqualYTypePNode(y, yChildren[left], pChildren[left])) break;
        for (; left + right < minCnt; right++)
            if (!EqualYTypePNode(y, yChildren[yChildCnt - right - 1], pChildren[pChildCnt - right - 1])) break;
        return left + right;
    }

    /// <summary>y-tiptap 3.0.9 ytextTrans: the live text, and every live format key set to null.</summary>
    private static (string Str, JsObject NAttrs) YtextTrans(YTreeType ytext)
    {
        var str = new System.Text.StringBuilder();
        var nAttrs = new JsObject();
        for (var n = ytext.Start; n is not null; n = n.Right)
        {
            if (n.Deleted) continue;
            if (n.Content == YContentKind.String) str.Append(n.Text);
            else if (n.Content == YContentKind.Format) nAttrs[n.Key!] = null;
        }
        return (str.ToString(), nAttrs);
    }

    /// <summary>y-tiptap 3.0.9 updateYText: the text diffed (lib0 simpleDiff), then a retain per text node sets its marks.</summary>
    private static void UpdateYText(YDocument y, YTreeType ytext, List<PmNode> ptexts)
    {
        var (str, nAttrs) = YtextTrans(ytext);
        var content = ptexts.Select(p =>
        {
            var attributes = nAttrs.Copy();
            var marks = MarksToAttributes(p.Marks);
            foreach (var key in marks.Keys) attributes[key] = marks[key];
            return (Insert: p.Text!, Attributes: attributes);
        }).ToArray();
        var (index, remove, insert) = SimpleDiff(str, string.Concat(content.Select(c => c.Insert)));
        y.TextDelete(ytext, index, remove);
        y.TextInsert(ytext, index, insert);
        y.ApplyDelta(ytext, [.. content.Select(c => new YDeltaOp(null, c.Insert.Length, c.Attributes))]);
    }

    /// <summary>lib0 0.2.117 diff.simpleDiffString: a common prefix and suffix that never split a surrogate pair.</summary>
    internal static (int Index, int Remove, string Insert) SimpleDiff(string a, string b)
    {
        var left = 0;
        var right = 0;
        while (left < a.Length && left < b.Length && a[left] == b[left]) left++;
        if (left > 0 && char.IsHighSurrogate(a[left - 1])) left--;
        while (right + left < a.Length && right + left < b.Length && a[a.Length - right - 1] == b[b.Length - right - 1]) right++;
        if (right > 0 && char.IsLowSurrogate(a[a.Length - right])) right--;
        return (left, a.Length - left - right, b[left..(b.Length - right)]);
    }

    [GeneratedRegex("(.*)(--[a-zA-Z0-9+/=]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex HashedMarkName();

    /// <summary>y-tiptap 3.0.9 yattr2markname.</summary>
    internal static string Yattr2markname(string attrName) => HashedMarkName().Match(attrName) is { Success: true } match ? match.Groups[1].Value : attrName;

    /// <summary>y-tiptap 3.0.9 marksToAttributes: a mark that does not exclude itself is keyed by name and hashOfJSON.</summary>
    internal static JsObject MarksToAttributes(List<PmMark> marks)
    {
        var pattrs = new JsObject();
        foreach (var mark in marks)
        {
            if (mark.Type.Name == "ychange") continue;
            pattrs[mark.Type.ExcludesItself ? mark.Type.Name : $"{mark.Type.Name}--{HashOfJson(mark.ToJson())}"] = mark.Attrs;
        }
        return pattrs;
    }

    /// <summary>y-tiptap 3.0.9 hashOfJSON: base64 of the sha256 of lib0 encodeAny, folded to six bytes (_convolute).</summary>
    internal static string HashOfJson(object? json)
    {
        var encoder = new Lib0Writer();
        encoder.WriteAny(json);
        var digest = SHA256.HashData(encoder.WrittenSpan);
        const int N = 6;
        for (var i = N; i < digest.Length; i++) digest[i % N] ^= digest[i];
        return Convert.ToBase64String(digest, 0, N);
    }
}
