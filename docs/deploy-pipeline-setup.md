# Setting up the deploy pipeline (GitHub Actions to Azure, no stored secret)

`.github/workflows/deploy.yml` signs in to Azure with **OpenID Connect**: GitHub proves "this job runs in repo X, environment
`dev`" and Azure trusts that through a *federated credential* on an app registration. Nothing secret lives in GitHub, only three
identifiers. The error `Not all values are present. Ensure 'client-id' and 'tenant-id' are supplied` means those variables are
not set (or are set where the job cannot see them).

Replace the placeholders: `<sub>` subscription id, `<tenant>` tenant id, `<appId>` the application (client) id from step 2.
Commands are PowerShell; they also work in Git Bash if you drop the backtick line continuations.

## 1. Provision the infrastructure once (from your machine)

The pipeline only *deploys*; it expects the Azure resources to exist.

```powershell
az login
azd auth login
azd env new dev --location southafricanorth      # the name "dev" is AZURE_ENV_NAME below
azd env set AZURE_BUDGET_EMAIL you@example.com   # optional USD 25 budget alert
azd up                                           # ~10-15 min; creates rg-dev, SQL, Key Vault, ACR, Container App
```

Prerequisites on your machine: Azure CLI, `azd`, PowerShell 7 (`pwsh`), go-sqlcmd (`winget install sqlcmd`).
Check: `azd env get-values | Select-String "APP_FQDN"` then open `https://<fqdn>/healthz`.

## 2. Create the identity the pipeline signs in as

```powershell
$appId = az ad app create --display-name "tc-provider-github-deploy" --query appId -o tsv
az ad sp create --id $appId
$appId        # keep this: it is AZURE_CLIENT_ID
az account show --query "{tenant:tenantId, subscription:id}" -o json    # AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID
```

## 3. Trust GitHub (the federated credential)

The **subject must match exactly**. `deploy.yml` runs in the GitHub *environment* `dev`, so the subject names that environment:

```powershell
@'
{
  "name": "github-env-dev",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:Aston-Integrove/task-center-provider-app:environment:dev",
  "audiences": ["api://AzureADTokenExchange"]
}
'@ | Set-Content -Path fed.json -Encoding utf8
az ad app federated-credential create --id $appId --parameters fed.json
Remove-Item fed.json
```

## 4. Give it just enough permission

Deploying (build image in ACR, update the Container App) needs Contributor on the environment's resource group only:

```powershell
az role assignment create --assignee $appId --role Contributor `
  --scope "/subscriptions/<sub>/resourceGroups/rg-dev"
```

Do not grant subscription-wide rights for this. (Re-running `azd provision` from CI would additionally need *User Access
Administrator*; this pipeline deliberately does not provision.)

## 5. Tell GitHub (variables, not secrets)

Create the environment and its variables. With the GitHub CLI (`gh auth login` once):

```powershell
gh api -X PUT repos/Aston-Integrove/task-center-provider-app/environments/dev
gh variable set AZURE_CLIENT_ID       --env dev --body "<appId>"
gh variable set AZURE_TENANT_ID       --env dev --body "<tenant>"
gh variable set AZURE_SUBSCRIPTION_ID --env dev --body "<sub>"
gh variable set AZURE_ENV_NAME        --env dev --body "dev"
gh variable set AZURE_LOCATION        --env dev --body "southafricanorth"
```

Or in the browser: repository **Settings > Environments > New environment** `dev` > *Environment variables* > add the five.
(Optionally add *Required reviewers* there to approve each deployment.)

Where you set them matters: **environment variables** are visible only to jobs that declare `environment: dev` (the deploy
job does). The PR job `provision-preview` in `ci.yml` has no environment, so if you want it to run, also set the same variables
as **repository variables** (Settings > Secrets and variables > Actions > Variables); without them it is skipped.

## 6. Run it

```powershell
gh workflow run deploy.yml
gh run watch
```

Success looks like: login, *Load provisioned resources*, *Deploy*, then *Post-deploy health check* printing
`{"status":"Healthy"}`. Every later merge to `main` deploys automatically.

## Troubleshooting

| Message | Cause and fix |
|---|---|
| `Not all values are present ... client-id and tenant-id` | Variables missing or in the wrong scope (step 5). The *Check configuration* step now names the missing ones. |
| `AADSTS700213 / AADSTS70021: No matching federated identity record found` | Subject mismatch. It must be exactly `repo:Aston-Integrove/task-center-provider-app:environment:dev` (case-sensitive); re-check step 3 and that the job really uses `environment: dev`. |
| `AuthorizationFailed ... does not have authorization to perform action` | Role missing or wrong scope (step 4); the group is `rg-<AZURE_ENV_NAME>`. |
| `azd deploy`: `no azure resource group` / service not found | Infrastructure was never provisioned in this subscription/environment name (step 1), or `AZURE_ENV_NAME`/`AZURE_LOCATION` differ from what `azd up` used. |
| Health check fails after deploy | Open the Container App log stream; common causes: SQL user for the managed identity missing (the `postprovision` hook did not run), Key Vault secrets absent. Re-run `azd provision` locally. |
| `The subscription is not registered to use namespace Microsoft.App` | One-time: `az provider register -n Microsoft.App -n Microsoft.ContainerRegistry -n Microsoft.OperationalInsights -n Microsoft.Sql --wait` |
