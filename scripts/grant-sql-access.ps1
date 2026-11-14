# Grant Azure SQL data-plane access to the App Service / Container App managed identity.
#
# USAGE: Copy this file to scripts/grant-sql-access.ps1 in your project root and add
#        a postprovision hook in azure.yaml:
#
#   hooks:
#     postprovision:
#       posix:
#         shell: sh
#         run: ./scripts/grant-sql-access.sh
#       windows:
#         shell: pwsh
#         run: ./scripts/grant-sql-access.ps1
#
# ENVIRONMENT VARIABLES (sourced from azd env):
#   SQL_SERVER           - SQL server name (without .database.windows.net)
#   SQL_DATABASE         - Database name
#   AZURE_RESOURCE_GROUP - Resource group name
#   SERVICE_WEB_NAME     - App Service name (used when set, takes priority)
#   SERVICE_API_NAME     - API service name (fallback when SERVICE_WEB_NAME is not set)
#   SQL_GRANT_DDLADMIN   - Set to "true" to also grant db_ddladmin (needed for EF migrations)
#
# EXIT CODES: 0 = success, 1 = validation or command failure (no arguments accepted).

# Load azd environment variables
$AzdEnvironment = azd env get-values
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to read azd environment values."
    exit 1
}

foreach ($line in $AzdEnvironment) {
    if ([string]::IsNullOrEmpty($line)) { continue }
    $name, $value = $line.Split('=', 2)
    if ($null -eq $value) {
        Write-Error "Invalid azd environment entry."
        exit 1
    }
    if ($value.Length -ge 2 -and
        (($value.StartsWith('"') -and $value.EndsWith('"')) -or
         ($value.StartsWith("'") -and $value.EndsWith("'")))) {
        $value = $value.Substring(1, $value.Length - 2)
    }
    try {
        Set-Item "env:$name" $value -ErrorAction Stop
    }
    catch {
        Write-Error "Failed to load azd environment entry: $_"
        exit 1
    }
}

if ($env:SQL_PUBLIC_ACCESS -eq 'Disabled') {
    Write-Host "SQL Server $($env:SQL_SERVER) uses Private Endpoint (publicNetworkAccess=Disabled) with Managed Identity $($env:MANAGED_IDENTITY_NAME) configured as Microsoft Entra SQL Administrator. Skipping external client SQL grant."
    exit 0
}

# Determine managed identity name (User-Assigned MANAGED_IDENTITY_NAME takes priority, then App Service / API name)
$AppName = if ($env:MANAGED_IDENTITY_NAME) {
    $env:MANAGED_IDENTITY_NAME
} elseif ($env:SERVICE_WEB_NAME) {
    $env:SERVICE_WEB_NAME
} else {
    $env:SERVICE_API_NAME
}

if (-not $AppName) {
    Write-Error "Neither MANAGED_IDENTITY_NAME, SERVICE_WEB_NAME, nor SERVICE_API_NAME is set in azd environment."
    exit 1
}

if ($AppName -cnotmatch '\A[a-zA-Z0-9][a-zA-Z0-9-]{0,58}[a-zA-Z0-9]\z') {
    Write-Error "App name must be 2-60 ASCII letters, digits or hyphens, starting and ending with a letter or digit."
    exit 1
}

$AppSqlLiteral = $AppName.Replace("'", "''")
$AppSqlIdentifier = $AppName.Replace(']', ']]')

Write-Host "Granting SQL data-plane access to managed identity: $AppName"

# Build idempotent SQL grant queries (reader + writer, required for all apps)
$SqlQuery = @"
IF NOT EXISTS (SELECT * FROM sys.database_principals WHERE name = N'$AppSqlLiteral')
  CREATE USER [$AppSqlIdentifier] FROM EXTERNAL PROVIDER;

IF NOT EXISTS (
  SELECT 1 FROM sys.database_role_members drm
  JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id
  JOIN sys.database_principals m ON drm.member_principal_id = m.principal_id
  WHERE r.name = 'db_datareader' AND m.name = N'$AppSqlLiteral'
)
  ALTER ROLE db_datareader ADD MEMBER [$AppSqlIdentifier];

IF NOT EXISTS (
  SELECT 1 FROM sys.database_role_members drm
  JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id
  JOIN sys.database_principals m ON drm.member_principal_id = m.principal_id
  WHERE r.name = 'db_datawriter' AND m.name = N'$AppSqlLiteral'
)
  ALTER ROLE db_datawriter ADD MEMBER [$AppSqlIdentifier];
"@

# Optionally grant db_ddladmin (needed when EF Core migrations run at startup or via hook)
$GrantDdlAdmin = $env:SQL_GRANT_DDLADMIN -eq 'true'
if ($GrantDdlAdmin) {
    $SqlQuery += @"

IF NOT EXISTS (
  SELECT 1 FROM sys.database_role_members drm
  JOIN sys.database_principals r ON drm.role_principal_id = r.principal_id
  JOIN sys.database_principals m ON drm.member_principal_id = m.principal_id
  WHERE r.name = 'db_ddladmin' AND m.name = N'$AppSqlLiteral'
)
  ALTER ROLE db_ddladmin ADD MEMBER [$AppSqlIdentifier];
"@
}

$TempFirewallRule = "TempPostProvisionClientIp"
$AddedFirewallRule = $false

try {
    if ($env:AZURE_RESOURCE_GROUP -and $env:SQL_SERVER) {
        try {
            $ClientIp = (Invoke-RestMethod -Uri "https://api.ipify.org" -TimeoutSec 10).Trim()
            if ($ClientIp -match '^\d{1,3}(\.\d{1,3}){3}$') {
                Write-Host "Temporarily allowing client IP $ClientIp on SQL Server $($env:SQL_SERVER)..."
                az sql server firewall-rule create `
                  --resource-group $env:AZURE_RESOURCE_GROUP `
                  --server $env:SQL_SERVER `
                  --name $TempFirewallRule `
                  --start-ip-address $ClientIp `
                  --end-ip-address $ClientIp `
                  --output none
                if ($LASTEXITCODE -eq 0) { $AddedFirewallRule = $true }
            }
        }
        catch {
            Write-Warning "Could not determine or add temporary client IP firewall rule: $_"
        }
    }

    $AccessToken = (az account get-access-token --resource "https://database.windows.net/" --query accessToken -o tsv).Trim()
    if (-not $AccessToken) {
        Write-Error "Failed to acquire Entra access token for Azure SQL Database via Azure CLI."
        exit 1
    }

    $ConnStr = "Server=tcp:$($env:SQL_SERVER).database.windows.net,1433;Database=$($env:SQL_DATABASE);Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;"
    $MaxAttempts = 4
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $Conn = [System.Data.SqlClient.SqlConnection]::new($ConnStr)
            $Conn.AccessToken = $AccessToken
            $Conn.Open()
            try {
                $Cmd = $Conn.CreateCommand()
                $Cmd.CommandTimeout = 120
                $Cmd.CommandText = $SqlQuery
                [void]$Cmd.ExecuteNonQuery()
            }
            finally {
                $Conn.Dispose()
            }
            break
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                Write-Error "SQL grant query failed after $MaxAttempts attempts: $_"
                exit 1
            }
            Write-Host "SQL database waking up or firewall propagating (attempt $attempt/$MaxAttempts); retrying in 10s..."
            Start-Sleep -Seconds 10
        }
    }

    Write-Host "SQL access granted successfully."
}
finally {
    if ($AddedFirewallRule) {
        az sql server firewall-rule delete `
          --resource-group $env:AZURE_RESOURCE_GROUP `
          --server $env:SQL_SERVER `
          --name $TempFirewallRule `
          --output none 2>$null
    }
}
