# MedVault API

MedVault API is an ASP.NET Core service that gives a patient-facing mobile app a secure, stable interface to hospital information systems (HIS). It preserves the mobile contract while isolating database access, authorization, file handling, and operational safeguards behind a small API surface.

> **Important:** This repository is designed for development and integration. Do not use the included sample configuration or mock data in production. Configure a read-only HIS account, secrets, TLS, proxy trust, and the security controls described below before deployment.

## Highlights

- Compatible mobile endpoints for login, lab requests, results, radiology, reports, and authenticated PDF downloads.
- A provider seam (`IHisRepository`) with a local mock implementation and a SQL Server implementation.
- Ownership checks on results and files, token revocation, path-traversal protection, login throttling, and reverse-proxy hardening.
- Contract tests that lock down response shape and security behavior.
- Sanitized, synthetic fixtures so the API can be run locally without access to an HIS.

## Quick start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Run locally

```bash
git clone https://github.com/Mustafa3654/Medvault-Api.git
cd Medvault-Api
dotnet run --project src/MedVault.Api
```

The development configuration uses the mock provider and listens on `http://localhost:5000`.

Use the synthetic test account below:

```text
username: 00000000
password: medvault
type:     P
```

```bash
curl -X POST http://localhost:5000/api/mobile/login \
  -H "Content-Type: application/json" \
  -d "{\"username\":\"00000000\",\"password\":\"medvault\",\"type\":\"P\"}"
```

The included [MedVault.http](MedVault.http) file and [Postman collection](docs/MedVault-HIS-APIs.postman_collection.json) provide ready-to-run requests.

## Test

```bash
dotnet test
```

Integration checks against a real HIS run only when `MEDVAULT_HIS_CONNECTION` is supplied. Keep production credentials in user secrets or environment variables—never commit them.

## API surface

| Method | Endpoint | Purpose |
| --- | --- | --- |
| `POST` | `/api/mobile/login` | Authenticate a patient or doctor and issue a token. |
| `POST` | `/api/mobile/logout` | Revoke the active token. |
| `GET` | `/api/mobile/request` | List a patient's laboratory orders. |
| `GET` | `/api/mobile/results` | Retrieve results for an authorized order. |
| `GET` | `/api/mobile/radiology` | List imaging studies. |
| `GET` | `/api/mobile/radiologyreport` | Retrieve an imaging report. |
| `GET` | `/api/mobile/file` | Download an authorized PDF. |

Tokens are accepted through `Authorization: Bearer <token>` or the legacy `tcToken` query parameter. Prefer the bearer header for new clients.

The full request/response contract and HIS-vendor checklist live in [the API specification](docs/MedVault-HIS-API-Specification.md).

## Connecting an HIS

1. Review and complete section 6 of the API specification with the HIS team.
2. Put the parameterized SQL queries in `His:Sql:*` and alias output columns to the documented JSON fields.
3. Set `His:Provider=sql` and provide `ConnectionStrings:His` using a **read-only** database account.
4. Configure `Files:Root` for report PDFs and explicitly list trusted proxy addresses in `Network:KnownProxies` when the API is deployed behind a proxy.
5. Run the acceptance checklist in section 10 of the specification.

`IHisRepository` is the integration boundary: endpoint behavior, authorization, token handling, and path safety remain independent of the HIS schema.

## Security notes

- Use HTTPS end-to-end and make bearer authentication the preferred client path.
- `tcToken` is supported for compatibility only; do not log query strings when it is enabled.
- Tokens and rate-limit state are in memory. Use a distributed implementation before running multiple API instances.
- Enable passwords (`Auth:RequirePassword=true`) when the hospital supports them.
- Keep database credentials, local settings, exports, PDFs, and production data out of source control.

See [the API specification](docs/MedVault-HIS-API-Specification.md) for deployment constraints and the threat-model details.

## Project layout

```text
docs/                         API specification and integration guides
src/MedVault.Api/
  Auth/                       token service and login throttling
  Contracts/                  contract types
  Data/                       HIS providers, mock fixtures, reference ranges
  Endpoints/                  mobile API routes and authorization
  Files/                      safe PDF resolution
tests/MedVault.Api.Tests/     contract and security tests
```

## License

Released under the [MIT License](LICENSE).
