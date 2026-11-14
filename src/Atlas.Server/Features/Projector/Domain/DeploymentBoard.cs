using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Boards.Contracts;
using Atlas.Server.Features.Boards.Domain;
using Atlas.Server.Platform.Content;

namespace Atlas.Server.Features.Projector.Domain;

/// <summary>
/// The deployment of one page as its board shows it (plan 3.4 §5) and „Poskládat tabuli“: a 1:1 port of the web's
/// src/Atlas.Web/src/projector/deploymentMap.ts (<c>deploymentMap</c>, <c>composeChanges</c>, <c>arrangeChanges</c>), so projector_compose_board lays out and arranges
/// the same board as the web's actions. Both answer tests/fixtures/content/deployment-board.json the same (DeploymentBoardParityTests,
/// deploymentBoardParity.test.ts). Products are the outermost headings over blocks, categories and modules the headings below them,
/// services the <c>system</c> of the inputs; a nested block is in its parent's module. A requirement heading (a heading with a state) is a
/// block in the module of the plain headings above it.
/// </summary>
public static partial class DeploymentBoard
{
    public const string Title = "Tabule nasazení";
    /// <summary>A new page board's title names its page (the breadcrumb says whose board it is), cut to the board title limit.</summary>
    public static string TitleFor(string pageTitle) => (pageTitle.Trim() is { Length: > 0 } name ? $"{Title} — {name}" : Title) is var full && full.Length > 200 ? full[..200] : full;

    public const string CoreCategory = "Jádro";
    public const int MaxBatch = 200;

    private const double ProductWidth = 400, ServiceWidth = 300, ServiceHeight = 150, Gap = 32, Below = 160, Lane = 120, Stagger = 56;

    /// <summary>A block: identity matters (a service's inputs are the blocks themselves), so a class.</summary>
    public sealed class Block(string id, string title, JsonNode? attrs)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string State { get; } = RequirementAttributes.Text(attrs, "state") is { Length: > 0 } state ? state : "planned";
        public string Key { get; } = RequirementAttributes.Text(attrs, "key");
        public string System { get; } = RequirementAttributes.Text(attrs, "system");
        public string Requires { get; } = RequirementAttributes.Text(attrs, "requires");
        public JsonNode? Attrs { get; } = attrs;
    }

    public sealed record Module(string Name, string Anchor, List<Block> Blocks);

    public sealed record Category(string Name, List<Module> Modules);

    public sealed record Product(string Name, string Anchor, List<Block> Base, List<Category> Categories, List<Block> Blocks);

    public sealed record Service(string Name, string Anchor, List<Block> Inputs, List<string> Products);

    /// <summary><c>Internal</c>: the <c>system</c> names another product of the page, so <c>Service</c> is that product's anchor (a connector between the two product cards).</summary>
    public sealed record Link(string Product, string Service, IReadOnlyList<string> Modules, int Count, string State, bool Internal = false);

    public sealed record Map(IReadOnlyList<Product> Products, IReadOnlyList<Service> Services, IReadOnlyList<Link> Links);

    // The headingId charset of the shape 2026-11-04 §1 (the dot is new; deploymentMap.ts follows with the editor's heading ids).
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ValidAnchor();

    private static string Type(JsonNode? node) => node?["type"] is JsonValue value && value.TryGetValue<string>(out var type) ? type : "";

    private static string TextOf(JsonNode? node)
        => node?["text"] is JsonValue text && text.TryGetValue<string>(out var value) ? value : string.Concat((node?["content"] as JsonArray ?? []).Select(TextOf));

    private static string HeadingAnchor(JsonNode node, string text)
        => node["attrs"]?["headingId"] is JsonValue id && id.TryGetValue<string>(out var value) && ValidAnchor().IsMatch(value) && !HeadingIds.IsTemporary(value)
            ? value : RequirementAttributes.Slug(text).Replace('.', '-');

    /// <summary>A service's anchor on a board: its name's slug („NIA“ and „nia“ are one service).</summary>
    public static string ServiceAnchor(string name) => RequirementAttributes.Slug(name).Replace('.', '-');

    private static Block BlockOf(JsonNode node)
    {
        var content = node["content"] as JsonArray;
        return new Block(RequirementAttributes.Text(node["attrs"], "id"), content is { Count: > 0 } ? TextOf(content[0]).Trim() : "", node["attrs"]);
    }

    /// <summary>Blocked over waiting over all done over moving: the state a module, product or service shows.</summary>
    public static string Rollup(IEnumerable<Block> blocks)
    {
        var states = blocks.Select(block => block.State).Where(state => state != "not_applicable").ToList();
        if (states.Contains("blocked"))
        {
            return "blocked";
        }
        if (states.Contains("waiting_for_input"))
        {
            return "waiting_for_input";
        }
        if (states.Count > 0 && states.All(state => state == "done"))
        {
            return "done";
        }
        return states.Any(state => state is "in_progress" or "done") ? "in_progress" : "planned";
    }

    /// <summary>The page's deployment from its stored document (its <c>document</c> node).</summary>
    public static Map Of(JsonNode? document)
    {
        var products = new List<Product>();
        var open = new List<(string Text, int Level, string Anchor)>();
        void Add(JsonNode node)
        {
            // Blocks before the first heading, or under a heading with no text to anchor a card to, belong to no product.
            if (open.Count == 0 || open[0].Anchor.Length == 0)
            {
                return;
            }
            var top = open[0];
            var product = products.Find(entry => entry.Anchor == top.Anchor);
            if (product is null)
            {
                products.Add(product = new Product(top.Text, top.Anchor, [], [], []));
            }
            var blocks = new List<Block>();
            void WalkBlock(JsonNode block)
            {
                blocks.Add(BlockOf(block));
                foreach (var child in block["content"] as JsonArray ?? [])
                {
                    if (Type(child) == "requirement")
                    {
                        WalkBlock(child!);
                    }
                }
            }
            WalkBlock(node);
            product.Blocks.AddRange(blocks);
            if (open.Count == 1)
            {
                product.Base.AddRange(blocks);
                return;
            }
            var module = open[^1];
            var categoryName = open.Count > 2 ? open[^2].Text : CoreCategory;
            var category = product.Categories.Find(entry => entry.Name == categoryName);
            if (category is null)
            {
                product.Categories.Add(category = new Category(categoryName, []));
            }
            var found = category.Modules.Find(entry => entry.Anchor == module.Anchor);
            if (found is null)
            {
                category.Modules.Add(found = new Module(module.Text, module.Anchor, []));
            }
            found.Blocks.AddRange(blocks);
        }
        void Walk(IEnumerable<JsonNode?> nodes)
        {
            foreach (var child in nodes)
            {
                switch (Type(child))
                {
                    case "heading":
                        var level = child!["attrs"]?["level"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 1;
                        var text = TextOf(child).Trim();
                        while (open.Count > 0 && open[^1].Level >= level)
                        {
                            open.RemoveAt(open.Count - 1);
                        }
                        open.Add((text, level, HeadingAnchor(child, text)));
                        break;
                    case "requirement":
                        Add(child!);
                        break;
                    default:
                        Walk(child?["content"] as JsonArray ?? []);
                        break;
                }
            }
        }
        // A requirement heading is a block (its view), never a module: a module is a plain heading that holds requirements.
        Walk(RequirementHeadings.Project(document));

        var services = new List<Service>();
        foreach (var block in products.SelectMany(product => product.Blocks))
        {
            var name = block.System.Trim();
            var anchor = name.Length > 0 ? ServiceAnchor(name) : "";
            if (anchor.Length == 0)
            {
                continue;
            }
            var service = services.Find(entry => entry.Anchor == anchor);
            if (service is null)
            {
                services.Add(service = new Service(name, anchor, [], []));
            }
            service.Inputs.Add(block);
        }
        var byKey = new Dictionary<string, Block>(StringComparer.Ordinal);
        foreach (var block in products.SelectMany(product => product.Blocks).Where(block => block.Key.Length > 0))
        {
            byKey[block.Key] = block;
        }
        var links = new List<Link>();
        // A system that names a product of the page is that product, no external service.
        var productOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var product in products)
        {
            productOf[ServiceAnchor(product.Name)] = product.Anchor;
        }
        foreach (var product in products)
        {
            var moduleOf = new Dictionary<Block, string>(ReferenceEqualityComparer.Instance);
            foreach (var module in product.Categories.SelectMany(category => category.Modules))
            {
                foreach (var block in module.Blocks)
                {
                    moduleOf[block] = module.Name;
                }
            }
            foreach (var service in services)
            {
                var used = new List<Block>();
                var modules = new List<string>();
                foreach (var block in product.Blocks)
                {
                    var own = service.Inputs.Contains(block);
                    var needed = (block.Requires.Length > 0 ? block.Requires.Split(',') : []).Select(key => byKey.GetValueOrDefault(key))
                        .Where(input => input is not null && service.Inputs.Contains(input)).ToList();
                    foreach (var input in (own ? [block] : Array.Empty<Block>()).Concat(needed!))
                    {
                        if (!used.Contains(input!))
                        {
                            used.Add(input!);
                        }
                    }
                    if ((own || needed.Count > 0) && moduleOf.TryGetValue(block, out var moduleName) && !modules.Contains(moduleName))
                    {
                        modules.Add(moduleName);
                    }
                }
                var target = productOf.GetValueOrDefault(service.Anchor);
                if (used.Count == 0 || target == product.Anchor)
                {
                    continue;
                }
                if (!service.Products.Contains(product.Name))
                {
                    service.Products.Add(product.Name);
                }
                links.Add(new Link(product.Anchor, target ?? service.Anchor, modules, used.Count, Rollup(used), target is not null));
            }
        }
        return new Map(products, services.Where(service => !productOf.ContainsKey(service.Anchor)).ToList(), links);
    }

    /// <summary>„NIA, NRZP · 3 pož.“: the connector's label, cut to what a board item's text may hold (BoardEditing.MaxText).</summary>
    public static string LinkLabel(Link link)
    {
        var label = string.Join(" · ", new[] { string.Join(", ", link.Modules), link.Count.ToString(CultureInfo.InvariantCulture) + " pož." }.Where(part => part.Length > 0));
        return label.Length > BoardEditing.MaxText ? label[..BoardEditing.MaxText] : label;
    }

    private const double TagRow = 360, TagMax = 220, TagGap = 4;

    /// <summary>The rows a product's tags wrap into (the web's .dcard-tag): about 6.4 px a character and its padding, at most 220 px a tag.</summary>
    public static int TagRows(IEnumerable<Block> blocks)
    {
        var (rows, used) = (0, TagRow);
        foreach (var block in blocks)
        {
            var width = Math.Min(TagMax, 18 + 6.4 * (block.Title.Length > 0 ? block.Title : block.Key).Length);
            if (used + TagGap + width > TagRow)
            {
                rows++;
                used = width;
            }
            else
            {
                used += TagGap + width;
            }
        }
        return rows;
    }

    /// <summary>A product card as tall as its tags, categories and modules.</summary>
    public static double ProductHeight(Product product)
        => Math.Min(1600, 84 + (product.Base.Count > 0 ? 14 + 26 * TagRows(product.Base) : 0) + product.Categories.Count * 44 + product.Categories.Sum(category => category.Modules.Count * 36));

    private static string? Prop(JsonElement props, string name)
        => props.ValueKind == JsonValueKind.Object && props.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record Placed(string Display, string Anchor, double X, double Y, double Width, double Height);

    private static string CardKey(string display, string anchor) => display + ":" + anchor;

    private static bool IsCard(BoardItemDto item, Guid pageId) => item.Kind == "page" && item.Target?.ResourceId == pageId && Prop(item.Props, "display") is "product" or "service";

    /// <summary>
    /// The architecture picture, the one placement Compose and Arrange share (deploymentMap.ts <c>lanes</c>): the products side by side from
    /// <c>left</c> in the page's order, each centred over a row of the external services it is the first to use, every other service of the
    /// row a step lower (the labels of the connectors fanning out of the product then do not cover each other). <c>sizeOf</c> says which
    /// cards are placed and how large each is (null: not placed). <c>Below</c> names the product>service pairs that stand one under the
    /// other: their connector runs straight down.
    /// </summary>
    private static (List<Placed> Cards, HashSet<string> Below) Lanes(Map map, double left, double top, Func<string, string, (double Width, double Height)?> sizeOf)
    {
        var (cards, below, taken) = (new List<Placed>(), new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        var x = left;
        foreach (var product in map.Products)
        {
            var own = sizeOf("product", product.Anchor);
            var services = map.Links.Where(link => !link.Internal && link.Product == product.Anchor && !taken.Contains(link.Service))
                .SelectMany(link => sizeOf("service", link.Service) is { } size ? new[] { (Anchor: link.Service, size.Width, size.Height) } : []).ToList();
            var row = services.Sum(service => service.Width + Gap) - Gap;
            var lane = Math.Max(own?.Width ?? 0, row);
            if (lane <= 0)
            {
                continue;
            }
            if (own is { } ownSize)
            {
                cards.Add(new Placed("product", product.Anchor, x + (lane - ownSize.Width) / 2, top, ownSize.Width, ownSize.Height));
            }
            var at = x + (lane - row) / 2;
            for (var index = 0; index < services.Count; index++)
            {
                var service = services[index];
                taken.Add(service.Anchor);
                cards.Add(new Placed("service", service.Anchor, at, top + (own?.Height + Below ?? 0) + index % 2 * Stagger, service.Width, service.Height));
                at += service.Width + Gap;
                if (own is not null)
                {
                    below.Add($"{product.Anchor}>{service.Anchor}");
                }
            }
            x += lane + Lane;
        }
        return (cards, below);
    }

    /// <summary>
    /// The changes that lay the deployment out on a board as an architecture picture (deploymentMap.ts composeChanges): the products side by
    /// side in the page's order, under each product the external services it is the first to use, a connector from a product to each
    /// service it uses and one between two products when one's system names the other. Only what the board lacks: whatever is there (cards a
    /// person moved or resized, their own arrows and items) stays untouched, new cards go in a row under everything the board holds; a
    /// connector only between cards that have none. Cards come before their connectors.
    /// </summary>
    public static IReadOnlyList<BoardChange> Compose(Map map, Guid pageId, IReadOnlyList<BoardItemDto> items, Func<Guid> newId)
    {
        var changes = new List<BoardChange>();
        long z = items.Aggregate(0L, (top, item) => Math.Max(top, item.Z));
        Dictionary<string, Guid> IdsOf(string display)
        {
            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var card in items.Where(item => item.Kind == "page" && item.Target?.ResourceId == pageId && Prop(item.Props, "display") == display))
            {
                ids[Prop(card.Props, "anchor") ?? ""] = card.Id;
            }
            return ids;
        }
        var (productIds, serviceIds) = (IdsOf("product"), IdsOf("service"));
        var placed = items.Where(item => item.Kind != "connector").ToList();
        var top = placed.Aggregate(0d, (bottom, item) => Math.Max(bottom, item.Y + item.Height + Gap));
        var heights = map.Products.ToDictionary(product => product.Anchor, ProductHeight, StringComparer.Ordinal);
        var (cards, below) = Lanes(map, placed.Count > 0 ? placed.Min(item => item.X) : 0, top, (display, anchor) => (display == "product" ? productIds : serviceIds).ContainsKey(anchor) ? null
            : display == "product" ? (ProductWidth, heights[anchor]) : (ServiceWidth, ServiceHeight));
        foreach (var card in cards)
        {
            var id = newId();
            (card.Display == "product" ? productIds : serviceIds)[card.Anchor] = id;
            changes.Add(new BoardChange("create", new BoardItemInput(id, "page", card.X, card.Y, card.Width, card.Height, ++z, TargetResourceId: pageId,
                Props: new JsonObject { ["display"] = card.Display, ["anchor"] = card.Anchor })));
        }
        var connected = items.Where(item => item.Kind == "connector").Select(item => $"{item.From}>{item.To}").ToHashSet(StringComparer.Ordinal);
        foreach (var link in map.Links)
        {
            var (from, to) = (productIds[link.Product], (link.Internal ? productIds : serviceIds)[link.Service]);
            if (connected.Contains($"{from}>{to}"))
            {
                continue;
            }
            // Without sides a connector takes the two that face each other, wherever its cards are moved (the web's autoSides).
            var props = new JsonObject { ["route"] = "curved", ["arrowStart"] = "none", ["arrowEnd"] = "arrow", ["dash"] = "solid" };
            if (!link.Internal && below.Contains($"{link.Product}>{link.Service}"))
            {
                props["fromSide"] = "bottom";
                props["toSide"] = "top";
            }
            if (link.State switch { "done" => "done", "waiting_for_input" => "waiting", "blocked" => "blocked", _ => null } is { } state)
            {
                props["state"] = state;
            }
            changes.Add(new BoardChange("create", new BoardItemInput(newId(), "connector", 0, 0, 8, 8, 0, Text: LinkLabel(link), From: from, To: to, Props: props)));
        }
        return changes;
    }

    /// <summary>
    /// „Uspořádat“ of a deployment board (deploymentMap.ts arrangeChanges): the product and service cards of <c>pages</c> that are on the board
    /// (those of <c>ids</c>, or all of them when none is given) moved into the architecture picture Compose makes (Lanes), each at the size it
    /// has, the pages' pictures one under another in the order given. The picture keeps the top left corner of where the cards stand now,
    /// unless a card would then cover an item that stays (a person's note or shape, a card of another page, one out of the selection; a
    /// frame or group may hold it): then the whole picture goes under everything that stays. The connectors compose made between two
    /// arranged cards (a link of the page between those two cards, in its direction and with its label) face each other again: straight
    /// down from a product to a service under it, the facing sides (none stored) between others. Nothing else moves or changes. One batch.
    /// </summary>
    public static IReadOnlyList<BoardChange> Arrange(IReadOnlyList<(Guid PageId, Map Map)> pages, IReadOnlyList<BoardItemDto> items, IReadOnlyCollection<Guid>? ids = null)
    {
        var sets = pages.Select(page =>
        {
            var byKey = new Dictionary<string, BoardItemDto>(StringComparer.Ordinal);
            foreach (var card in items.Where(item => IsCard(item, page.PageId) && (ids is not { Count: > 0 } || ids.Contains(item.Id))))
            {
                byKey[CardKey(Prop(card.Props, "display")!, Prop(card.Props, "anchor") ?? "")] = card;
            }
            return (page.Map, ByKey: byKey);
        }).Where(set => set.ByKey.Count > 0).ToList();
        var all = sets.SelectMany(set => set.ByKey.Values).ToList();
        if (all.Count == 0)
        {
            return [];
        }
        var left = all.Min(card => card.X);
        List<(Map Map, Dictionary<string, BoardItemDto> ByKey, List<Placed> Cards, HashSet<string> Below)> Lay(double top)
        {
            var laid = new List<(Map, Dictionary<string, BoardItemDto>, List<Placed>, HashSet<string>)>();
            foreach (var set in sets)
            {
                var (cards, below) = Lanes(set.Map, left, top, (display, anchor) => set.ByKey.TryGetValue(CardKey(display, anchor), out var card) ? (card.Width, card.Height) : null);
                top = cards.Aggregate(top, (bottom, next) => Math.Max(bottom, next.Y + next.Height + Lane));
                laid.Add((set.Map, set.ByKey, cards, below));
            }
            return laid;
        }
        var pictures = Lay(all.Min(card => card.Y));
        var moving = pictures.SelectMany(picture => picture.Cards.Select(next => picture.ByKey[CardKey(next.Display, next.Anchor)].Id)).ToHashSet();
        var staying = items.Where(item => item.Kind is not ("connector" or "frame" or "group") && !moving.Contains(item.Id)).ToList();
        bool Covers(Placed a, BoardItemDto b) => a.X < b.X + b.Width + Gap && b.X < a.X + a.Width + Gap && a.Y < b.Y + b.Height + Gap && b.Y < a.Y + a.Height + Gap;
        if (pictures.Any(picture => picture.Cards.Any(next => staying.Any(other => Covers(next, other)))))
        {
            pictures = Lay(staying.Max(item => item.Y + item.Height) + Gap);
        }
        var changes = new List<BoardChange>();
        foreach (var (map, byKey, cards, below) in pictures)
        {
            var arranged = new HashSet<Guid>();
            foreach (var next in cards)
            {
                var card = byKey[CardKey(next.Display, next.Anchor)];
                arranged.Add(card.Id);
                if (card.X != next.X || card.Y != next.Y)
                {
                    changes.Add(new BoardChange("move", ItemId: card.Id, X: next.X, Y: next.Y, Width: card.Width, Height: card.Height));
                }
            }
            foreach (var link in map.Links)
            {
                if (!byKey.TryGetValue(CardKey("product", link.Product), out var from) || !byKey.TryGetValue(CardKey(link.Internal ? "product" : "service", link.Service), out var to)
                    || !arranged.Contains(from.Id) || !arranged.Contains(to.Id))
                {
                    continue;
                }
                var down = !link.Internal && below.Contains($"{link.Product}>{link.Service}");
                var (fromSide, toSide) = down ? ("bottom", "top") : ((string?)null, (string?)null);
                foreach (var line in items.Where(item => item.Kind == "connector" && item.From == from.Id && item.To == to.Id && (item.Text ?? "") == LinkLabel(link)))
                {
                    if (Prop(line.Props, "fromSide") != fromSide || Prop(line.Props, "toSide") != toSide)
                    {
                        changes.Add(new BoardChange("set", ItemId: line.Id, Props: new JsonObject { ["fromSide"] = fromSide, ["toSide"] = toSide }));
                    }
                }
            }
        }
        return changes;
    }
}
