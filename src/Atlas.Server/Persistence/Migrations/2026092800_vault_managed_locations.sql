-- A managed vault location keeps its KDBX files in a folder Atlas derives from its data directory (Vaults:DataPath, else
-- .data/vaults/<location id>), so it stores no path: an empty Root keeps a moved installation or a changed configuration working.
ALTER TABLE "VaultLocation" DROP CONSTRAINT "CK_VaultLocation_Kind";
ALTER TABLE "VaultLocation" DROP CONSTRAINT "CK_VaultLocation_Root";
ALTER TABLE "VaultLocation" ADD CONSTRAINT "CK_VaultLocation_Kind" CHECK ("Kind" IN ('managed','local','smb'));
ALTER TABLE "VaultLocation" ADD CONSTRAINT "CK_VaultLocation_Root" CHECK (CASE "Kind"
    WHEN 'managed' THEN "Root" = ''
    WHEN 'smb' THEN "Root" ILIKE 'smb://%'
    ELSE length(btrim("Root")) > 0 AND "Root" NOT ILIKE 'smb://%' END);
