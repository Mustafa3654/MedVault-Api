param(
    [string]$DoctorMobile = '76850664'
)

$settingsPath = Join-Path $PSScriptRoot 'appsettings.json'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    $settingsPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'publish-doctor-login\appsettings.json'
}

if (-not (Test-Path -LiteralPath $settingsPath)) {
    throw 'Could not find appsettings.json. Put this script beside appsettings.json in the publish folder, or keep the publish-doctor-login folder beside the scripts folder.'
}

$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$connection = [System.Data.SqlClient.SqlConnection]::new($settings.ConnectionStrings.His)

$query = @"
SELECT
    d.ADMI AS doctor_code,
    d.NOM AS doctor_name,
    COUNT(DISTINCT NULLIF(LTRIM(RTRIM(m.ADMIP)), '')) AS distinct_patients,
    COUNT(m.ADMI) AS total_visits,
    COUNT(DISTINCT CASE
        WHEN SUBSTRING(m.ADMI, 3, 1) = 'L' THEN NULLIF(LTRIM(RTRIM(m.ADMIP)), '')
    END) AS patients_with_lab_visits
FROM DATAPRIX.dbo.DOCTORS d
LEFT JOIN DATA1.dbo.MALADE m
    ON LTRIM(RTRIM(m.CODEDOCT)) = LTRIM(RTRIM(d.ADMI))
WHERE
    REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(d.TEL, ''))), ' ', ''), '-', ''), '/', ''), '.', ''), '(', ''), ')', '') = @mobile
    OR REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(ISNULL(d.TEL1, ''))), ' ', ''), '-', ''), '/', ''), '.', ''), '(', ''), ')', '') = @mobile
GROUP BY d.ADMI, d.NOM;
"@

try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = $query
    $null = $command.Parameters.Add('@mobile', [System.Data.SqlDbType]::VarChar, 50)
    $command.Parameters['@mobile'].Value = $DoctorMobile

    $reader = $command.ExecuteReader()
    $table = [System.Data.DataTable]::new()
    $table.Load($reader)

    if ($table.Rows.Count -eq 0) {
        Write-Output "No doctor record matched mobile number $DoctorMobile in DATAPRIX.dbo.DOCTORS.TEL/TEL1."
    }
    else {
        $table | Format-Table -AutoSize
    }
}
catch {
    Write-Error "Could not query SQL Server. Check that this computer can reach the configured server and that the SQL account can read DATAPRIX and DATA1. $($_.Exception.GetBaseException().Message)"
    exit 1
}
finally {
    if ($connection.State -ne [System.Data.ConnectionState]::Closed) {
        $connection.Close()
    }
    $connection.Dispose()
}
