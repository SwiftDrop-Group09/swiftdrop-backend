# Local project secrets

The booking test harness uses its own disposable PostgreSQL instance and needs
no Neon, IAM or broker secret. The following commands are for real Development
hosts and the shared infra runner. Supply privately provisioned Neon connection
strings with the correct owning database, role and TLS settings. These commands
store values; they do not create databases, roles or migrations.

## Direct dotnet Development hosts

Run PowerShell from the backend root. All four host projects already declare
UserSecretsId; no user-secrets init or new secret names are needed. This helper
prompts without placing the secret in command history or process arguments and
pipes JSON into the SDK's user-secrets command. It does not print the value.

```powershell
function Set-SwiftDropProjectSecret {
    param([string]$Project, [string]$Key, [string]$InputName)
    $privateInput = Read-Host $InputName -AsSecureString
    $privatePointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($privateInput)
    try {
        $privateValue = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($privatePointer)
        @{ $Key = $privateValue } | ConvertTo-Json -Compress |
            dotnet user-secrets set --project $Project
        if ($LASTEXITCODE -ne 0) { throw "Could not store $InputName." }
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($privatePointer)
        $privateValue = $null
        $privateInput.Dispose()
    }
}

Set-SwiftDropProjectSecret 'src/Services/Identity/SwiftDrop.Identity.Api' 'ConnectionStrings:Database' 'IDENTITY_DATABASE_CONNECTION_STRING'
Set-SwiftDropProjectSecret 'src/Services/Identity/SwiftDrop.Identity.Api' 'ConnectionStrings:MigrationDatabase' 'IDENTITY_MIGRATION_CONNECTION_STRING'
Set-SwiftDropProjectSecret 'src/Services/Delivery/SwiftDrop.Delivery.Api' 'ConnectionStrings:Database' 'DELIVERY_DATABASE_CONNECTION_STRING'
Set-SwiftDropProjectSecret 'src/Services/Merchant/SwiftDrop.Merchant.Api' 'ConnectionStrings:Database' 'MERCHANT_DATABASE_CONNECTION_STRING'
Set-SwiftDropProjectSecret 'src/Workers/SwiftDrop.Notification.Worker' 'ConnectionStrings:Database' 'NOTIFICATIONS_DATABASE_CONNECTION_STRING'
```

ConnectionStrings:Database is deliberately scoped by project. Do not give one
host another service's connection. Identity's migration setting is separate;
Delivery's current explicit migration task uses its existing Delivery setting.
Merchant/Notification settings are reserved for their owning adapters and do not
imply those adapters are implemented. Database configuration alone does not
enable IAM, Customer login or JWT validation.

User secrets are local Development configuration, outside the repository, and
are not an encrypted production vault. Use the same OS account to store them
and run the host. Environment/command-line settings can override user secrets.
Do not run user-secrets list in shared logs because it prints the values.

## Shared runner and Docker Compose

The infra runner/Compose reads the six named inputs from an ignored
swiftdrop-infra/development/.env or private exported environment variables.
It does not import dotnet user-secrets. In host mode it explicitly supplies the
owning ConnectionStrings__Database environment setting, which takes precedence
over user-secrets. Set the infra inputs when using that runner.

From the backend root, create the local file only if it does not already exist:

```powershell
$infraSecretFile = '..\swiftdrop-infra\development\.env'
if (-not (Test-Path -LiteralPath $infraSecretFile)) {
    Copy-Item -LiteralPath '..\swiftdrop-infra\development\.env.example' -Destination $infraSecretFile
}
notepad $infraSecretFile
```

Enter the private values under these exact keys in that ignored file:

```dotenv
IDENTITY_DATABASE_CONNECTION_STRING=<Identity Neon runtime connection>
IDENTITY_MIGRATION_CONNECTION_STRING=<Identity Neon migration connection>
DELIVERY_DATABASE_CONNECTION_STRING=<Delivery Neon connection>
MERCHANT_DATABASE_CONNECTION_STRING=<Merchant Neon connection>
NOTIFICATIONS_DATABASE_CONNECTION_STRING=<Notifications Neon connection>
RABBITMQ_DEFAULT_PASS=<private local RabbitMQ administration password>
```

The placeholders above are instructions, not usable connection strings.
RABBITMQ_DEFAULT_PASS belongs only to RabbitMQ administration; it must not be
stored in an API's user-secrets or used as an application messaging credential.
The runner can generate its local value when the template retains
GENERATE_LOCALLY. Keep populated environment files private and ignored.

## Browser, Flutter and Gateway

No database password, broker password or signing key belongs in these clients or
Gateway. VITE_API_BASE_URL and Flutter API_BASE_URL are public URLs, not secrets.
The isolated test entry points use fixture context from the test harness; normal
applications still require the IAM member's real session/request adapter.
Do not add tokens to VITE_* values, dart-define, source files or test fixtures.
