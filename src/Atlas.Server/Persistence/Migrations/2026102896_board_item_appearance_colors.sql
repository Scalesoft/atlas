-- An editor component on a board takes a background from the appearance palette of spaces (appearance.ts, AppearanceCatalog):
-- twelve hues in five tones, '<hue>-<tone>'. The board's own eleven colours stay; the palette's legacy names (coral, rose…) are
-- not board colours. Existing rows keep their colour.
ALTER TABLE "BoardItem"
    DROP CONSTRAINT "CK_BoardItem_Color",
    ADD CONSTRAINT "CK_BoardItem_Color" CHECK (
        "Color" IN ('yellow','orange','red','pink','purple','blue','teal','green','gray','white','black')
        OR "Color" ~ '^(red|orange|amber|lime|green|teal|cyan|blue|indigo|violet|pink|slate)-[1-5]$');
