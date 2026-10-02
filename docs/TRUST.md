# Trust & integrations (roadmap items 14, 15, 16)

Code: `src/Raffaello.Core/Trust`, `src/Raffaello.Core/Integrations`, `src/Raffaello.Core/Portal`, `src/Raffaello.Server/Trust`,
`src/Raffaello.Server/Portal` (+ `Portal/web`), `tools/cad`, app page `Views/Trust` (TRUST & INTEGRATIONS), CLI `TrustCommands.cs`.
Tests: `tests/Raffaello.Core.Tests/Trust*.cs`, `tests/Raffaello.Server.Tests/Trust*.cs`.

## 14a. Tamper-evident audit log (hash chain)

`Hash(n) = SHA-256(Hash(n-1) + "\n" + canonical(row n))`, starting from `GENESIS`. The canonical text length-prefixes every
field (id, time to the millisecond, user, machine, table, row id, action, summary, changes, old / new JSON) - see `AuditHash`.

- **Desktop data file (SQLite)**: the row is sealed inside the same write transaction (`SqliteAuditChain.Seal`, called from
  `Db.Audit` / `SideStore.Audit`). Columns `ChainSeq`, `PrevHash`, `Hash` are added to AuditLog; table `AuditChainHead` keeps the
  sealed end. Rows written by an older version are sealed by the next write. A data reset (Settings) continues the chain from
  the old head (`BaseSeq`) and records when.
- **Server (PostgreSQL)**: computed authoritatively by the server. Writers only insert audit rows; the sealer (every 3 s and before
  each verification) seals committed rows in id order under one advisory lock - no global lock inside write transactions, so no
  deadlock with the ledger-key locks. The chain order is `ChainSeq`. The head is appended hourly (and on shutdown / on demand) to
  `<backup folder>/audit-anchors.log`, outside the database.
- **Verification** (`AuditChainVerifier`) reports EDITED (content no longer matches its seal), DELETED (missing sequence numbers),
  LINK BROKEN (a row re-sealed by a forger: the next row still points at the old hash), TAIL DELETED / HEAD MISMATCH, and ANCHOR
  MISMATCH: every PC remembers the heads it verified (`%APPDATA%\Raffaello\audit-anchors.json`) and the server keeps its anchor file,
  so even a complete, consistent rewrite of the chain is caught by anyone who verified earlier.
- **Where**: app TRUST & INTEGRATIONS > INTEGRITY > VERIFY INTEGRITY (server mode: on the server, or *independent* = download the
  sealed rows and recompute every hash on the PC); CLI `raffaello-cli verify-audit [--db FILE | --server URL --token T [--independent]]`
  (exit 3 when tampered); API `GET/POST /api/v1/trust/audit/verify`, `GET /head`, `GET /chain?afterSeq=&take=`, `POST /seal`, `POST /anchor` (ADMIN).
- Limits: a row edited in the seconds before the server sealer runs is sealed as edited (window <= 3 s). Someone with full
  database **and** anchor-file access who rewrites everything is only caught by the PCs' anchors. After `Raffaello.Server.exe restore`
  the anchor file (newer than the backup) reports the lost tail as TAIL DELETED - that is true; keep the file as evidence and
  rename it to start fresh.

## 14b. Approvals with personal signatures

- Each user gets an **ECDSA P-256** key pair at their first signature (`SigningKeyStore`): private key in
  `%APPDATA%\Raffaello\keys\<user>.raffaello-key.json`, protected with **Windows DPAPI (current user)**; on non-Windows / CLI a
  passphrase protector (PBKDF2 + AES-GCM). The public key is registered (`UserSigningKeys` table, server or data file); a new key
  withdraws the previous one (old signatures stay valid).
- A signature (`RecordSignatures`, append-only) covers the **canonical JSON** of the record (invoice revision header + lines, or
  variation + lines; workflow fields such as status, Aconex no., submission dates and package bookkeeping are excluded so submitting
  an approved invoice does not "change" it) and, for kind PACKAGE, the **package ZIP SHA-256**. The signed text (statement) is stored
  verbatim: kind, record, title, purpose (PREPARED / CHECKED / APPROVED), payload SHA-256, package SHA-256, signer, name, role, key
  fingerprint, UTC time.
- **Server rules** (`TrustGuard`): you can only register your own key and sign as yourself, in your real role (the server sets the
  key's role), with the permission the purpose needs (APPROVED = APPROVE_INVOICES, CHECKED = CHECK_INVOICES, PREPARED =
  PREPARE_INVOICES / EDIT_DATA); the server recomputes the payload from the database and refuses a signature over anything else;
  a package signature must match the package recorded on the revision; signatures and keys are never changed or deleted.
- **Verification**: VALID ("this package was approved by X (REVIEWER) at T and has not changed since"), CHANGED (genuine
  signature, content or package changed), INVALID (forged / edited row / withdrawn key), UNKNOWN KEY. App: SIGNATURES tab
  (VERIFY, VERIFY PACKAGE ZIP - which approvals cover these exact bytes); CLI `verify-signatures`, `verify-package`, `sign`;
  API `GET /api/v1/trust/signatures/verify?table=SubInvoices&id=`.
- **Signature block in PDFs**: SIGNED PDF COPY appends an "APPROVALS & INTEGRITY" page (one block per signature: purpose,
  name, role, local and UTC time, short key fingerprint, verdict, content and package SHA-256, audit-chain status) to any exported
  PDF (invoice, package index, VO submission). The existing exporters are not changed; the signed copy is a separate file.

## 14c. Embedded (PAdES-style) PDF signature

Optional on the signed copy: an incremental update with a `/Sig` field (`ETSI.CAdES.detached`, SHA-256, ESS
signing-certificate-v2), a visible widget on the approvals page and an AcroForm; earlier signatures stay valid.
`PdfSignatureVerifier` / `raffaello-cli verify-pdf` checks the CMS and that the signature covers the whole file.

**Legal note**: by default the certificate is *self-issued* from the user's Raffaello key (subject = name, role, "MOBCO Raffaello
(self-issued)"). Adobe Reader shows it as signed but the identity as not verified. It proves integrity and who held the key -
it is an internal approval, **not** a qualified / legally recognised electronic signature. For legal-grade signatures MOBCO needs a
signing certificate issued by an accredited CA (in KSA a provider licensed under the Electronic Transactions Law); `PdfCmsSigner.Sign`
accepts any `X509Certificate2` with a private key (PFX or Windows certificate store, RSA or ECDSA) unchanged - only the certificate
source has to be wired to the company certificate. Add an RFC 3161 timestamp from the CA for long-term validation.

## 15a. E-Promise (ERP) export

Certified (head-office approved / locked) subcontractor invoice revision -> E-Promise import file (`EPromiseExporter`). The owner
BOQ code `B6-01-01-00-6-26-V-5` is split into Bill / Section / Page / Rev / Item; WBS, Activity (cost code), budget resource code /
name and job come from the invoice line first, then the E-Promise budget list imported from the 'E promise - Resource' sheet.
Default columns mirror that sheet: Job, Bill, Section, Page, Rev, Item, BOQ No, Description, WBS, Activity, Budget Resource Code,
Budget Resource, then Unit, Qty, Rate, Amount, Contract No, Subcontractor, Invoice Ref. Qty x Rate = Amount (= qty x rate x stage %).
Everything is configurable in `%APPDATA%\Raffaello\epromise-export.json`: header text / field / order / constants, current or
cumulative quantity, quantity x stage %, one row per BOQ code + cost code + resource, CSV delimiter / BOM, sheet name. Issues (no BOQ
code, code not in the budget list, missing cost code / resource, no job) are listed. Excel (house header #A6A6A6, codes as text,
no title rows) or CSV. App: E-PROMISE tab; CLI `epromise-export`. **Not verified**: the real ERP import template - send one
exported file to finance and adjust the JSON mapping.

## 15b. Aconex REST API

`AconexApiClient` implements the existing `IAconexClient` (workflow lookup, register search, download), so the status board,
download queue and invoice links work unchanged. Endpoints (Oracle Aconex API, XML): `GET /api/projects`,
`GET /api/projects/{id}/register?search_query=&return_fields=&search_type=PAGED&page_number=&page_size=`,
`GET /api/projects/{id}/register/{documentId}/file`, `GET /api/projects/{id}/workflows/search?search_query=workflow_number:"WF-.."`.
Auth: OAuth client credentials (token URL configurable), bearer token or legacy basic; `X-Application-Key`. Retries on 429 / 5xx,
paging, document ids remembered (or re-found by number). Lookups save the raw XML and a PNG evidence card of the steps instead of
browser screenshots. Config `%APPDATA%\Raffaello\aconex-api.json` (paths and search field names are templates); secret stored with
DPAPI (TRUST > ACONEX API > STORE SECRET) or env `RAFFAELLO_ACONEX_SECRET`. When `Enabled` is true the app's Aconex automation
uses the API instead of the browser. Tested against a mock API server; **not verified** against Oracle - MOBCO needs API access
(integration registration with Oracle), then check the project id, the search field names and the date field.

## 15c. CAD exchange

See `docs/CAD_EXCHANGE.md` (schema) and `tools/cad/README.md` (AutoCAD / Revit add-ins, Dynamo script, block map, build).

## 16. Subcontractor portal

Served by Raffaello.Server at **`/portal`** (static page from the assembly; English / Arabic with RTL; phone-friendly; camera
upload) with its API under `/api/v1/portal`.

- **Accounts**: separate table `PortalAccounts` (BCrypt, 10+ chars with letters and digits), role SUBCONTRACTOR, one company each.
  Created by an ADMIN (TRUST > PORTAL INBOX or `POST /api/v1/portal-admin/accounts`). Portal tokens (`p_...`, 12 h) live in
  `PortalSessions`; the internal API never accepts them and the portal never accepts app tokens.
- **Isolation**: the company comes from the token only; every answer is filtered by it on the server (claims, invoices, remaining,
  submissions, files, messages). Another company's ids answer 404. Remaining = PROJECT QTY minus all claims on the key
  (other subcontractors are never named). Company settings (`PortalCompanySettings`: building, room scope wildcards) are set by the QS.
- **Flow**: download the numbered statement template (rooms in scope, remaining as comments) -> fill -> send with marked-up PDFs and
  photos -> QS inbox in the app (live) -> OPEN FILES / PREVIEW STATEMENT (existing site statement importer: duplicates and
  over-remaining caught) -> POST TO LEDGER or REJECT with a reason -> the subcontractor sees SUBMITTED / UNDER REVIEW / IMPORTED /
  REJECTED + reason and the QS's messages. A statement of another company is refused at upload.
- **Security**: extension AND content signature (xlsx / pdf / jpg / png / heic), 25 MB per file, 100 MB and 30 files per submission,
  10 accepted submissions per hour per account, 20 per company per day, 120 requests per minute, 30 messages per hour, sign-in lockout
  (shared LoginThrottle), strict CSP / no-frame / nosniff headers, every action audited (user `portal:<name>`) and hash-chained;
  submissions and messages are append-only (`PortalGuard`). Settings: `appsettings.json` section `Raffaello:Portal`.
- Exposing the portal to subcontractors outside the LAN needs IT: HTTPS (docs/SERVER.md) and a reverse proxy / firewall rule
  publishing only `/portal` and `/api/v1/portal`.
