/*
    De-identify a restored copy of the HIS database.

    PURPOSE
        Turn a restored backup into something that can safely leave the hospital network - so the
        mobile developer can run against real structure, real row counts and real edge cases without
        ever holding a real patient's name, phone number or results.

    RUN THIS ON THE RESTORED COPY. NEVER ON THE HOSPITAL'S DATABASE.
        The safety check below refuses to run against a database named DATA1 or DPERSONEL. Do not
        remove it. Every statement here is an UPDATE - on production that is an incident.

    WHAT IS PRESERVED (and why the copy is still worth testing against)
        - Row counts, visit ids, dates, joins and every clinical value. The queries under test see
          exactly the data volumes and shapes they will see in production.
        - The ADMI department character (L = lab, X = radiology), which is what API 2 and API 4 split on.
        - The grouping of several patients under one phone number - the family case that the MedVault
          contract depends on.

    WHAT IS DESTROYED
        - Names, phone numbers, and any other direct identifier listed in step 3.

    BEFORE YOU TRUST IT
        Step 1 lists every column that looks like an identifier. This script scrubs the ones we know
        about from the BSB integration; a HIS has tables we have never seen. Run step 1, read the
        output, and add anything it finds to step 3 before sharing the copy with anyone.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* ---------------------------------------------------------------------------
   0. Safety check - refuse to run anywhere that looks like production
   --------------------------------------------------------------------------- */

IF DB_NAME() IN ('DATA1', 'DPERSONEL')
BEGIN
    RAISERROR('Refusing to run: this database is named like the live HIS. Restore the backup under a different name (DATA1_DEV, DPERSONEL_DEV) and run this there.', 20, 1) WITH LOG;
END

IF @@SERVERNAME = '192.168.10.239' OR SERVERPROPERTY('MachineName') = '192.168.10.239'
BEGIN
    RAISERROR('Refusing to run: this is the hospital HIS server.', 20, 1) WITH LOG;
END

PRINT 'Running against: ' + @@SERVERNAME + ' / ' + DB_NAME();

/* ---------------------------------------------------------------------------
   1. Discovery - what else in here is an identifier?

   Run this on its own first. Read the results. Anything it lists that is not
   already handled in step 3 must be added there before the copy is shared.
   --------------------------------------------------------------------------- */

SELECT
    s.name  AS [schema],
    t.name  AS [table],
    c.name  AS [column],
    ty.name AS [type],
    c.max_length
FROM sys.columns c
JOIN sys.tables  t  ON t.object_id = c.object_id
JOIN sys.schemas s  ON s.schema_id = t.schema_id
JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
WHERE
    -- name-ish, phone-ish, address-ish, id-document-ish, date-of-birth-ish
    c.name LIKE '%NOM%'   OR c.name LIKE '%NAME%'  OR c.name LIKE '%PRENOM%'
 OR c.name LIKE '%TEL%'   OR c.name LIKE '%PHONE%' OR c.name LIKE '%MOBILE%'
 OR c.name LIKE '%MAIL%'  OR c.name LIKE '%ADRES%' OR c.name LIKE '%ADDRESS%'
 OR c.name LIKE '%BIRTH%' OR c.name LIKE '%DDN%'   OR c.name LIKE '%DOB%'
 OR c.name LIKE '%NSS%'   OR c.name LIKE '%PASSPORT%' OR c.name LIKE '%IDENT%'
ORDER BY t.name, c.name;

/* ---------------------------------------------------------------------------
   2. Row counts before - compare against step 4 to prove nothing was deleted
   --------------------------------------------------------------------------- */

SELECT 'MALADE'   AS [table], COUNT(*) AS [rows] FROM [dbo].[MALADE]
UNION ALL
SELECT 'PERSONEL', COUNT(*) FROM [dbo].[PERSONEL];

/* ---------------------------------------------------------------------------
   3. The scrub

   Deterministic: the same input always produces the same fake value, so a name
   that appears in two tables stays consistent and the joins still make sense.
   --------------------------------------------------------------------------- */

BEGIN TRANSACTION;

-- 3a. Patient names on visits.
-- Keeps the double space that appears in real MedVault data ("ALEX  EXAMPLE"), because the app renders
-- these strings verbatim and the developer should see the same oddity in the test copy.
UPDATE [dbo].[MALADE]
SET NOM = 'PATIENT' + RIGHT('00000' + CAST(ABS(CHECKSUM(ADMIP)) % 99999 AS varchar(5)), 5)
        + '  TEST'
WHERE NOM IS NOT NULL;

-- 3b. Phone numbers.
-- Everyone who shared a number before still shares one after, so the family-group behaviour that
-- API 2 depends on survives the scrub. Range 70000000-79999999 matches the local mobile format.
UPDATE p
SET p.TEL = t.fake
FROM [dbo].[PERSONEL] p
JOIN (
    SELECT DISTINCT
        TEL,
        '7' + RIGHT('0000000' + CAST(ABS(CHECKSUM(TEL)) % 9999999 AS varchar(7)), 7) AS fake
    FROM [dbo].[PERSONEL]
    WHERE TEL IS NOT NULL AND LTRIM(RTRIM(TEL)) <> ''
) t ON t.TEL = p.TEL;

UPDATE p
SET p.TEL1 = t.fake
FROM [dbo].[PERSONEL] p
JOIN (
    SELECT DISTINCT
        TEL1,
        '7' + RIGHT('0000000' + CAST(ABS(CHECKSUM(TEL1)) % 9999999 AS varchar(7)), 7) AS fake
    FROM [dbo].[PERSONEL]
    WHERE TEL1 IS NOT NULL AND LTRIM(RTRIM(TEL1)) <> ''
) t ON t.TEL1 = p.TEL1;

-- 3c. TODO: add every other identifier that step 1 reported.
--     Names on PERSONEL, addresses, emails, national id numbers, dates of birth.
--     A date of birth should be shifted, not blanked - the app displays it and a null would hide a
--     rendering bug that production would then find for you:
--         UPDATE t SET BIRTHDATE = DATEADD(day, ABS(CHECKSUM(<key>)) % 300 - 150, BIRTHDATE);

COMMIT TRANSACTION;

/* ---------------------------------------------------------------------------
   4. Verify - row counts unchanged, and no original identifier survived
   --------------------------------------------------------------------------- */

SELECT 'MALADE'   AS [table], COUNT(*) AS [rows] FROM [dbo].[MALADE]
UNION ALL
SELECT 'PERSONEL', COUNT(*) FROM [dbo].[PERSONEL];

-- Every remaining name must match the generated pattern. Any row here is a scrub that was missed.
SELECT TOP 20 ADMI, NOM
FROM [dbo].[MALADE]
WHERE NOM IS NOT NULL AND NOM NOT LIKE 'PATIENT%  TEST';

-- Every remaining phone must be 8 digits starting with 7.
SELECT TOP 20 ADMI, TEL, TEL1
FROM [dbo].[PERSONEL]
WHERE (TEL  IS NOT NULL AND LTRIM(RTRIM(TEL))  <> '' AND LTRIM(RTRIM(TEL))  NOT LIKE '7[0-9][0-9][0-9][0-9][0-9][0-9][0-9]')
   OR (TEL1 IS NOT NULL AND LTRIM(RTRIM(TEL1)) <> '' AND LTRIM(RTRIM(TEL1)) NOT LIKE '7[0-9][0-9][0-9][0-9][0-9][0-9][0-9]');

-- The family grouping must still exist, or the test copy cannot exercise API 2 properly.
SELECT TOP 10 TEL, COUNT(*) AS patients
FROM [dbo].[PERSONEL]
WHERE TEL IS NOT NULL AND LTRIM(RTRIM(TEL)) <> ''
GROUP BY TEL
HAVING COUNT(*) > 1
ORDER BY COUNT(*) DESC;

PRINT 'Scrub complete. Do not share this copy until the three checks above return what you expect.';
