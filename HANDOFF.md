# Handoff — PDF serving + real HIS connection

**Written 2026-09-03**, at the end of a session on the dev machine (`PC-128794`), immediately before
going to the hospital to deploy. Everything below was verified on `PC-128794` against a local copy of
the Bekaa data. Nothing has been tested against the hospital server (`192.168.10.239`) — it is not
reachable from the dev network.

Read this alongside [README.md](README.md) (project overview) and
[docs/MedVault-HIS-API-Specification.md](docs/MedVault-HIS-API-Specification.md) (the contract).

---

## 1. What this session set out to do

Three things:

1. Point the API at the real SQL Server instead of the mock/MedVault captures.
2. Serve the FoxPro-generated result PDFs. FoxPro 9 writes every report into `C:\ExcelH`, flat, no
   subfolders. ASP.NET Core serves that folder statically at the `/ResultPDF` URL path.
3. Populate `pdf_path` on `/api/mobile/request` by looking for the matching file on disk, or `null`
   when FoxPro has not written it yet.

All three are done and working locally.

## 2. Code changes

Four files. There is no git here, so this list *is* the diff.

### [src/MedVault.Api/Files/PdfFileResolver.cs](src/MedVault.Api/Files/PdfFileResolver.cs)

Added alongside the existing `Resolve` (untouched):

- `const string UrlPrefix = "ResultPDF"` — the single source of truth for both the URL mount point and
  the prefix written into `pdf_path`.
- `RootDirectory` — `Files:Root`, or null when unset/missing.
- `PathForRequest(requestNb)` — globs `<request_nb>*.pdf` in the root (top directory only), returns
  `"ResultPDF/<filename>.pdf"` for the newest by write time, else null.
- `CreateFileProvider()` — the `PhysicalFileProvider` `Program.cs` mounts, or null.

Three judgment calls inside `PathForRequest` that the user approved but which are worth knowing:

- **Prefix match, not contains.** The user offered `*<request_nb>*.pdf`; a contains-match would let
  request `26L00618` pick up `26L006183`'s report. Every file in `C:\ExcelH` starts with its request
  number anyway.
- **Zero-length files are skipped.** `C:\ExcelH` contains several 0-byte PDFs (FoxPro creates the file
  before writing it). A 0-byte file opens as a corrupt document in the mobile app, so it reads as
  "not ready yet" (null) instead.
- **The request number is validated before it enters the glob** (ASCII alphanumeric, `-`, `_` only),
  so a crafted request number cannot carry wildcards or path separators.

### [src/MedVault.Api/Program.cs](src/MedVault.Api/Program.cs)

`UseStaticFiles` with a `PhysicalFileProvider` over `Files:Root`, mounted at `/ResultPDF`. Sits after
the exception handler and before the rate limiter. Registration is **guarded on the directory
existing** — `PhysicalFileProvider` throws on a missing folder and would stop the app from booting; if
it is absent you get a startup warning and null `pdf_path` values instead of a dead service.
`ServeUnknownFileTypes` stays false, so non-PDF files FoxPro drops in the folder are not served.

### [src/MedVault.Api/Endpoints/MobileEndpoints.cs](src/MedVault.Api/Endpoints/MobileEndpoints.cs)

`GetRequestsAsync` takes a `PdfFileResolver` and projects each `RequestItem` through it. Only a **null**
`pdf_path` is filled in, so a future HIS column would win. Done in the endpoint rather than in each
repository so mock and SQL behave identically, and so the disk lookup stays out of the code path
`GetResultsAsync` uses for its ownership check.

`status` was left alone — still hardcoded `'V'` in the `GetRequests` query, as the user asked.

### [src/MedVault.Api/appsettings.json](src/MedVault.Api/appsettings.json) — dev only

`His:Provider` → `sql`, `Files:Root` → `C:\ExcelH`, and a connection string for `PC-128794`.
`Facility:DisplayName` deliberately **stays `MedVault Demo Hospital`** here: `ContractTests.cs:54` pins
`Lab == "MedVault Demo Hospital"` when replaying the MedVault captures, and renaming it breaks the suite.

## 3. Server configuration

[appsettings-for-server.json](appsettings-for-server.json) is the server template and is ready:

| Key | Value |
|---|---|
| `Facility:DisplayName` | `Bekaa Hospital` (was already set — only the dev file says Verdun) |
| `His:Provider` | `sql` |
| `ConnectionStrings:His` | `192.168.10.239`, database `Dpersonel`, `sa` — password is in the file |
| `Files:Root` | `C:\ExcelH` |
| `Auth:MaxLinkedPatients` | `0` — **check disabled**, see open item (e) |

The app listens on **port 5000** (was 5080 — changed everywhere on 2026-09-03).

The hardcoded test number was scrubbed from the whole repo the same day: `70143335` → `03191176` →
`00000000`. `00000000` is a deliberately impossible number so the mock seed can never collide with a
real patient's. It appears in `MockHisRepository.SeedMobile`, the seed filename
`Data/Seed/requests-00000000.json`, the tests, the README, `MedVault.http` and the Postman files.
Note this only scrubbed the *repo* — `03191176` is still a live number in the hospital database.

**`publish-new/`** is a clean Release build with that config already copied in as `appsettings.json`.
It boots, serves `/health`, and serves `/ResultPDF` correctly. Copy the whole folder to the server.

**The old `publish/` folder is broken — delete it, do not deploy it.** It is from 2 Aug, still has
`Provider: mock`, and critically `"System.Globalization.Invariant": true`, which predates the
`InvariantGlobalization=false` fix in the csproj. `Microsoft.Data.SqlClient` throws the moment it
opens a connection under invariant mode, so that build cannot reach SQL at all.

The server needs the **ASP.NET Core 10 Hosting Bundle** — this is a framework-dependent build.

Quick check on the server:

```
C:\MedVault\MedVault.Api.exe --urls "http://0.0.0.0:5000"
```

then `curl http://<server-ip>:5000/health` from another machine on the LAN.

For unattended running, IIS + the Hosting Bundle was the recommendation (auto-start, recycling, W3C
logging, and hospital IT will know it). Note the app has **no `UseWindowsService()` call**, so
`sc.exe create` on the bare exe will fail to start — adding it is a one-line change plus a package
reference if a Windows service is preferred.

## 4. Verified on `PC-128794`

- `dotnet build` clean, `dotnet test` 54/54 passing (7 HIS integration tests skip without
  `MEDVAULT_HIS_CONNECTION`).
- Login as `71818711` → `/api/mobile/request` returned
  `"pdf_path":"ResultPDF/26L006183_report.pdf"` — correctly choosing the real report over the 0-byte
  `26L006183_lab results.pdf` for the same visit.
- `GET /ResultPDF/26L006183_report.pdf` → `200 application/pdf`, 1210 bytes.
- `GET /ResultPDF/../appsettings.json` → 404.
- The published server build boots and reports `Bekaa Hospital`.

## 5. Open items — start here

### a. `/api/mobile/results` returned 503 — FIXED 2026-09-07

**The hospital server has only `DATA1` and `DPERSONEL`. There is no `DATAPRIX`.**

The configured `GetResults` joined `[DATAPRIX].dbo.RLAB1` and `[DATAPRIX].dbo.NOMLAB` for test names,
units, display order and category names, so every call threw `Invalid object name` and the exception
handler reported it as a 503. `/login` and `/request` kept working because the config actually deployed
was the dev `appsettings.json`, whose simpler `GetRequests` never touched `DATAPRIX`.

Both queries in `appsettings-for-server.json` are now DATAPRIX-free and were verified end to end
against `PC-128794`, which has the same two databases and no `DATAPRIX`:

- `GetResults` reads `DATA1.dbo.TRLAB` alone. Returns the full contract column set with real test
  names and values (`RBC 4.83`, `Hemoglobin 14.5`).
- `GetRequests` keeps the real `V`/`P` status derived from `TRLAB.RETAT` and the `report_datetime`
  from `MAX(TRLAB.DATEV)` — the old server version's `STRING_AGG` category aggregate is gone, which
  also removes a second landmine: `STRING_AGG` needs compatibility level 110+, and both databases on
  `PC-128794` are at **100**.

What is lost until a catalogue exists:

| Field | Now |
|---|---|
| `unit` | always null — `TRLAB` has no unit column |
| `cat_name` (result row) | the category **code** (`01`), not `HEMATOLOGY` |
| `cat_name` (request row) | null — the raw code is meaningless to a patient |
| `test_name` | `TRLAB.DESCRIP` rather than the catalogue description |
| `position` | `TRLAB.RANG` is null in this data, so rows order by `LIGNE` |

`ref_range` and `result_status` are null either way — they come from the reference-range catalogue
file, never from the HIS.

Also note `SqlHisRepository.cs:182` fills a blank `group_name` from `cat_name`, so `group_name` now
shows the raw code too. Pre-existing behaviour, not caused by this change.

**To apply:** copy `appsettings-for-server.json` over `C:\MedVault\appsettings.json`. No redeploy and
no restart — `SqlHisRepository` reads `His:Sql:*` from configuration on every call, and ASP.NET Core
reloads the file on change. Restore the DATAPRIX versions from this file's history if that database
ever appears.

### b. Is `C:\ExcelH` on the machine the API runs on?

FoxPro writes that folder locally on whichever PC generates the reports. If the API host is a
different machine, `Files:Root` needs a UNC share (`\\FOXPRO-PC\ExcelH`) and the service account needs
read access. Otherwise every `pdf_path` comes back null and `/ResultPDF` serves nothing. This is
called out in a comment in the server config.

### c. `/ResultPDF` has no authentication (accepted risk, revisit)

The static mount serves every PDF in `C:\ExcelH` to anyone who can reach the port — no token, no
ownership check. This is exactly what `PdfFileResolver.Resolve` and the `/api/mobile/file` route exist
to prevent, and visit ids are guessable. The user asked for the plain static mount and it was fine on
`localhost:5000`; on the hospital LAN it is real exposure. Warnings are in the code at both the
registration site in `Program.cs` and the class doc in `PdfFileResolver.cs`. The fix is either to drop
`/ResultPDF` in favour of the already-working `/api/mobile/file`, or to gate the mount with a
middleware that checks the token and ownership first.

### d. PDF filenames — RESOLVED, but the app was building URLs by hand

The real `C:\ExcelH` on the hospital server holds **966 files, 570 MB**, named like
`26L007506_100.PDF` — uppercase extension, and a numeric suffix that is *usually* `_100` but sometimes
`_101` or `_107`. Those look like lab category codes (`01` haematology, `07` urine), with `_100` as the
combined report; a visit can have several.

`PathForRequest` globs `<request_nb>*.pdf`, which is case-insensitive on Windows, so every variant
matches. **Confirmed working on the hospital server by the user on 2026-09-08.**

The 404s in the ngrok log (`26L007429_101.pdf` etc.) were the mobile app **constructing** the URL and
hardcoding `_101`. The real file is `26L007429_100.PDF`. The app must use the `pdf_path` value the API
returns, verbatim, and must not build filenames itself.

`RequestItem` now also carries **`pdf_paths`** — every matching report, `_100` first, always present and
empty rather than null when there is nothing. `pdf_path` keeps returning a single value (the `_100`
report when one exists) so existing clients are unaffected.

Earlier note, still true: a filename containing a space is left raw rather than URL-encoded, because
encoding would break the string comparison in `OwnsFileAsync` that `/api/mobile/file` relies on. The
app should percent-encode when building the request.

### e. `Auth:MaxLinkedPatients` is disabled on the server (0 = unlimited) — READ THIS

**Resolved 2026-09-03 at the hospital, but it traded a real safeguard away.**

Symptom: `03191176` returned `Invalid credentials` while every other number logged in fine. It was not
a password problem and not the test-number rename. The number is on **29 patient records** in
`PERSONEL`, `Auth:MaxLinkedPatients` was 12, and `IsImplausibleFamilyGroup` refuses anything over the
limit using the *same* generic message as a bad login (deliberately — which is why it was confusing).
The server log said so directly:

```
Refused login for a number linked to 29 patients - looks like a placeholder, not a family
```

Fix applied: `Auth:MaxLinkedPatients` → `0` in the server config only. Verified — that number now logs
in and the warning is gone.

**What this costs.** The limit existed because `Auth:RequirePassword` is `false`, so a mobile number
alone is the key to every record behind it. The Bekaa HIS has one number on **11,989 patient records**
and several others above 40 — default values, the hospital's own number, a clerk's number typed onto
every walk-in. With the check off, whoever holds any of those reads all of those patients' results.
`03191176` on 29 records is very likely a placeholder of exactly this kind rather than a 29-person
family.

Better options when there is time, in order: issue passwords and set `Auth:RequirePassword=true`
(removes the whole problem); or restore a limit and exempt the specific numbers staff need to test
with; or set the limit just above the largest genuine family group. Leaving it at `0` permanently is
the thing to avoid.

### f. Malformed requests returned a bare 400 in Production only — FIXED

The second half of the hospital login confusion. `Invalid credentials` was
`Auth:MaxLinkedPatients` (item e); `Bad request` was this, and the two were unrelated.

`RouteHandlerOptions.ThrowOnBadRequest` defaults to **true in Development and false everywhere else**.
Left at the default, minimal-API body binding rejected an unparseable login with a bare 400 and an
**empty body**, so the exception handler in `Program.cs` never ran and the caller got nothing to
distinguish "your JSON is broken" from "your login was rejected". `WebApplicationFactory` hosts tests
as Development, so `A_malformed_body_is_400_with_the_error_contract_not_503` passed the whole time
while the deployed server did the opposite.

Fix: `builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true)` in
`Program.cs`, so every environment returns the same `ErrorResponse`. Verified on the published
Release build hosted as Production — a truncated body, an unquoted number and a trailing comma now all
return `{"success":false,"message":"Bad request"}`.

Guarded by `ContractTests.A_malformed_body_keeps_the_error_contract_in_production`, which uses a new
`ProductionApiFactory`. Confirmed it fails on all three inputs when the fix is removed. Suite is now
57 passing / 7 skipped.

The trigger was a hand-edited Postman body (the password line removed, leaving invalid JSON). Worth
knowing: **the password is ignored entirely** while `Auth:RequirePassword` is `false`, so
`{"username":"03191176","type":"P"}` is the whole request. And `username` must be a quoted string —
`03191176` unquoted is not valid JSON, because JSON numbers may not have a leading zero, so that
particular number breaks in any client that treats it as numeric while `71818711` appears to work.

A missing `Content-Type: application/json` is a **415**, not a 400 — a third distinct symptom.

### g. `ref_range`, `unit`, `group_name` — ALL RESOLVED 2026-09-09, DATAPRIX arrived

`DATAPRIX` was attached to the SQL Server and **the reference ranges were in it after all**. The old
config note claiming ranges existed nowhere was written before that database was available; it was
wrong and has been corrected in place.

**`DATAPRIX.dbo.NLAB1` is the reference-range table** — 1,942 rows across 900 test codes:

| Column | Meaning |
|---|---|
| `CODE` | test code, joins to `TRLAB.CODE` |
| `VALUEL` / `VALUEH` | the low and high bounds |
| `SEXE` | `M`, `F`, or `(` meaning "any" — same encoding as `PERSONEL.SEX` |
| `AGEL` / `AGEH` | age band in years (253 rows carry a real band; the rest are 0–200) |

`GetResults` now picks one row per test for **that patient's sex and age**, preferring an exact sex
match over "any" and the narrowest matching age band. Verified: a female patient gets Hemoglobin
`12-16`, a male gets `14-18`.

It also formats `ref_range` as `<div>13.4-17.6</div>` to match the MedVault capture byte for byte, and
computes `result_status` (`H`/`L`/`N`) and `sign` (`High`/`Low`) from the same bounds. High detection
confirmed on real rows — TSH 39.634 against 0–20, Triglycerides 301 against 0–150.

`unit`, `cat_name` and `group_name` come from `RLAB1` + `NOMLAB` as originally designed, so
`group_name` is now `HEMATOLOGY` rather than the raw code `01`.

Two further fixes made at the same time:

- **`result_status` must be selected as `NULL`, never as the literal `'N'`.** `ReferenceRanges.Annotate`
  only computes a status when the value arrives empty, so a hardcoded `'N'` would have made every
  result display as normal — including genuinely high ones — the moment ranges existed. This was a live
  bug in both the original and the DATAPRIX-free query.
- **`birth_date` on the request row** now falls back to `PERSONEL.NAISSANCE`; `MALADE.NAISSANCE` is
  frequently empty.

`GetRequests` also has category names back (`cat_name`), built with **`FOR XML PATH`, not
`STRING_AGG`** — `STRING_AGG` requires database compatibility level 110+ and these databases are at
**100**, where it fails to parse. That was a latent bug in the original server config.

All of it is **config only** — `appsettings-for-server.json`. No rebuild. Copy it over
`appsettings.json` on the server; the queries are re-read per call, so no restart is needed for these.

`docs/reference-ranges-for-lab.txt` and the hand-maintained catalogue file are now redundant for the
tests present in `NLAB1`. `ReferenceRanges` still runs and still fills gaps for any test the database
has no row for, so the two coexist — it only writes `ref_range`/`result_status` when they arrive empty.

### g-old. Original note (superseded — kept because the search method is still useful)

These two drive the reference range and the High/Low gauge in the app. Both are null today, and the
app developer reported it as a bug. It is not a bug in the API.

**Searched, not assumed.** On 2026-09-08 every table in every database on `PC-128794` was scanned for
columns named like a range (`NORM`, `MINI`, `MAXI`, `LOW`, `HIGH`, `RANG`, `REFER`, `VAL`). In `DATA1`
and `DPERSONEL` the only hits were:

| Column | What it really holds |
|---|---|
| `TRLAB.REFER` | the **analyser name** — Sysmex (73,977 rows), AU480 (34,340), Yumizen, Olympus, ACL350, VITEK |
| `TRLAB.RANG` | display order, integer, null throughout this data |
| `TRLAB.ABNORMAL` | **empty in all 217,239 rows** |
| `MALADE.REFER`, `TRANSP.REFER` | referring doctor / transfer fields, unrelated |

Everything else was in `master`, `msdb`, or the accounting database `Data2024n`. **This search could
not cover `DATAPRIX`** — that database is not on `PC-128794`.

The user reported on 2026-09-08 that the ranges **are** stored in `DATAPRIX` and is retrieving it. If
so, the note in the config claiming ranges exist nowhere was based on an incomplete look.

**Two fallbacks, in order of preference, if DATAPRIX does not have them:**

1. The lab's report PDFs in `C:\ExcelH` may already print ranges — those are authoritative and cost
   nothing to check.
2. Ask the lab per analyser. `TRLAB.REFER` says exactly which machine ran each test, so the request can
   be specific: "the Sysmex haematology ranges, the AU480 chemistry ranges".

`docs/reference-ranges-for-lab.txt` is a ready-to-hand-over list of the 40 most-used tests with codes
and units, with LOW/HIGH blank for the lab to complete.

**Do not invent these values.** A wrong reference range beside a patient's result is worse than none;
the API deliberately returns null and the app hides the gauge. The laboratory director signs them off.

### h. What DATAPRIX will and will not fix

When the database is attached, swapping the queries back is **config only** — no rebuild, no restart,
because `SqlHisRepository` reads `His:Sql:*` on every call.

Restored immediately: `unit`, `cat_name`/`group_name` (`HEMATOLOGY` instead of `01`), catalogue test
names, and display ordering.

**Not restored by attaching it alone:** `ref_range` and `result_status`. The current query hardcodes
`CAST(NULL AS varchar(200)) AS ref_range` and `'N' AS result_status` — the original DATAPRIX version
did too, because its author believed the ranges were not there. Those two lines must be pointed at the
real columns once the schema is known. Run this first and work from the output:

```
sqlcmd -S 192.168.10.239 -U sa -P <pw> -d DATAPRIX -C -W -Q "SELECT t.name, c.name FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id WHERE c.name LIKE '%NORM%' OR c.name LIKE '%MIN%' OR c.name LIKE '%MAX%' OR c.name LIKE '%RANG%' OR c.name LIKE '%REF%' ORDER BY t.name"
```

Note the original `GetRequests` category aggregate was overwritten and is not recoverable from this
repo. Rewrite it **without `STRING_AGG`** — that function needs database compatibility level 110+, and
both databases on `PC-128794` are at **100**. The original would have failed there.

If ranges do come from SQL, `ReferenceRanges.Annotate` already defers to whatever the query supplies:
it only fills `ref_range`/`result_status` when they arrive empty, so the catalogue file and the
database can coexist without fighting.

### i. Smaller things

- Traffic is plain HTTP: tokens and results cross the network unencrypted. Fine on a trusted LAN,
  worth a certificate otherwise.
- `ConnectionStrings:His` is `sa` in plaintext in a config file. The API only ever needs SELECT on
  `DATA1`, `DPERSONEL` and `DATAPRIX`. Replace with a SELECT-only login and move the value to the
  `DOTNET_ConnectionStrings__His` environment variable.
- Behind IIS/ARR, set `Network:KnownProxies` or login throttling puts the whole hospital in one
  bucket. Configure IIS logging to omit `cs-uri-query`, since `tcToken` travels in the query string.
- `MobileEndpoints.cs:63` falls back to the literal `"MedVault Demo Hospital"` when
  `Facility:DisplayName` is missing, while `SqlHisRepository` falls back to `""`. Harmless while the
  key is set, but the fallback would claim the wrong hospital.

## 6. Useful commands

Find the phone number for a visit, to test a specific PDF end to end:

```
sqlcmd -S <server> -U sa -P <pw> -d Dpersonel -C -h -1 -W -Q "SET NOCOUNT ON; SELECT m.ADMI, LTRIM(RTRIM(p.TEL)) FROM [DATA1].dbo.MALADE m JOIN [DPERSONEL].dbo.PERSONEL p ON p.ADMI = m.ADMIP WHERE m.ADMI = '26L006183';"
```

Log in and list requests (`Auth:RequirePassword` is false — the mobile number alone logs in):

```
curl -s -X POST http://localhost:5000/api/mobile/login -H "Content-Type: application/json" -d "{\"username\":\"71818711\",\"type\":\"P\"}"
curl -s "http://localhost:5000/api/mobile/request?tcToken=<token>"
```

Watch out for stale processes on port 5000 — a `127.0.0.1` binding wins over `0.0.0.0` for loopback
requests, which caused one confusing test result this session (an old dev build answered instead of
the new one, reporting the wrong facility name). `tasklist | grep -i medvault` before trusting output.
