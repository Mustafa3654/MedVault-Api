# MedVault API — handover to the mobile app

**For:** the MedVault mobile app developer
**Status:** ready to integrate against, running on mock data
**Contract source:** `MedVault-API-Documentation.docx` (MedVault Demo Hospital), reproduced exactly

---

## What this is

An API that speaks the MedVault Demo Hospital contract. The three MedVault endpoints behave identically to
the hospital's own API — same URLs, same JSON keys, same values — so **the app needs no changes to
talk to it**. Three more endpoints are added for radiology and PDF download; the app can ignore them
until you're ready.

Point the app's base URL at this server and it should work as-is. If anything differs from what the
app expects today, that is a bug on our side — tell us and we'll fix it, rather than changing the app.

## Base URL

```
http://localhost:5000
```

Change it for the deployed environment. `GET /health` returns `{"status":"ok"}` without auth if you
need to check the server is up.

## Auth

Log in, get a token, put the token on every other call. Two ways to send it, both work:

- `?tcToken=<token>` in the query string — what the app does today, keep using it
- `Authorization: Bearer <token>` — preferred when you get a chance to change it, because query
  strings end up in server and proxy logs

**Token lifetime is 12 hours**, and a server restart invalidates every token. Please handle a `401` by
logging in again silently, rather than showing an error — see [Known limits](#known-limits).

---

## The three MedVault endpoints — unchanged

### 1. `POST /api/mobile/login`

```json
{ "username": "00000000", "password": "medvault", "type": "P" }
```

`password` is optional and currently ignored (see [Known limits](#known-limits)). `type` is `"P"` for
patient, `"D"` for doctor.

**200** — note the capital `L` in `Lab`:

```json
{
  "success": true,
  "token": "2ff818214657415281e6a8e0fa434f6e...",
  "user": "00000000",
  "name": "ALEX EXAMPLE",
  "Lab": "MedVault Demo Hospital"
}
```

**Failed login is also a `200`**, matching MedVault — check the `success` field, not the status code:

```json
{ "success": false, "message": "Invalid credentials" }
```

A wrong number and a wrong password return the identical response on purpose, so the endpoint can't be
used to find out which mobile numbers belong to patients. Don't display different messages for the two
cases; you can't tell them apart, and that's deliberate.

### 2. `GET /api/mobile/request?tcToken=<token>`

Returns the patient's orders **and those of every family member sharing the mobile number** — note the
three different `patient_name` values below. Group by `patient_name` in the UI.

```json
{
  "requests": [
    {
      "request_nb": "2026001001",
      "order_datetime": "2026-03-25 11:03:00",
      "status": "V",
      "pdf_path": "ResultPDF/2026001001/example-report.pdf",
      "report_datetime": "2026-03-25 14:47:00",
      "patient_name": "ALEX  EXAMPLE"
    },
    {
      "request_nb": "2026001002",
      "order_datetime": "2026-05-25 22:37:00",
      "status": "V",
      "pdf_path": null,
      "report_datetime": null,
      "patient_name": "CASEY  EXAMPLE"
    }
  ]
}
```

`pdf_path` and `report_datetime` are `null` when the report isn't released yet — that's the normal
state for most orders, not an error.

### 3. `GET /api/mobile/results?requestnb=<request_nb>&tcToken=<token>`

Returns all 48 result rows for the sample order, each with the full 34-field object:

```json
{
  "request_nb": "2026001001",
  "results": [
    {
      "name": "ALEX EXAMPLE",
      "birthdate": "1990-01-01",
      "id": 1857,
      "test_name": "Iron",
      "is_title": "N",
      "unit": "&micro;g/dL",
      "ref_range": "<div>70-180</div>",
      "result": "73",
      "sign": "",
      "cat_name": "BioChemistry",
      "result_status": "N",
      "is_valid": "Y"
    }
  ]
}
```

*(abbreviated — the live response has all 34 fields on every row)*

Four things to get right when parsing:

| | |
|---|---|
| `unit` and `ref_range` contain **HTML and HTML entities** | `&micro;g/dL`, `<div>70-180</div>`, and some `ref_range` values contain multi-line `<div style="white-space:pre-line;">` blocks. Render as HTML, don't escape it. |
| `sign` uses **words, not letters** | `"High"` / `"Low"` / `""`. The single letters `H` / `L` / `N` / `U` are in `result_status`, which is a different field. The MedVault document's field table says `H / L` for `sign` — the document is wrong, the capture is right. |
| `cat_name` has a **trailing space** on some categories | `"Hematology "` and `"Endocrinology "` carry it, `"BioChemistry"` doesn't. If you trim it, one category splits into two in the UI. Group on the raw string. |
| `null` and `""` are **different** | Both appear, and they mean different things. Don't coalesce. |

Every row returned has `is_valid: "Y"`. Unvalidated results are filtered out server-side and will never
reach you.

---

## The three new endpoints

Additive — nothing in the app breaks if you ignore them.

### 4. `GET /api/mobile/radiology?tcToken=<token>`

```json
{
  "studies": [
    {
      "study_nb": "2604120017",
      "order_datetime": "2026-04-12 09:15:00",
      "status": "V",
      "modality": "CT",
      "study_name": "CT Chest with contrast",
      "pdf_path": "ReportPDF/2604120017/a91f3b7c22e.pdf",
      "report_datetime": "2026-04-12 13:40:00",
      "patient_name": "ALEX  EXAMPLE"
    }
  ]
}
```

### 5. `GET /api/mobile/radiologyreport?studynb=<study_nb>&tcToken=<token>`

```json
{
  "study_nb": "2604120017",
  "report": {
    "name": "ALEX EXAMPLE", "birthdate": "1990-01-01",
    "study_name": "CT Chest with contrast", "modality": "CT",
    "study_datetime": "2026-04-12 09:15:00", "report_datetime": "2026-04-12 13:40:00",
    "radiologist": "DR. SAM EXAMPLE",
    "clinical_history": "...", "technique": "...", "findings": "...", "impression": "...",
    "is_valid": "Y",
    "pdf_path": "ReportPDF/2604120017/a91f3b7c22e.pdf"
  }
}
```

> The radiology sample data is illustrative — the shape is final, the values are placeholders until the
> hospital's imaging tables are mapped.

### 6. `GET /api/mobile/file?path=<pdf_path>&tcToken=<token>`

Pass a `pdf_path` exactly as it came back from endpoint 2 or 4. Returns `application/pdf`.

Do **not** build the path yourself or fetch the PDF folder directly — the server checks that the path
is one this patient's own orders point at, and rejects anything else with a `404`.

### 7. `POST /api/mobile/logout?tcToken=<token>`

Revokes the token immediately. Returns `{"success": true}`. Call it on sign-out.

---

## Error handling

Every error has the same shape, so one parser handles all of them:

```json
{ "success": false, "message": "..." }
```

| Status | When | `message` |
|---|---|---|
| `200` | Failed login — check `success` | `Invalid credentials` |
| `400` | Missing parameter, or a malformed request body | `Bad request` |
| `401` | Missing, invalid, expired, or logged-out token | `Unauthorized` |
| `404` | Order/study isn't this patient's, or doesn't exist | `Not found` |
| `429` | Too many login attempts (10 per 15 min) | `Too many attempts` |
| `503` | The hospital system is unreachable | `Service unavailable` |

Two that matter for UX:

- **`404` means "not yours or doesn't exist"** — the two are deliberately indistinguishable. Show
  "not available", never "this belongs to another patient".
- **`503` is the only status that means the hospital is down.** A `400` is our request being wrong.
  Please don't show an outage message for a `400`.

**An order with no results yet is `200` with an empty array, not a `404`:**

```json
{ "request_nb": "2026001002", "results": [] }
```

Most orders are in this state. Show "results not ready yet" for an empty array, and reserve the
error state for an actual `404`.

---

## Verify it yourself

Don't take any of the above on trust. `MedVault.http` in the repo root has every request in this
document as a runnable block — open it in VS Code (REST Client extension), Rider, or Visual Studio,
start the server, and click through them.

```bash
dotnet run --project src/MedVault.Api
```

Or from a terminal:

```bash
curl -X POST http://localhost:5000/api/mobile/login -H "Content-Type: application/json" -d "{\"username\":\"00000000\",\"password\":\"medvault\",\"type\":\"P\"}"
```

### What has been verified on our side

```bash
dotnet test
```

30 automated tests, all passing. What they establish:

- **The results payload is identical to the MedVault capture** — compared field by field against the JSON
  in `MedVault-API-Documentation.docx`: 48 rows, all 34 fields, zero differences, including `null` vs `""`,
  the HTML entities, and the trailing spaces in `cat_name`.
- **The requests list matches the capture**, family members included.
- Tokens are rejected when invalid, expired, or logged out.
- One patient cannot read another patient's order — returns `404`, not data.
- File download refuses `../` traversal and refuses real files the patient doesn't own.
- Login is throttled, and every error path returns the shape in the table above.

Each of those tests was checked by deliberately breaking the code and confirming the test fails —
they're not passing by accident.

---

## Known limits

Please design around these; none of them are things the app can fix, but the app can handle them well.

1. **Tokens don't survive a server restart.** All tokens become invalid. Handle `401` by
   re-authenticating in the background — if the app shows an error screen instead, every deployment
   looks like an outage to patients.
2. **Token lifetime is 12 hours.** Same handling as above.
3. **The password field is currently ignored** — the mobile number alone logs in, which is how the
   MedVault API works today. Keep sending `password`; the server will start enforcing it once the hospital
   issues passwords, and nothing in the app needs to change on that day.
4. **Radiology values are placeholders.** Shape is final; data arrives when the imaging tables are
   mapped. Build against the shape.
5. **Currently served over HTTP on localhost.** The deployed environment must be HTTPS before any real
   patient data flows — tokens travel in the query string.

## Questions

Anything that doesn't match what the app expects, send us the request and the response you got and
we'll fix the server. The rule for this integration is that the app stays as it is.
