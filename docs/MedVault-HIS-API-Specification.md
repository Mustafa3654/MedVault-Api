# MedVault ↔ HIS Integration — API Specification

**Version:** 0.1 (draft for HIS vendor review)
**Date:** 2026-08-02
**Audience:** HIS development team (FoxPro + SQL Server), MedVault mobile team
**Status:** Sections marked **[CONFIRM]** require an answer from the HIS team before implementation starts.

---

## 1. Purpose

MedVault is a patient mobile application. It does not talk to the HIS database directly. It talks to a
single ASP.NET Web API layer that sits in front of the HIS.

```
HIS (FoxPro + SQL Database)
        │
        │   (read-only queries / views)
        ▼
  ASP.NET Web API
        │
        ├── API 1  Login
        ├── API 2  Requests List      (lab orders)
        ├── API 3  Results            (lab results per order)
        ├── API 4  Radiology List     (imaging studies)      ← new
        ├── API 5  Radiology Report   (report per study)     ← new
        └── API 6  File download      (result / report PDF)  ← new
        │
        ▼
     MedVault
```

APIs 1–3 must match the existing MedVault Demo Hospital (MedVault) contract **byte for byte**, so the existing
mobile screens work without modification. APIs 4–6 are additive and are described in section 7.

**Reference:** all MedVault examples in this document are real captures for patient mobile `00000000`,
taken from *MedVault-API-Documentation.docx*.

---

## 2. Conventions

| Item | Value |
|---|---|
| Transport | HTTPS only. Plain HTTP must be refused or redirected, never served. |
| Base URL | `https://<host>/api/mobile/…` **[CONFIRM]** host name and port |
| Content type | `application/json; charset=utf-8` on every response |
| Encoding | UTF-8. Patient names may contain Arabic characters. |
| Date/time format | `YYYY-MM-DD HH:MM:SS`, **local hospital time (Asia/Beirut), no timezone suffix** |
| Empty values | `null` for "no value", `""` for "empty string" — the two are **not** interchangeable; match the MedVault samples exactly |
| Numbers | Results are sent as **strings** (`"result": "73"`), reference ranges may contain HTML |
| HTTP status | `200` on success. See section 8 for the error contract. |

> **Do not "clean up" the MedVault payload.** Field names such as `Lab` (capital L), HTML entities in `unit`
> (`&micro;g/dL`), `<div>` wrappers in `ref_range`, escaped slashes (`\/`), and trailing spaces in
> `cat_name` (`"Hematology "`) are all reproduced by the mobile app as-is. Changing them breaks the UI.

---

## 3. API 1 — Login

Authenticates a patient (or doctor) and returns the token used by every other endpoint.

**Method:** `POST`
**Endpoint:** `/api/mobile/login`
**Headers:** `Content-Type: application/json`

### Request

```json
{
    "username": "00000000",
    "type": "P"
}
```

| Field | Description |
|---|---|
| `username` | Patient's local mobile number, no country code (e.g. `00000000`) |
| `type` | `"P"` for patient, `"D"` for doctor |

### Response — success

```json
{
    "success": true,
    "token": "de8c50c556109d4363e8faecf2f7050562ee6eb8361c67314a59f874cceaf6cf1510eec0ec46afe1",
    "user": "00000000",
    "name": "ALEX EXAMPLE",
    "Lab": "MedVault Demo Hospital"
}
```

| Field | Description |
|---|---|
| `success` | Boolean |
| `token` | The `tcToken` used in all subsequent calls |
| `user` | The mobile number that was sent |
| `name` | Patient full name |
| `Lab` | Facility display name — **note the capital `L`** |

### Response — failure

```json
{
    "success": false,
    "message": "Invalid credentials"
}
```

HTTP status `200` with `success: false` (the mobile app branches on `success`, not on the status code).
Return the **same** generic message for "number not found" and "wrong password" — a different message for
each lets anyone enumerate which mobile numbers are registered patients.

### [CONFIRM] Open questions on login

1. **Password.** The MedVault capture sends *no* password field — the mobile number alone authenticates.
   MedVault intends to issue a password per patient. If so the request becomes:

   ```json
   { "username": "00000000", "password": "<patient password>", "type": "P" }
   ```

   Decide one of:
   - **(a)** Mobile number only, as MedVault does. Simplest, and weakest — anyone who knows the number sees the results.
   - **(b)** Mobile number + password issued by the hospital at registration. **Recommended.**
   - **(c)** Mobile number + one-time SMS code. Strongest, needs an SMS gateway.

   Passwords must be stored **hashed** (bcrypt/PBKDF2/Argon2), never in a plaintext HIS column.

2. **Family members.** The MedVault list endpoint returns results for *every patient linked to that mobile
   number* — in the sample, `00000000` returns orders for `ALEX EXAMPLE`, `CASEY EXAMPLE` and
   `TAYLOR ALEX EXAMPLE`. This means the phone number is the entire access-control boundary for a whole
   family's medical records. Confirm this is intended, and confirm **which HIS column links a patient to a
   phone number**, and whether an adult family member can be un-linked on request.

3. **Token lifetime.** Not defined by MedVault. Proposed: valid **12 hours**, single-use refresh by re-login,
   invalidated server-side on logout. **[CONFIRM]**

4. **Doctor accounts (`type: "D"`).** What does a doctor see — only their own referred patients, or all?
   Scope must be defined before `"D"` is enabled at all.

---

## 4. API 2 — Requests List

Returns all lab requests (orders) for the logged-in user, including linked family members.

**Method:** `GET`
**Endpoint:** `/api/mobile/request?tcToken=<token>`

### Example request

```
GET /api/mobile/request?tcToken=de8c50c556109d4363e8faecf2f7050562ee6eb8361c67314a59f874cceaf6cf1510eec0ec46afe1
```

### Response

```json
{
    "requests": [
        {
            "request_nb": "2026001001",
            "order_datetime": "2026-03-25 11:03:00",
            "status": "V",
            "pdf_path": "ResultPDF\/2026001001\/example-report.pdf",
            "report_datetime": "2026-03-25 14:47:00",
            "patient_name": "ALEX  EXAMPLE",
            "name": "ALEX  EXAMPLE",
            "cat_name": "CHEMISTRY, HEMATOLOGY",
            "birth_date": null,
            "lab_name": "Bekaa Hospital"
        },
        {
            "request_nb": "2026001002",
            "order_datetime": "2026-05-25 22:37:00",
            "status": "P",
            "pdf_path": null,
            "report_datetime": null,
            "patient_name": "CASEY  EXAMPLE",
            "name": "CASEY  EXAMPLE",
            "cat_name": null,
            "birth_date": null,
            "lab_name": "Bekaa Hospital"
        }
    ]
}
```

| Field | Description |
|---|---|
| `request_nb` | Unique request/order number — the key for API 3 |
| `order_datetime` | `YYYY-MM-DD HH:MM:SS` |
| `status` | `"V"` once at least one result row has been released, `"P"` while the order is still pending. Derived from the result rows, never hardcoded — see below |
| `pdf_path` | Relative path to the result PDF, or `null` when not available |
| `report_datetime` | When the report was released, or `null`. Confirms a release; its absence does not deny one |
| `patient_name` | Full patient name (note: samples contain double spaces — preserve them) |
| `name` | Same value as `patient_name`. Present so the row matches the MedVault contract other consumers read |
| `cat_name` | Categories present in the order (`"CHEMISTRY, HEMATOLOGY"`), or `null` |
| `birth_date` | Date of birth, or `null` where the HIS has no such column |
| `lab_name` | Facility display name. Filled from `Facility:DisplayName` when the HIS supplies none |

**`status` is what result-ready notifications key off.** MedVault notifies the patient on the
`P` → `V` transition, so an implementation that returns `"V"` for every row notifies at order time,
before the sample has been run. The query must derive it from the released result rows — for Bekaa,
`TRLAB.RETAT = 1`, the same marker `GetResults` filters on, so `status` can never claim `V` for an
order whose results query would return nothing.

### Notes

- Sort order in the MedVault capture is **ascending by `order_datetime`**. MedVault re-sorts client-side, so
  any stable order is acceptable, but keep it consistent.
- No pagination exists in the MedVault contract. **[CONFIRM]** expected maximum orders per patient; if a
  long-standing patient can exceed ~500 orders we should agree on an optional `?from=&to=` filter
  (additive, ignored by the current app).
- `pdf_path` is relative — see section 7.3, which defines what it is relative to. The MedVault document does
  not specify this and it must be nailed down.

---

## 5. API 3 — Results (per request)

Returns the detailed test results for one request number.

**Method:** `GET`
**Endpoint:** `/api/mobile/results?requestnb=<request_nb>&tcToken=<token>`

### Example request

```
GET /api/mobile/results?requestnb=2026001001&tcToken=de8c50c5561…
```

### Response shape

```json
{ "request_nb": "2026001001", "results": [ /* one object per test */ ] }
```

Every result object must contain **all** of the following fields, even when the value is `null` or `""`:

```json
{
    "name": "ALEX EXAMPLE",
    "birthdate": "1990-01-01",
    "id": 1857,
    "test_name": "Iron",
    "is_title": "N",
    "group_name": "Other",
    "group_instruction": "",
    "group_clinical_remark": "",
    "unit": "&micro;g\/dL",
    "ref_range": "<div>70-180<\/div>",
    "test_type": null,
    "test_id": 378,
    "is_valid": "Y",
    "category_num": 3,
    "result": "73",
    "result_txt": "",
    "sign": "",
    "position": null,
    "is_main": "N",
    "prev_result_val": null,
    "prev_order_id": "",
    "field_num": "115",
    "clinical_remark": "",
    "method_instruction": "",
    "code_remark": "",
    "is_printed": "N",
    "calc_result": null,
    "calc_unit": null,
    "group_printed": null,
    "need_validation": "N",
    "highlight_txt": "N",
    "group_order": null,
    "group_num": 0,
    "cat_num": 3,
    "cat_name": "BioChemistry",
    "result_status": "N",
    "dec_pts": null
}
```

### Fields the MedVault UI actually reads

| Field | Used for |
|---|---|
| `name`, `birthdate` | Patient header on the report screen |
| `test_name` | Row label, e.g. `Glucose`, `TSH`, `Iron` |
| `result` | Primary value shown |
| `result_txt` | Free-text value, used when `result` is empty |
| `unit` | Shown next to the value; may contain HTML entities |
| `ref_range` | Reference range; may contain HTML (`<div>`, `white-space:pre-line`) |
| `sign` | Abnormal flag. Observed values: `""`, `"High"`, `"Low"` |
| `result_status` | Observed values: `"N"`, `"H"`, `"L"`, `"U"`, `null` — **[CONFIRM]** meaning of `"U"` |
| `cat_name` | Grouping/filter tabs: `Hematology `, `BioChemistry`, `Endocrinology `, `Serology`, `Urinalysis` |
| `group_name`, `group_order`, `position` | Sub-grouping and row order inside a category (e.g. `CBCD`, `Lipid panel`, `Electrolytes`) |
| `is_title` | `"Y"` = section heading row, `"N"` = real test |
| `is_valid` | `"Y"` = validated, safe to display. **Rows with `is_valid != "Y"` must not be returned at all** |

### Notes

- `sign` carries the words `"High"`/`"Low"` in the sample data, while `result_status` carries the letters
  `"H"`/`"L"`. The MedVault field table describes `sign` as an "H / L flag". Both spellings appear — MedVault
  must treat `sign` as a display string and drive colouring from `result_status`. **[CONFIRM]** with the
  HIS team which of the two is authoritative.
- `ref_range` may contain multi-line HTML with `\r\n` inside, e.g. the eGFR and lipid-panel entries.
  Do not strip it; MedVault renders it.
- Unvalidated or pending results must never be exposed. A patient reading a preliminary critical value
  without a clinician is a real harm, not just a data issue.

---

## 6. Data mapping — HIS → API

### 6.0 What is already known

Bekaa Hospital's database is already read, read-only, by the existing BSB/WhatsApp report-delivery
integration. The mapping below is **derived from that working integration, not confirmed by the HIS
team** — please correct anything that is wrong rather than assuming it is right.

| Item | Value |
|---|---|
| SQL Server | `192.168.10.239` |
| Databases | `DATA1` (visits), `DPERSONEL` (people and phone numbers) |
| Visits | `DATA1.dbo.MALADE` |
| People | `DPERSONEL.dbo.PERSONEL` |
| Join | `PERSONEL.ADMI = MALADE.ADMIP` |

`MALADE.ADMI` is the visit id, e.g. `26L006186`. **Its 3rd character is the department** — `L` = lab,
`X` = radiology. That single character is how this API separates lab orders (API 2) from imaging
studies (API 4), so no new column is needed for the split.

Working SQL for login, requests and radiology is already in `appsettings.json` under `His:Sql:*`.

### 6.1 Login

| JSON field | HIS table | Column | Notes |
|---|---|---|---|
| `username` (lookup key) | `DPERSONEL.dbo.PERSONEL` | `TEL`, falling back to `TEL1` | **[CONFIRM]** stored format — MedVault logs in with `00000000`, BSB used `96170535819` |
| `name` | `DATA1.dbo.MALADE` | `NOM` | |
| `Lab` | *constant* | — | Facility display name |
| linked family members | `DPERSONEL.dbo.PERSONEL` | rows sharing the same `TEL` | Confirms open item 2: one phone really does cover several patients |
| password hash | — | — | **No password column exists.** See section 3, item 1 |

### 6.2 Requests list

| JSON field | HIS table | Column | Notes |
|---|---|---|---|
| `request_nb` | `MALADE` | `ADMI` | Filter `SUBSTRING(ADMI,3,1) = 'L'` |
| `order_datetime` | `MALADE` | `DATEIN` | **[CONFIRM]** this is the visit date; is there a true order time? |
| `status` | — | — | **[CONFIRM]** currently hard-coded `'V'`; which column holds real status? |
| `pdf_path` | — | — | **[CONFIRM]** see 6.5 — this is a blocker |
| `report_datetime` | — | — | **[CONFIRM]** when a report is released |
| `patient_name` | `MALADE` | `NOM` | |

### 6.5 The two real gaps

Everything above came free from the BSB integration. These two did not, and the project cannot finish
without them:

1. **Where do individual lab results live?** BSB only ever attached a finished PDF, so nothing on our
   side knows which tables hold `test_name`, `result`, `unit`, `ref_range`, `cat_name` and the rest of
   section 5. This is the heart of API 3 and the single most important question in this document.

2. **The report PDFs are on client workstations, not a server.** FoxPro writes `<ADMI>*.pdf` into
   `C:\ExcelH` on each PC that prints reports; BSB reached them over the `C$` admin share and searched
   each machine in turn. A patient-facing API must not reach into staff PCs over admin shares. Before
   go-live, reports need to be written to (or copied to) **one folder on a server** that the API can
   read. That folder becomes `Files:Root`.

### 6.7 Order status — what `TRLAB.RETAT` actually holds

Probed against the live database on 2026-09-01 (242,919 result rows):

| `RETAT` | Rows | Have a result value | Notes |
|---|---|---|---|
| `1` | 226,416 | 224,731 | Released. The only state currently returned to patients |
| *(null)* | 18,360 | 18,312 | Legacy — concentrated in 2014–2017, **zero rows in 2026** |
| `2` | 1,210 | 1,181 | Still occurring: 245 rows in 2026 |
| `3` | 933 | 927 | Still occurring: 577 rows in 2026 |

**A pending order has no `TRLAB` rows at all** — it is not a row carrying a pending flag. Over the last
60 days: 1,349 orders fully released, 131 with no rows, 12 partly released. That is what makes the
`status` derivation in `GetRequests` sound: zero released rows means `"P"`.

`DATEV` is written only on `RETAT = 1` rows and only from **2026-06-22** onward (78,391 of 226,416),
running up to the present. So `report_datetime` is reliable for new orders and null for historic ones.

#### [CONFIRM] What do `RETAT` 2 and 3 mean?

Only 442 of 1,210 (`2`) and 148 of 933 (`3`) have a `RETAT = 1` row for the same test on the same
order. So roughly **1,553 result rows have results but no released counterpart anywhere** — a patient
viewing those orders sees the test silently missing. 822 of them are from 2026.

If `2`/`3` mean *sample rejected* or *cancelled*, hiding them is correct. If they mean *corrected* or
*repeated*, MedVault is withholding results patients should see. The schema cannot distinguish these,
so the filter stays at `RETAT = 1` until the laboratory answers. This is the highest-value open
question in this document.

### 6.8 Phone number format — RESOLVED

Probed 2026-09-01. `PERSONEL.TEL` holds numbers in **local form throughout** — not one row carries a
`961` country code. MedVault's `00000000` is the right shape and the BSB `96170535819` form was a red
herring, so the open question in section 3 is closed.

But the column is not clean. Of the 104,563 nine-character numbers, **104,387 contain a separator** —
`70 143335`, `03-123456`. Against a literal `TEL = @mobile` comparison, none of those patients can log
in, which was roughly half of all accounts.

Every phone comparison in `His:Sql:*` therefore strips spaces, dashes, slashes, dots and parentheses
from **both** sides before comparing. This is exact — two numbers match only when their digits match —
unlike normalising a guessed leading zero, which could have matched one patient onto another's record.
Each query also guards that the normalised `@mobile` is non-empty, so a username like `"---"` matches
nobody rather than every account with a blank `TEL`.

Two consequences worth knowing:

- The `REPLACE` chain is **not sargable**, so a login scans `PERSONEL` (~209k rows). Fine today; if login
  latency becomes a problem, add a persisted computed column holding the digits and index it.
- A handful of rows hold Arabic words or `SA` instead of a number. They can never match a login attempt,
  so they are harmless — but the column is free text, not a validated phone field.

### 6.6 Reference ranges and abnormal flags

Bekaa's HIS stores neither. Every column matching `norm`/`mini`/`maxi`/`refer`/`unit` was searched
across all 120 databases on the server; `RLAB1.VDEFAUT` is empty, `LBDAT.LBL`/`LBN` are prices, and
`TRLAB.REFER` holds the **analyser name** (Sysmex, AU480, Horiba) — it is not a range and must never be
displayed as one. `TRLAB.ABNORMAL` is `NULL` in all 242,297 rows, so the analyser's own High/Low flag is
not available either.

The starter catalogue ships with the real test codes, names, units and categories read from
`DATAPRIX.dbo.RLAB1` on 2026-09-01, ordered by usage — 8,800 CBC rows apiece down to the urine strip
tests — with every range left blank for the laboratory to fill.

So `ref_range` and `result_status` are produced by the API from a hand-maintained catalogue file,
configured as `His:ReferenceRangeCatalogue` and formatted per
`src/MedVault.Api/Data/Catalogue/reference-ranges.starter.json`. Entries are matched on the row's
`field_num` (`TRLAB.CODE`) first, then on `test_name`.

| Catalogue state | `ref_range` | `result_status` |
|---|---|---|
| Entry with `low`/`high` | `<div>70-180</div>` | `H` / `L` / `N`, computed from `result` |
| Entry with `text` only | `<div>Negative</div>` | `null` — nothing to compare against |
| Entry present, result not numeric | the range | `null` |
| **No entry** | `null` | `null` |

**`result_status` must never be hardcoded to `"N"`.** A blanket `N` reports a CRP of 60 as normal
against a reference of `< 5`. A test whose range the lab has not confirmed returns `null` for both
fields, and the app hides the gauge — which is the honest answer, and the one MedVault already handles.

`group_name` does not exist in this schema either. The API falls back to `cat_name` (`HEMATOLOGY`,
`CHEMISTRY`, `URINE ANALYSIS`), which sections the report the way the printed one is sectioned instead
of collapsing every test into a single "Other" group.

### 6.3 Results

| JSON field | HIS table | Column | Notes |
|---|---|---|---|
| `test_name`, `test_id` | | | |
| `result`, `result_txt` | | | |
| `unit`, `ref_range` | | | Per-test or per-age/sex range? |
| `sign`, `result_status` | | | Computed by HIS or by the API? |
| `cat_name`, `cat_num` | | | |
| `group_name`, `group_num`, `group_order`, `position` | | | |
| `is_valid`, `need_validation` | | | |

### 6.4 Radiology (section 7)

| JSON field | HIS table | Column | Notes |
|---|---|---|---|
| `study_nb` | `MALADE` | `ADMI` | Filter `SUBSTRING(ADMI,3,1) = 'X'` |
| `order_datetime` | `MALADE` | `DATEIN` | |
| `patient_name` | `MALADE` | `NOM` | |
| `modality` | | | **[CONFIRM]** not in `MALADE` — where is CT / MRI / X-Ray / US recorded? |
| `study_name` | | | **[CONFIRM]** |
| report text | | | **[CONFIRM]** stored as text, RTF, or PDF only? |
| report PDF | | | **[CONFIRM]** see 6.5 |

---

## 7. Radiology — new endpoints

### 7.1 Design note

Radiology studies are **not** added to API 2. If they were, the existing MedVault lab screen would list
imaging studies and then call API 3 for them, which returns nothing. Instead radiology gets its own pair
of endpoints with the same shape, so the lab flow is untouched and the imaging flow is a new screen.

If the HIS stores radiology reports internally (a report table with the text, or a PDF path), **no PACS
integration is required** for this phase. PACS is only needed to show the images themselves, which is out
of scope here.

### 7.2 API 4 — Radiology list

**Method:** `GET`
**Endpoint:** `/api/mobile/radiology?tcToken=<token>`

```json
{
    "studies": [
        {
            "study_nb": "2604120017",
            "order_datetime": "2026-04-12 09:15:00",
            "status": "V",
            "modality": "CT",
            "study_name": "CT Chest with contrast",
            "pdf_path": "ReportPDF\/2604120017\/a91f3b7c22e.pdf",
            "report_datetime": "2026-04-12 13:40:00",
            "patient_name": "ALEX  EXAMPLE"
        }
    ]
}
```

Same conventions as API 2. `modality` **[CONFIRM]** — proposed code list: `XR`, `CT`, `MR`, `US`, `MG`, `NM`.

### 7.3 API 5 — Radiology report

**Method:** `GET`
**Endpoint:** `/api/mobile/radiologyreport?studynb=<study_nb>&tcToken=<token>`

```json
{
    "study_nb": "2604120017",
    "report": {
        "name": "ALEX EXAMPLE",
        "birthdate": "1990-01-01",
        "study_name": "CT Chest with contrast",
        "modality": "CT",
        "study_datetime": "2026-04-12 09:15:00",
        "report_datetime": "2026-04-12 13:40:00",
        "radiologist": "DR. SAM EXAMPLE",
        "clinical_history": "Persistent cough, 3 weeks",
        "technique": "Axial images obtained after IV contrast",
        "findings": "…",
        "impression": "…",
        "is_valid": "Y",
        "pdf_path": "ReportPDF\/2604120017\/a91f3b7c22e.pdf"
    }
}
```

**[CONFIRM]** Many HIS installations store the radiology report as one free-text blob rather than split
into `technique` / `findings` / `impression`. If that is the case here, send the whole report in
`findings` and leave the other two as `""` — do not attempt to parse it server-side.

### 7.4 API 6 — File download

The MedVault contract returns `pdf_path` as a **relative** path (`ResultPDF/2026001001/example-report.pdf`) but
never says what it is relative to, or how the file is authenticated. Serving that folder as a static
directory would expose every patient's PDF to anyone who can guess a path. Instead:

**Method:** `GET`
**Endpoint:** `/api/mobile/file?path=<pdf_path>&tcToken=<token>`
**Response:** `Content-Type: application/pdf`, the file bytes

Rules:

1. The API resolves `path` against a configured root folder and **rejects any path containing `..`, a
   drive letter, a UNC prefix, or a leading separator** after decoding.
2. The API verifies the resolved file belongs to an order that the token's patient is entitled to see.
   Path knowledge alone must never be enough.
3. Unknown or unauthorised path → `404` (not `403` — do not confirm that a file exists).

---

## 8. Errors

| Situation | HTTP | Body |
|---|---|---|
| Bad credentials | `200` | `{"success": false, "message": "Invalid credentials"}` |
| Missing/invalid/expired `tcToken` | `401` | `{"success": false, "message": "Unauthorized"}` |
| `request_nb` not owned by this token's patient | `404` | `{"success": false, "message": "Not found"}` |
| Malformed parameters | `400` | `{"success": false, "message": "Bad request"}` |
| HIS unreachable / query failure | `503` | `{"success": false, "message": "Service unavailable"}` |

Never return a SQL error, stack trace, connection string, or FoxPro table name to the client. Log the
detail server-side with a correlation id and return only the id to the caller.

---

## 9. Security requirements

1. **HTTPS with a valid certificate.** No self-signed certificate in production; MedVault should pin or
   at minimum refuse invalid certificates.
2. **The token is in the query string.** This is inherited from the MedVault contract and MedVault sends it
   that way today. Query strings are written to IIS logs, proxy logs, and any intermediate appliance.
   Mitigations, all of which keep the existing app working:
   - Also accept `Authorization: Bearer <token>` and migrate MedVault to it over time.
   - Configure IIS/`W3C` logging to **omit `cs-uri-query`**, or scrub `tcToken` from logs.
   - Keep token lifetime short (section 3, item 3).
3. **Read-only database account.** The API needs `SELECT` only. It must never hold an account that can
   write to the HIS.
4. **Per-request authorisation.** Every call re-checks that the requested `request_nb` / `study_nb` /
   file belongs to the token's patient or their linked family. Never trust the identifier alone.
5. **Rate limiting** on `/login`, in two independent buckets:
   - **Per source IP** (`Auth:LoginAttemptsPerWindow`) — stops someone walking the mobile-number space.
   - **Per account** (`Auth:LoginAttemptsPerUserPerWindow`) — stops one patient's number being guessed at,
     and applies to every caller.

   MedVault does not call this API from patient handsets: every patient arrives through one MedVault
   server, so an IP bucket alone puts the entire patient population into one budget and a busy morning
   locks all of them out. `Auth:TrustedClientIps` exempts listed addresses from the **IP** bucket only —
   list the integration servers and nothing else, since an entry gives up IP-based enumeration
   protection for that source. The per-account bucket still applies to them, which is what keeps the
   allowlist from becoming unlimited guesses.

   Behind IIS/ARR, `Network:KnownProxies` must also be set or every request carries the proxy's address
   (section 9, and the note in `appsettings-for-server.json`).
6. **Cap the family group.** One mobile number covering a family is the MedVault contract; one covering
   thousands is a placeholder. Probed 2026-09-01: a single number in the Bekaa database is linked to
   **11,989** patient records, another to 475, then 100, 62, 58, 40 — default values and staff numbers
   typed onto every walk-in. Genuine families sit at six or below (83,233 numbers have one patient,
   21,060 have two). Because the number alone logs in, each of those is a key to every record behind it.
   `Auth:MaxLinkedPatients` (default 12) refuses such a login outright — not truncated to the first N,
   which would still hand a stranger real patients' reports. The refusal is the ordinary
   "Invalid credentials" reply, and is logged server-side.
7. **Audit log** of who read which report and when. This is normally a regulatory requirement for patient
   record access; **[CONFIRM]** retention period with the hospital.
8. **No PHI in URLs beyond the identifiers already required**, and no PHI in analytics or crash reports.

---

## 10. Acceptance checklist

The integration is accepted when, against a test patient:

- [ ] `POST /api/mobile/login` returns a token, and the response JSON keys match section 3 exactly (including `Lab`).
- [ ] `GET /api/mobile/request` returns the patient's orders **and** those of linked family members.
- [ ] `GET /api/mobile/results` for a known order returns every field in section 5, with `null` vs `""` matching the samples.
- [ ] HTML in `unit` and `ref_range` is passed through unmodified.
- [ ] No row with `is_valid != "Y"` is ever returned.
- [ ] `status` is `"P"` for an order with no released results and `"V"` once results are released — not `"V"` for both.
- [ ] `result_status` is `null`, not `"N"`, for a test with no catalogue entry; a value outside a catalogued range reads `H` or `L`.
- [ ] A mobile number linked to more patients than `Auth:MaxLinkedPatients` is refused at login.
- [ ] A patient whose number is stored with a separator (`70 143335`) can log in.
- [ ] A patient behind the allowlisted MedVault server can log in well past `Auth:LoginAttemptsPerWindow` times, while one account still exhausts its own budget.
- [ ] An order number belonging to a different patient returns `404`, not data.
- [ ] An expired token returns `401`.
- [ ] `pdf_path` values resolve through API 6 and reject `../` traversal.
- [ ] A response is delivered in under 2 seconds for a patient with 100 orders. **[CONFIRM]** target.

### 10.1 Running the checklist against the HIS

Most of the list above is automated in `tests/MedVault.Api.Tests/HisIntegrationTests.cs`. It is skipped
unless a connection string is present, so it never runs by accident.

```bash
MEDVAULT_HIS_CONNECTION="Server=...;Database=DATA1;Integrated Security=true;TrustServerCertificate=true" \
MEDVAULT_TEST_MOBILE=00000000 \
MEDVAULT_OTHER_MOBILE=71000000 \
dotnet test --filter FullyQualifiedName~HisIntegrationTests
```

Prefer a restored backup over the live database — see [his-backup-testing.md](his-backup-testing.md),
which also covers producing a de-identified copy the mobile developer can hold himself.

Before running it against the live HIS, three things must be true:

1. **Written authorisation** from the hospital to query the database, and a maintenance window if
   there is any doubt about load.
2. **A SELECT-only login.** The first test proves the account cannot write, by asking SQL Server for
   the granted permissions — never by attempting a write.
3. **Consented test patients.** `MEDVAULT_TEST_MOBILE` and `MEDVAULT_OTHER_MOBILE` must be numbers the
   hospital has agreed to use. The cross-patient test needs a second real patient, because attempting
   a made-up order number proves nothing.

Never put the connection string in `appsettings.json`. Use an environment variable or
`dotnet user-secrets`, and keep the credentials out of the shell history on a shared machine.

The tests report counts, field names and shapes only — no name, birth date or result value is ever
written to the output, so a failure can be pasted into a ticket safely.

Order to work in:

| Step | Test | Needs |
|---|---|---|
| 1 | `The_his_account_can_read_and_cannot_write` | connection only |
| 2 | `Each_configured_query_parses_and_exposes_the_contract_columns` | connection only — validates the section 6 SQL without reading a row |
| 3 | `The_test_patient_resolves_to_exactly_one_account` | a test patient |
| 4 | `Requests_have_the_contract_shape` | a patient with orders |
| 5 | `No_unvalidated_result_is_ever_returned` | a patient with a released report |
| 6 | `An_owned_order_never_reports_itself_as_missing` | as above |
| 7 | `One_patient_cannot_read_another_patients_order` | a second test patient |

Step 2 is the gate on section 6: it uses `sp_describe_first_result_set`, which validates a query and
returns its column list **without executing it**, so the HIS team's SQL can be checked for parse errors
and missing aliases before it ever touches patient data. `GetResults` and `GetRadiologyReport` will fail
this step until they are written — that is the checklist working, not a defect.

---

## 11. ملخص بالعربية

### الفكرة العامة

تطبيق **MedVault** لا يتصل بقاعدة بيانات الـ HIS مباشرة. بينهما طبقة **ASP.NET Web API** تقرأ من قاعدة
البيانات وتعيد النتائج بصيغة JSON محددة.

عند تسجيل الدخول:

- **اسم المستخدم** = رقم الهاتف المحلي (بدون رمز الدولة، مثال `00000000`).
- **كلمة المرور** = كلمة المرور التي ينشئها المستشفى للمريض. *(ملاحظة مهمة: النموذج الحالي في MedVault لا يرسل
  كلمة مرور إطلاقًا — رقم الهاتف وحده يكفي لعرض النتائج. يجب الاتفاق على هذه النقطة، والخيار الموصى به هو
  إضافة كلمة مرور — راجع القسم 3.)*

يرسل التطبيق طلبًا إلى **Login API**، فيبحث الـ API في قاعدة البيانات ويعيد رمز الدخول **(Token)**. ثم
يُستخدم هذا الرمز لطلب تقارير المختبر أو الأشعة.

### عند طلب تقارير المريض

- يبحث الـ API باستخدام **رقم الهاتف** (ومن خلاله رقم الملف / Case Number).
- يجلب كل طلبات المختبر الموجودة في الـ HIS ← **API 2**.
- يجلب تفاصيل نتائج كل طلب ← **API 3**.
- يجلب تقارير الأشعة الموجودة في الـ HIS ← **API 4** و **API 5**.
- ويعيدها بصيغة JSON المطلوبة، مطابقة تمامًا لصيغة MedVault حتى يعمل التطبيق دون أي تعديل.

### إذا كانت تقارير الأشعة محفوظة داخل الـ HIS

هذا ممتاز، ولا حاجة إلى **PACS** في هذه المرحلة. يكفي أن يكون في قاعدة البيانات جدول للتقارير يحتوي مثلًا:

| العمود | المعنى |
|---|---|
| `study_nb` | رقم الفحص (المفتاح) |
| `patient_id` | رقم المريض |
| `modality` | نوع الفحص (CT / MRI / X-Ray / Ultrasound) |
| `study_name` | اسم الفحص |
| `study_datetime` | تاريخ ووقت إجراء الفحص |
| `report_text` | نص التقرير (أو `findings` / `impression` منفصلين) |
| `radiologist` | اسم طبيب الأشعة |
| `report_datetime` | تاريخ ووقت اعتماد التقرير |
| `is_valid` | هل التقرير معتمد ويمكن عرضه للمريض |
| `pdf_path` | مسار ملف الـ PDF إن وُجد |

الـ PACS يصبح ضروريًا فقط إذا أردنا عرض **الصور** نفسها داخل التطبيق، وهذا خارج نطاق هذه المرحلة.

### ملاحظتان أمنيتان مهمتان

1. **رمز الدخول يُرسل داخل رابط الطلب (query string)**، وهذا يعني أنه يُسجَّل في سجلات الخادم والوسطاء.
   نبقيه كما هو حتى يعمل التطبيق الحالي، لكن يجب إخفاء `tcToken` من السجلات، وتقصير مدة صلاحيته، ودعم
   `Authorization: Bearer` كبديل مستقبلي.
2. **رقم الهاتف الواحد يعرض نتائج كل أفراد العائلة المرتبطين به** (كما في مثال MedVault: ثلاثة أشخاص على رقم
   واحد). أي أن رقم الهاتف وحده هو حدّ الحماية لسجلات عائلة كاملة. يجب تأكيد أن هذا هو السلوك المطلوب.

---

## 12. Open items summary

**The two that block everything else are 1 and 2.** The rest can be answered while development continues.

| # | Item | Section | Owner |
|---|---|---|---|
| 1 | **Where do individual lab result rows live?** | 5, 6.5 | HIS |
| 2 | **A server folder for report PDFs**, instead of `C:\ExcelH` on each client PC | 6.5, 7.4 | HIS + IT |
| 3 | Password vs mobile-only authentication (no password column exists today) | 3 | MedVault + hospital |
| 4 | Format stored in `PERSONEL.TEL` — local or with country code | 6.1 | HIS |
| 5 | Real `status` column, and its full code list | 4, 6.2 | HIS |
| 6 | Is `DATEIN` the order time, and where is the report release time | 6.2 | HIS |
| 7 | Radiology: modality, study name, and where the report text is stored | 6.4, 7.3 | HIS |
| 8 | Meaning of `result_status: "U"` | 5 | HIS |
| 9 | `sign` vs `result_status` — which is authoritative | 5 | HIS |
| 10 | Token lifetime and logout behaviour | 3 | Both |
| 11 | Doctor (`type: "D"`) scope | 3 | Hospital |
| 12 | Audit log retention period | 9 | Hospital |

**Resolved** since the first draft, from the existing BSB integration: the family-member link
(`PERSONEL` rows sharing a `TEL`), the lab/radiology split (3rd character of `ADMI`), and the login,
requests and radiology-list queries — all in section 6.0.
