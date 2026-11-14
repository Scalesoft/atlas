-- The picker and handler use the shared appearance catalog. The original page-decoration
-- constraint predates its expanded icon and colour palette. Keep the pair invariant and
-- legacy values while admitting every current catalog value.
ALTER TABLE "PageDecoration" DROP CONSTRAINT "CK_PageDecoration_Selection";
ALTER TABLE "PageDecoration" ADD CONSTRAINT "CK_PageDecoration_Selection" CHECK (
  ("Icon" IS NULL AND "Color" IS NULL)
  OR ("Icon" IS NOT NULL AND "Color" IS NOT NULL
    AND "Icon" IN (
      'book-open','home','star','sparkles','lightbulb','smile','compass','megaphone',
      'ticket','palette','pin','box','blocks','plug','highlighter','pencil',
      'search','file-text','file-plus','folder','folder-plus','folder-input','layout-template','list',
      'list-checks','list-ordered','scroll-text','quote','clipboard-paste','paperclip','image','table',
      'type','hash','link','external-link','users','workflow','user','user-check',
      'code-xml','square-code','code','database','server','network','monitor','settings-2',
      'sliders-horizontal','filter','activity','layout-dashboard','refresh-cw','download','upload','rotate-cw',
      'bar-chart-3','line-chart','pie-chart','donut','sigma','shield','shield-check','shield-alert',
      'lock','lock-open','key-round','fingerprint','eye','eye-off','ban','info',
      'circle-help','circle-check','circle-x','check','triangle-alert','octagon-alert','octagon-x','hourglass',
      'calendar','clock','history','sun','moon','globe','map-pin','building-2'
    )
    AND ("Color" IN ('blue','green','amber','coral','slate','teal','orange','rose','violet')
      OR "Color" ~ '^(red|orange|amber|lime|green|teal|cyan|blue|indigo|violet|pink|slate)-[1-5]$')
  )
);
