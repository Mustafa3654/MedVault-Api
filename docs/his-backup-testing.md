# Testing against a restored copy of the HIS

Working on a restored backup instead of the live hospital database is the right call. It removes the
load question, removes the risk of a bad query on production, and lets you re-run the suite as often
as you like.

It also unlocks something better: **a de-identified copy the developer can have on his own machine.**
That is the real fix for "he has no access to the server" — not a VPN, not a shared credential.

---

## Two copies, two purposes

| | Copy A — *identified* | Copy B — *de-identified* |
|---|---|---|
| Contains | Real names, phones, results | Real structure, fake identifiers |
| Lives on | Hospital network only | Anywhere, including the developer's laptop |
| Who uses it | You | The developer |
| For | Acceptance testing (spec §10) | Building and debugging the app |

Copy A is a restored backup and is **exactly as sensitive as production** — a backup of PHI is PHI.
Copy B is made from Copy A by scrubbing the identifiers, and is what leaves the network.

---

## Copy A — restore and lock it down

### 1. Restore under a different name

```sql
RESTORE DATABASE DATA1_DEV
FROM DISK = 'D:\backups\DATA1.bak'
WITH
    MOVE 'DATA1'     TO 'D:\sqldata\DATA1_DEV.mdf',
    MOVE 'DATA1_log' TO 'D:\sqldata\DATA1_DEV_log.ldf',
    RECOVERY;

RESTORE DATABASE DPERSONEL_DEV
FROM DISK = 'D:\backups\DPERSONEL.bak'
WITH
    MOVE 'DPERSONEL'     TO 'D:\sqldata\DPERSONEL_DEV.mdf',
    MOVE 'DPERSONEL_log' TO 'D:\sqldata\DPERSONEL_DEV_log.ldf',
    RECOVERY;
```

Use `RESTORE FILELISTONLY FROM DISK = '...'` first if you don't know the logical file names.

The `_DEV` suffix is not cosmetic. The queries in `appsettings.json` reference `[DATA1].dbo.MALADE`
and `[DPERSONEL].dbo.PERSONEL` by name, so restoring under the original names on a machine that can
also reach the hospital server is how you end up querying the wrong one. Different name, no ambiguity —
and the de-identification script refuses to run against a database called `DATA1` for the same reason.

### 2. Make it physically read-only

```sql
ALTER DATABASE DATA1_DEV     SET READ_ONLY WITH ROLLBACK IMMEDIATE;
ALTER DATABASE DPERSONEL_DEV SET READ_ONLY WITH ROLLBACK IMMEDIATE;
```

Now no query can modify it, whatever the connection string says. Do this *after* de-identifying, if
you're making Copy B — the scrub needs to write.

### 3. Create the SELECT-only login the API will use

Worth doing on the copy even though it's read-only, because this is the account the hospital will
eventually create on the real server, and you want to find out here if it's missing a grant.

```sql
CREATE LOGIN medvault_ro WITH PASSWORD = '<generate a strong one>';

USE DATA1_DEV;
CREATE USER medvault_ro FOR LOGIN medvault_ro;
ALTER ROLE db_datareader ADD MEMBER medvault_ro;

USE DPERSONEL_DEV;
CREATE USER medvault_ro FOR LOGIN medvault_ro;
ALTER ROLE db_datareader ADD MEMBER medvault_ro;
```

`db_datareader` grants SELECT and nothing else — which is what
`The_his_account_can_read_and_cannot_write` checks for.

### 4. Point the tests at it

The queries in `appsettings.json` hard-code `[DATA1]` and `[DPERSONEL]`. Override them for the copy
rather than editing the file, so the production queries stay in source control unchanged:

```bash
MEDVAULT_HIS_CONNECTION="Server=localhost;Database=DATA1_DEV;User Id=medvault_ro;Password=...;TrustServerCertificate=true" \
MEDVAULT_TEST_MOBILE=00000000 \
MEDVAULT_OTHER_MOBILE=71000000 \
dotnet test --filter FullyQualifiedName~HisIntegrationTests
```

If the three-part names get in the way, the simplest fix is to restore under the original names on a
machine with **no route to the hospital server at all**. Isolation by network beats isolation by
naming convention.

### 5. Pick the two test patients from the data

You need one patient with a released report and one other patient with orders. Find them without
reading anyone's results:

```sql
-- Phone numbers that cover more than one patient - the family case API 2 must handle
SELECT TOP 10 p.TEL, COUNT(*) AS patients
FROM DPERSONEL_DEV.dbo.PERSONEL p
WHERE p.TEL IS NOT NULL AND LTRIM(RTRIM(p.TEL)) <> ''
GROUP BY p.TEL
HAVING COUNT(*) > 1
ORDER BY COUNT(*) DESC;

-- Lab visits for a candidate number
SELECT COUNT(*) AS lab_visits
FROM DATA1_DEV.dbo.MALADE m
JOIN DPERSONEL_DEV.dbo.PERSONEL p ON p.ADMI = m.ADMIP
WHERE LTRIM(RTRIM(p.TEL)) = '<candidate>'
  AND SUBSTRING(m.ADMI, 3, 1) = 'L';
```

This is also where you settle the open question in `appsettings.json`: does `PERSONEL.TEL` hold
`00000000` or `96170535819`? The first query answers it in one look, and it determines whether the API
needs to normalise the number before lookup.

---

## Copy B — the one the developer gets

Restore a second copy (or take a copy of Copy A before setting it read-only), then run:

```
scripts/deidentify-his-copy.sql
```

Run **step 1 on its own first.** It lists every column in the database that looks like a name, phone,
address, email, ID number or date of birth. The script scrubs the ones we know about from the BSB
integration — `MALADE.NOM`, `PERSONEL.TEL`, `PERSONEL.TEL1` — but a hospital database has tables
neither of us has seen. Read that output and add anything it finds to step 3 before the copy goes
anywhere.

What survives the scrub is what makes the copy useful: row counts, visit ids, dates, joins, the `L`/`X`
department character, every clinical value, and the fact that several patients share one phone number.
The developer gets real volumes and real edge cases. Names and numbers are replaced deterministically,
so the same person scrubs to the same fake identity everywhere and the joins still make sense.

Then lock it read-only, back it up, and hand him the `.bak`.

**Before it leaves the network,** run the three verification queries at the end of the script. They
check that no row count changed, that no original name or phone survived, and that the family grouping
is still there. A de-identification you haven't verified is not a de-identification.

---

## The honest limit

None of this makes the two missing queries appear. `His:Sql:GetResults` and
`His:Sql:GetRadiologyReport` are still `TODO` comment blocks, and the integration tests will report
them as not supplied whichever copy you point at.

What the restored copy buys you *today* is real: it confirms the connection and permissions model, it
validates the three derived queries that your own config marks `UNVERIFIED`, and it answers the phone
format question. That is exactly the evidence to take back to the HIS team when you ask them for the
two that are missing — and finding out on a copy that the derived queries are wrong is much cheaper
than finding out on the hospital's server.
