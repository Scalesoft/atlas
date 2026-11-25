-- Atlas 5.2: the profile choice "Moje barva". Existing preferences and their history stay "auto": the colour they had by the account's hash.
ALTER TABLE "UserAppearancePreference" ADD COLUMN "CollaboratorColor" text NOT NULL DEFAULT 'auto';
