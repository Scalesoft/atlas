# KeePass integration: dependency assessment

Checked 2026-09-25. User clarification: **LGPL is acceptable when its conditions are met without requiring publication of Atlas source**. The earlier blanket no-copyleft requirement is superseded for LGPL. Prefer permissive licenses; GPL/AGPL dependencies remain outside the approved integration scope. Support commercial distribution and a possible future open-source release. This is a technical dependency assessment, not legal clearance or a complete product SBOM audit.

## Decision update 2026-09-26 (WP4 V1)

The owner chose an own minimal KDBX 4.0/4.1 reader (and a writer only for new empty databases) in `src/Atlas.Server/Features/Vaults/Kdbx` instead of adopting DgNet.Keepass. The only added package is **BouncyCastle.Cryptography 2.7.0** (MIT, no NuGet dependencies, no advisories listed on nuget.org on 2026-09-26; package metadata and packaged `LICENSE.md` checked), used solely for Argon2 (`Argon2BytesGenerator`) and raw RFC 7539 ChaCha20 (`ChaCha7539Engine`). AES, AES-KDF rounds, SHA-2, HMAC and GZip are .NET primitives. The notice is in [NOTICES.md](../NOTICES.md). No KeePass or KeePassXC code was consulted or ported; the implementation follows the published format description.

The DgNet findings below shaped the reader: every resource limit (`Vaults:*` options: file 16 MiB, header 64 KiB, header field 16 KiB, Argon2 memory 256 MiB / iterations 32 / parallelism 8, AES-KDF 60 million rounds, decompressed 64 MiB, group depth 64, 50 000 entries, XML field 1 MiB) is checked before the work it bounds; the header SHA-256 is verified before any field is interpreted; key derivation is single flight; decompression is counted while streaming; XML has DTDs prohibited and no resolver, and a markup guard caps the memory of a single node. History, attachments, custom icons/data and deleted objects are walked without being kept, but their protected values still advance the inner stream. KDBX 3.1, key files, Windows account keys, Twofish and Salsa20 inner streams fail closed with guidance. Errors never carry passwords, keys or decrypted XML.

Test fixtures are generated with KeePass 2.61.1 itself (`tests/fixtures/kdbx/generate-fixtures.ps1` loads KeePassLib from the locally installed `KeePass.exe` at development time only; KeePass is GPL and is neither a build, test nor runtime dependency, and none of its code is in the repository). An optional test opens databases written by Atlas with the installed KeePass.

## Decision

Do not implement the proposed KeePassXC CLI adapter. No KDBX package or executable was added to the application. A temporary official portable CLI download used for feasibility research was removed after the license requirement changed.

Prefer an in-process .NET library with permissive licensing if it passes the security evaluation. `DgNet.Keepass 0.2.0` is a concrete NuGet candidate, not yet an accepted production dependency. Avoid writing KDBX cryptography ourselves or translating GPL source into an ostensibly permissive implementation.

## Checked candidates

| Component | License evidence | Result |
| --- | --- | --- |
| KeePassXC 2.7.12, including CLI | [Versioned COPYING](https://github.com/keepassxreboot/keepassxc/blob/2.7.12/COPYING): GPL-2 or GPL-3 | Excluded by the user's policy, irrespective of possible process-boundary arguments |
| KeePassLib.Standard 2.57.1 | [NuGet metadata](https://www.nuget.org/packages/KeePassLib.Standard/2.57.1): GPL-2.0-or-later | Excluded |
| KPCLib 2.0.4 | Package LGPL metadata is contradicted by GPL-2-or-later notices in KeePassLib source files, including `PwDatabase.cs` | Not approved; metadata alone does not establish LGPL coverage of the code |
| DgNet.Keepass 0.2.0 | Downloaded `.nuspec`: MIT; repository commit `9a4928aad73150d9f5a1a0ce735c86b77be6f094`, root LICENSE: MIT, copyright 2026 pidamg | Candidate for a bounded evaluation; not a security endorsement |
| BouncyCastle.Cryptography 2.6.2 | Actual `.nuspec` and packaged LICENSE.md: MIT; no declared NuGet dependencies for its target frameworks | Sole declared dependency of DgNet.Keepass; retain applicable notices if adopted |
| kdbxweb | [Repository LICENSE](https://github.com/keeweb/kdbxweb/blob/master/LICENSE): MIT | Alternative outside .NET; adds a server-side JS runtime boundary and requires a separately supplied Argon2 implementation. Full concrete dependency/license/security evaluation still needed |

[DgNet repository](https://github.com/pidamg/DgNet.Keepass), [NuGet candidate](https://www.nuget.org/packages/DgNet.Keepass/0.2.0), [Bouncy Castle licensing](https://www.bouncycastle.org/about/license/), [kdbxweb usage and Argon2 requirements](https://github.com/keeweb/kdbxweb).

MIT permits commercial use and redistribution under its notice conditions; it does not require publishing the surrounding application's source. This does not establish provenance of every line, compatibility with every deployment, or completion of third-party notice obligations. The DgNet repository calls KeePassXC a reference implementation; this statement alone proves neither copied code nor independent provenance. Do not claim a provenance audit has been completed.

## Security observations on DgNet 0.2.0

Read the source matching the package's repository commit, rather than only its README:

- `src/Kdbx/KdbxReader.cs`, `ReadV4` and `Decompress`: GZip output is copied to a growing MemoryStream with no decoded-size cap. Bounding the encrypted file alone cannot bound decompressed memory.
- `ReadFrom`: key derivation runs before the KDBX 4 header SHA/HMAC checks. `src/Crypto/Argon2Kdf.cs` forwards file-derived memory/iterations/parallelism to Bouncy Castle without an application cost ceiling. A server wrapper must reject excessive parameters before derivation; request cancellation alone is not a resource limit.
- These are static code observations, not a demonstrated exploit, independent audit or exhaustive review. Interoperability, corruption/replay behavior, cancellation, key lifecycle and concurrency were not tested in this licensing pass.
- No public reader hook enforces a bound on decoded XML/attachments. Compressed attachments, sparse binary IDs and recursive groups/history add further allocation paths. The existing reader cannot be made safe merely by passing a capped encrypted stream. A disposable codec worker with OS-enforced memory/CPU limits, a deadline and capped IPC is the viable evaluation direction; no worker is implemented yet. Preserve existing databases read-only until round-trip fidelity is established.

Before adoption: inspect provenance and notices; pin and audit the exact resolved dependency graph; verify KDBX 3/4 fixtures from an independent implementation, password/key-file behavior and authenticated corruption rejection; bound headers, blocks, KDF cost, decompression and collections; test failure cleanup and ensure plaintext/master passwords never reach logs, content projections or scratch files. If meeting this bar requires a substantial maintained fork, reassess the library instead of silently accepting that obligation.

## Existing LGPL dependencies

The current server project already references these packages; they were not added by the vault work:

| Existing dependency | Evidence from installed package metadata | Required disposition |
| --- | --- | --- |
| SMBLibrary 1.5.7.1 | LGPL-3.0-or-later | Retain; verify replaceable DLL distribution, notices and corresponding library source. Commercial licensing is optional, not required merely because Atlas is commercial |
| NHibernate 5.6.1 | LGPL-2.1-only | Retain; apply the LGPL distribution checks below. No license-driven persistence rewrite is required |
| Remotion.Linq.EagerFetching 2.2.0 (via NHibernate) | Legacy `licenseUrl` points to LGPL-2.1 | Include this transitive library in notices, source availability and replaceability checks |

An initial metadata scan of the current server `obj/project.assets.json` checked 80 resolved NuGet packages. Several older packages have only license URLs or no SPDX expression; these remain unverified. This was not a full review of vendored code, frontend packages, containers, native libraries or distribution artifacts. Local research evidence is in ignored `.tmp/vault-license-audit/`, including the downloaded candidate packages, matching source and `atlas-nuget-metadata.json`.

LGPL permits a separate application to retain its own license when the applicable conditions are met. Dynamic linking is a convenient compliance path, not the only allowed path and not a complete compliance check by itself. See [LGPL 2.1 sections 4 and 6](https://licenses.nuget.org/LGPL-2.1-only) and [LGPL 3 section 4, plus incorporated GPL 3 distribution terms](https://licenses.nuget.org/LGPL-3.0-or-later).

For distributed Atlas binaries/containers: preserve the ability to replace a library with an interface-compatible modified version; supply required copyright/use notices and license texts (LGPL 3 also requires GPL 3 text); provide corresponding source for the distributed LGPL library, including any modifications, through a compliant distribution mechanism; ensure application terms allow the modification/reverse engineering required for debugging library modifications. This concerns library sources, not an automatic requirement to publish the independent Atlas application. Distribution of changes to a library must preserve its license obligations. Ordinary internal use or SaaS access without delivery of software copies differs from distributing on-premises binaries or images.

Implementation evidence: the `Dockerfile` and `tests/browser/e2e-podman.ps1` use ordinary `dotnet publish --configuration Release --output ...` (framework-dependent, `UseAppHost=false`); checked project/build settings do not enable single-file publishing, NativeAOT or assembly merging. Existing Debug and Release output contains separate `NHibernate.dll` and `SMBLibrary.dll`. This supports the dynamic-assembly approach. A replacement-DLL test and audit of the final distribution's notices/source delivery have not yet been performed. Do not treat NuGet usage alone as proof of compliance. Any future single-file/AOT/merging change requires re-evaluation.

## Effect on implementation

Keep the approved UX requirements: existing/new file, private local server directory/remote folder, generated/custom master password. Reuse of the existing SMB client and NHibernate is acceptable under the clarified policy, with distribution compliance tracked as a release requirement. Production vault enrollment/release remains disabled pending the security and integration checkpoints. The KDBX library still needs independent license and security evaluation; acceptance of LGPL does not approve GPL KeePassLib or the GPL CLI. The application includes weak-copyleft libraries and must not be advertised as copyleft-free.
