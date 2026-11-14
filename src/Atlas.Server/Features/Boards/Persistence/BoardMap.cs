using FluentMigrator;
using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Boards.Domain;

namespace Atlas.Server.Features.Boards.Persistence;

[Migration(2026102610, "Boards, board items and the board operation log")]
public sealed class BoardsMigration() : EmbeddedSchemaMigration("2026102610_boards.sql");

[Migration(2026102620, "Typed props, kinds and colours of board items, board settings")]
public sealed class BoardInteractionsMigration() : EmbeddedSchemaMigration("2026102620_board_interactions.sql");

[Migration(2026102640, "Boards in the space tree, their trash stamp and their search documents")]
public sealed class BoardsInTreeMigration() : EmbeddedSchemaMigration("2026102640_boards_in_tree.sql");

[Migration(2026102680, "Board groups and explicit membership of groups and frames")]
public sealed class BoardGroupsMigration() : EmbeddedSchemaMigration("2026102680_board_groups.sql");

[Migration(2026102750, "Object cards: board items that show any object")]
public sealed class BoardObjectCardsMigration() : EmbeddedSchemaMigration("2026102750_board_object_cards.sql");

[Migration(2026102820, "A page's own board: its purpose, outside the space tree, as readable as its page")]
public sealed class PageBoardsMigration() : EmbeddedSchemaMigration("2026102820_page_boards.sql");

[Migration(2026102830, "Live cards by their target: placements across boards")]
public sealed class BoardItemTargetIndexMigration() : EmbeddedSchemaMigration("2026102830_board_item_target_index.sql");

[Migration(2026102896, "Board item colours from the appearance palette: an editor component's background")]
public sealed class BoardItemAppearanceColorsMigration() : EmbeddedSchemaMigration("2026102896_board_item_appearance_colors.sql");

[Migration(2026102991, "Personal boards dropped: each one becomes its owner's private note; a board has a space again")]
public sealed class DropPersonalBoardsMigration() : EmbeddedSchemaMigration("2026102991_drop_personal_boards.sql");

[Migration(2026110185, "Board lists derived from the relations (links): Lidé projektu, Na co čekáme, Kde se používá")]
public sealed class BoardLinksItemsMigration() : EmbeddedSchemaMigration("2026110185_board_links_items.sql");

[Migration(2026110470, "The 3.3 deployment boards and the requirement cards on other boards are deleted")]
public sealed class DropDeploymentBoardsMigration() : EmbeddedSchemaMigration("2026110470_drop_deployment_boards.sql");

public sealed class BoardMap : ClassMapping<Board>
{
    public BoardMap()
    {
        Table("`Board`");
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.SpaceId);
        Property(x => x.ParentPageId);
        Property(x => x.SortOrder);
        Property(x => x.Title);
        Property(x => x.Sequence);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.CreatedBy);
        Property(x => x.UpdatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.IsTrashed);
        Property(x => x.TrashedAt, m => m.Type(NHibernateUtil.UtcDateTime));
        Property(x => x.TrashedById);
        Property(x => x.TrashBatchId);
        Property(x => x.SettingsJson, m => m.Type<JsonbText>());
        Property(x => x.Purpose);
    }
}
