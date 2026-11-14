-- Atlas 3.6: the profile choice "Otevírat stránky zamčené". Existing preferences and their history keep opening pages unlocked.
-- IF NOT EXISTS: a 3.6 database from before the 3.5.1 merge applied this as 2026110550.
ALTER TABLE "UserAppearancePreference" ADD COLUMN IF NOT EXISTS "OpenLocked" boolean NOT NULL DEFAULT false;
