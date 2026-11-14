-- Expand the existing style vocabulary; preserve current preferences and all temporal history.
-- Runs in the migration runner's transaction. Automatic downgrade is unsupported:
-- historic rows may already reference one of the new styles.
ALTER TABLE "UserAppearancePreference" DROP CONSTRAINT "CK_UserAppearancePreference_Accent";
ALTER TABLE "UserAppearancePreference" ADD CONSTRAINT "CK_UserAppearancePreference_Accent"
    CHECK ("Accent" IN ('blue', 'green', 'red', 'ocean', 'sunset'));
