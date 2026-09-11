<#
    Read-only probe of the Bekaa HIS, to settle the assumptions the API currently makes.

    Answers four questions:
      1. Does TRLAB.RETAT actually distinguish released results from pending ones?
         The order status (and therefore MedVault's result-ready notification) depends on it.
      2. Does TRLAB.DATEV behave like a release timestamp?
      3. Is a date of birth stored anywhere in MALADE / PERSONEL?
      4. What are the test codes, names, units and categories? (starter for the reference-range catalogue)

    Every statement is a SELECT. Nothing is written. Questions 1-3 return counts and dates only -
    no patient rows, no names, no results. Question 4 returns the test catalogue, which is
    reference data, not patient data.

    Usage, on the machine that can reach the database:

        powershell -ExecutionPolicy Bypass -File probe-his.ps1 -Server 192.168.10.239

    It will prompt for the login. Use a SELECT-only account if one exists.
    Output goes to his-probe-output.txt next to the script.
#>

param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$OutFile = "$PSScriptRoot\his-probe-output.txt"
)

$cred = Get-Credential -Message "SQL Server login for $Server (use a SELECT-only account if you have one)"

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder['Data Source'] = $Server
$builder['Initial Catalog'] = 'DATA1'
$builder['User ID'] = $cred.UserName
$builder['Password'] = $cred.GetNetworkCredential().Password
$builder['TrustServerCertificate'] = $true
$builder['Connect Timeout'] = 15
$builder['ApplicationIntent'] = 'ReadOnly'

$queries = [ordered]@{

    '1a. RETAT distribution - if this is all one value, RETAT is not the release marker' = @'
SELECT RETAT, COUNT(*) AS rows_with_this_value
FROM [DATA1].dbo.TRLAB
GROUP BY RETAT
ORDER BY COUNT(*) DESC
'@

    '1b. RETAT split for orders created in the last 60 days - pending work should show a non-1 value' = @'
SELECT r.RETAT, COUNT(*) AS rows_with_this_value
FROM [DATA1].dbo.TRLAB r
JOIN [DATA1].dbo.MALADE m ON m.ADMI COLLATE DATABASE_DEFAULT = r.ADMI COLLATE DATABASE_DEFAULT
WHERE m.DATEIN >= DATEADD(day, -60, GETDATE())
GROUP BY r.RETAT
'@

    '1c. Orders that are entirely unreleased vs partly or fully released (last 60 days)' = @'
SELECT CASE WHEN rel.released = 0 THEN 'no released rows (pending)'
            WHEN rel.released = rel.total THEN 'all rows released'
            ELSE 'partly released' END AS order_state,
       COUNT(*) AS orders
FROM [DATA1].dbo.MALADE m
CROSS APPLY (SELECT COUNT(*) AS total,
                    SUM(CASE WHEN t.RETAT = 1 THEN 1 ELSE 0 END) AS released
             FROM [DATA1].dbo.TRLAB t
             WHERE t.ADMI COLLATE DATABASE_DEFAULT = m.ADMI COLLATE DATABASE_DEFAULT) rel
WHERE SUBSTRING(m.ADMI, 3, 1) = 'L'
  AND m.DATEIN >= DATEADD(day, -60, GETDATE())
  AND rel.total > 0
GROUP BY CASE WHEN rel.released = 0 THEN 'no released rows (pending)'
              WHEN rel.released = rel.total THEN 'all rows released'
              ELSE 'partly released' END
'@

    '2a. DATEV coverage by year, and whether it lines up with RETAT' = @'
SELECT YEAR(m.DATEIN) AS order_year,
       COUNT(*) AS result_rows,
       SUM(CASE WHEN r.DATEV IS NOT NULL THEN 1 ELSE 0 END) AS has_datev,
       SUM(CASE WHEN r.RETAT = 1 THEN 1 ELSE 0 END) AS retat_1,
       SUM(CASE WHEN r.RETAT = 1 AND r.DATEV IS NOT NULL THEN 1 ELSE 0 END) AS both
FROM [DATA1].dbo.TRLAB r
JOIN [DATA1].dbo.MALADE m ON m.ADMI COLLATE DATABASE_DEFAULT = r.ADMI COLLATE DATABASE_DEFAULT
WHERE m.DATEIN >= '2018-01-01'
GROUP BY YEAR(m.DATEIN)
ORDER BY order_year
'@

    '2b. Is DATEV plausibly later than the order date? (2026 rows only)' = @'
SELECT TOP 20
       DATEDIFF(minute, m.DATEIN, r.DATEV) AS minutes_from_order_to_datev
FROM [DATA1].dbo.TRLAB r
JOIN [DATA1].dbo.MALADE m ON m.ADMI COLLATE DATABASE_DEFAULT = r.ADMI COLLATE DATABASE_DEFAULT
WHERE r.DATEV IS NOT NULL AND m.DATEIN >= '2026-01-01'
ORDER BY m.DATEIN DESC
'@

    '3. Any date-of-birth-shaped column in the patient tables (names only, no data)' = @'
SELECT c.TABLE_CATALOG AS db_name, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE
FROM [DATA1].INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_NAME IN ('MALADE')
  AND (c.DATA_TYPE IN ('date','datetime','smalldatetime')
       OR c.COLUMN_NAME LIKE '%NAIS%' OR c.COLUMN_NAME LIKE '%BIRTH%'
       OR c.COLUMN_NAME LIKE '%DOB%'  OR c.COLUMN_NAME LIKE '%AGE%')
UNION ALL
SELECT c.TABLE_CATALOG, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE
FROM [DPERSONEL].INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_NAME IN ('PERSONEL')
  AND (c.DATA_TYPE IN ('date','datetime','smalldatetime')
       OR c.COLUMN_NAME LIKE '%NAIS%' OR c.COLUMN_NAME LIKE '%BIRTH%'
       OR c.COLUMN_NAME LIKE '%DOB%'  OR c.COLUMN_NAME LIKE '%AGE%')
ORDER BY db_name, TABLE_NAME, COLUMN_NAME
'@

    '4. Test catalogue - the starter list for reference-ranges.json' = @'
SELECT LTRIM(RTRIM(d.CODE)) AS code,
       LTRIM(RTRIM(d.DESCRIP)) AS test_name,
       LTRIM(RTRIM(ISNULL(d.UNITE, ''))) AS unit,
       LTRIM(RTRIM(ISNULL(n.DESCRIP, ''))) AS category,
       used.times_used
FROM [DATAPRIX].dbo.RLAB1 d
LEFT JOIN [DATAPRIX].dbo.NOMLAB n
       ON n.CODE COLLATE DATABASE_DEFAULT = d.LAB COLLATE DATABASE_DEFAULT
OUTER APPLY (SELECT COUNT(*) AS times_used
             FROM [DATA1].dbo.TRLAB t
             WHERE t.CODE COLLATE DATABASE_DEFAULT = d.CODE COLLATE DATABASE_DEFAULT) used
WHERE NULLIF(LTRIM(RTRIM(d.DESCRIP)), '') IS NOT NULL
ORDER BY used.times_used DESC, test_name
'@

    '5. Phone number format in PERSONEL.TEL - shape only, digits masked' = @'
SELECT LEN(LTRIM(RTRIM(p.TEL))) AS digits_in_number,
       COUNT(*) AS accounts,
       MAX(CASE WHEN LEFT(LTRIM(RTRIM(p.TEL)), 3) = '961' THEN 'starts with 961'
                ELSE 'no country code' END) AS shape
FROM [DPERSONEL].dbo.PERSONEL p
WHERE NULLIF(LTRIM(RTRIM(ISNULL(p.TEL, ''))), '') IS NOT NULL
GROUP BY LEN(LTRIM(RTRIM(p.TEL)))
ORDER BY accounts DESC
'@
}

$report = New-Object System.Text.StringBuilder
$null = $report.AppendLine("MedVault HIS probe - $Server - $(Get-Date -Format 'yyyy-MM-dd HH:mm')")

$connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
try {
    $connection.Open()
    Write-Host "Connected to $Server." -ForegroundColor Green
}
catch {
    Write-Host "Could not connect: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

foreach ($name in $queries.Keys) {
    Write-Host "Running: $name"
    $null = $report.AppendLine().AppendLine("=== $name ===")

    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $queries[$name]
        $command.CommandTimeout = 120

        $table = New-Object System.Data.DataTable
        $table.Load($command.ExecuteReader())

        $text = $table | Format-Table -AutoSize | Out-String -Width 200
        $null = $report.AppendLine($text.Trim())
        $null = $report.AppendLine("($($table.Rows.Count) rows)")
    }
    catch {
        # A query that fails is itself an answer - usually "that column does not exist here".
        $null = $report.AppendLine("FAILED: $($_.Exception.Message)")
        Write-Host "  failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

$connection.Close()

$report.ToString() | Out-File -FilePath $OutFile -Encoding utf8
Write-Host ""
Write-Host "Written to $OutFile" -ForegroundColor Green
Write-Host "Read it before sending it on, and check nothing patient-identifying slipped in." -ForegroundColor Yellow
