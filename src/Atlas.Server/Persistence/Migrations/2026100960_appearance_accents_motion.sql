-- Atlas 2.5 (Ohnisko): three more calm accents and the reduced-motion preference of the profile.
-- Expands the vocabulary only; current preferences and their temporal history stay as they are.
-- Runs in the migration runner's transaction. Automatic downgrade is unsupported:
-- historic rows may already reference one of the new values.
ALTER TABLE "UserAppearancePreference" DROP CONSTRAINT "CK_UserAppearancePreference_Accent";
ALTER TABLE "UserAppearancePreference" ADD CONSTRAINT "CK_UserAppearancePreference_Accent"
    CHECK ("Accent" IN ('blue', 'green', 'red', 'ocean', 'sunset', 'indigo', 'violet', 'amber'));
ALTER TABLE "UserAppearancePreference" ADD COLUMN "Motion" varchar(20) NOT NULL DEFAULT 'system';
ALTER TABLE "UserAppearancePreference" ADD CONSTRAINT "CK_UserAppearancePreference_Motion"
    CHECK ("Motion" IN ('system', 'reduce'));
