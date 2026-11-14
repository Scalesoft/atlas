// Creates the load accounts c2-load-01 … c2-load-NN on an isolated COPY (developer sign-in as the admin) and gives them Writer
// in the spaces named by ATLAS_LOAD_SPACES (comma-separated ids). Password from ATLAS_LOAD_PASSWORD; never run against a real wiki.
import { Session, loadAccounts, grant } from "./lib.mjs";

const password = process.env.ATLAS_LOAD_PASSWORD;
const count = Number(process.env.ATLAS_LOAD_USERS ?? 34);
const spaces = (process.env.ATLAS_LOAD_SPACES ?? "").split(",").filter(Boolean);
if (!password || password.length < 12 || !spaces.length) throw new Error("Set ATLAS_LOAD_PASSWORD (12+ characters) and ATLAS_LOAD_SPACES.");

const admin = await new Session("admin").developer();
const accounts = await loadAccounts(admin, count, password);
for (const space of spaces) for (const account of accounts) await grant(admin, space, account.id, "Writer");
console.log(`${accounts.length} accounts with Writer in ${spaces.length} space(s)`);
