-- Atlas 4.1: the profile choice "Jazyk". Existing preferences and their history leave the language to the browser.
ALTER TABLE "UserAppearancePreference" ADD COLUMN "Language" text NOT NULL DEFAULT 'auto';
