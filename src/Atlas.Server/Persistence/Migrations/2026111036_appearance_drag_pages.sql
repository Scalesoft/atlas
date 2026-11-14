-- Atlas 3.6: the profile choice "Přesouvat stránky tažením". Existing preferences and their history keep dragging pages in the tree.
ALTER TABLE "UserAppearancePreference" ADD COLUMN "DragPages" boolean NOT NULL DEFAULT true;
