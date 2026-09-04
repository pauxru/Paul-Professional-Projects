# Deployment Guide

> Operator guide only. None of these Azure/GitHub deployment commands were run while building this repository. Review names, policy, region, cost and authorization before execution.

## 1. Local validation

```powershell
git clone https://github.com/OWNER/REPO.git
Set-Location .\REPO
dotnet build -c Release
dotnet test -c Release
az bicep build --file .\infra\bicep\main.bicep
```

## 2. Select Azure context

```powershell
az login
az account list --output table
az account set --subscription "<subscription-id>"
az account show --query "{name:name,id:id,tenantId:tenantId}" --output table
```

## 3. Preview and deploy Bicep

Secure values are process-local and must come from the operator’s approved secret workflow:

```powershell
$env:POSTGRES_ADMIN_PASSWORD = Read-Host "Temporary PostgreSQL bootstrap password"
$env:JWT_SIGNING_KEY = Read-Host "JWT signing key (48+ random characters)"

az bicep build-params --file .\infra\bicep\environments\dev.bicepparam
az deployment sub what-if `
  --name "storefront-dev-$(Get-Date -Format yyyyMMddHHmmss)" `
  --location eastus2 `
  --parameters .\infra\bicep\environments\dev.bicepparam

az deployment sub create `
  --name "storefront-dev-$(Get-Date -Format yyyyMMddHHmmss)" `
  --location eastus2 `
  --parameters .\infra\bicep\environments\dev.bicepparam

Remove-Item Env:POSTGRES_ADMIN_PASSWORD,Env:JWT_SIGNING_KEY
```

Repeat with `staging.bicepparam` and `prod.bicepparam` only after review. Use an Azure Policy/What-If gate and a deployment identity allowed to create RBAC assignments.

## 4. Terraform alternative

Terraform was not available on the build host; validate these commands/tool versions in the operator environment:

```powershell
Set-Location .\infra\terraform
$env:TF_VAR_postgres_administrator_password = Read-Host "PostgreSQL bootstrap password"
$env:TF_VAR_jwt_signing_key = Read-Host "JWT signing key"
terraform init -backend-config=backend.example.hcl
terraform fmt -check -recursive
terraform validate
terraform plan -var-file=.\environments\dev.tfvars.json -out=dev.tfplan
terraform apply dev.tfplan
Remove-Item Env:TF_VAR_postgres_administrator_password,Env:TF_VAR_jwt_signing_key
```

Use a unique backend key per environment; never share a state file.

## 5. Build images remotely in ACR

```powershell
$rg = "rg-contoso-storefront-dev"
$acr = az acr list --resource-group $rg --query "[0].name" --output tsv
$tag = git rev-parse HEAD

az acr build `
  --registry $acr `
  --image "storefront-api:$tag" `
  --target api `
  .

az acr build `
  --registry $acr `
  --image "storefront-migrations:$tag" `
  --target migrations `
  .
```

Pin the resulting digest in a production hardening pass; the sample workflow takes an immutable tag.

## 6. Configure GitHub OIDC federation

Create one Entra application/service principal:

```powershell
$owner = "GITHUB_OWNER"
$repo = "GITHUB_REPOSITORY"
$app = az ad app create --display-name "github-$owner-$repo-storefront" | ConvertFrom-Json
$sp = az ad sp create --id $app.appId | ConvertFrom-Json
$tenantId = az account show --query tenantId --output tsv
$subscriptionId = az account show --query id --output tsv
```

Create a federated credential for each protected GitHub Environment:

```powershell
foreach ($environment in @("dev", "staging", "prod")) {
  $credentialFile = ".\federated-$environment.json"
  @{
    name = "github-$environment"
    issuer = "https://token.actions.githubusercontent.com"
    subject = "repo:$owner/$repo`:environment:$environment"
    description = "GitHub Actions $environment environment"
    audiences = @("api://AzureADTokenExchange")
  } | ConvertTo-Json | Set-Content $credentialFile

  az ad app federated-credential create `
    --id $app.appId `
    --parameters $credentialFile
  Remove-Item $credentialFile
}
```

Assign only the existing environment resource groups needed by the deployment workflow:

```powershell
foreach ($environment in @("dev", "staging", "prod")) {
  $scope = "/subscriptions/$subscriptionId/resourceGroups/rg-contoso-storefront-$environment"
  az role assignment create `
    --assignee-object-id $sp.id `
    --assignee-principal-type ServicePrincipal `
    --role "Contributor" `
    --scope $scope
}
```

In a real enterprise, replace `Contributor` with a tested custom deployment role restricted to Container Apps/Jobs and ACR reads.

Create protected environments and non-secret variables:

```powershell
gh auth login
foreach ($environment in @("dev", "staging", "prod")) {
  gh api --method PUT "repos/$owner/$repo/environments/$environment"
  gh variable set AZURE_CLIENT_ID --env $environment --body $app.appId
  gh variable set AZURE_TENANT_ID --env $environment --body $tenantId
  gh variable set AZURE_SUBSCRIPTION_ID --env $environment --body $subscriptionId
}
```

In GitHub settings, add required reviewers to staging/prod and prevent self-review. **Do not create `AZURE_CLIENT_SECRET`; OIDC needs none.**

## 7. Verify identity and secrets

```powershell
$rg = "rg-contoso-storefront-dev"
$vault = az keyvault list --resource-group $rg --query "[0].name" --output tsv
az keyvault secret show --vault-name $vault --name storefront--database--connectionstring --query id --output tsv
az keyvault secret show --vault-name $vault --name storefront--cache--connectionstring --query id --output tsv
az keyvault secret show --vault-name $vault --name storefront--security--signingkey --query id --output tsv
```

Never print secret values.

## 8. Manual migration/deployment sequence

```powershell
$environment = "dev"
$rg = "rg-contoso-storefront-$environment"
$job = "caj-contoso-storefront-$environment-migrations"
$appName = "ca-contoso-storefront-$environment-api"
$tag = git rev-parse HEAD
$loginServer = az acr list -g $rg --query "[0].loginServer" -o tsv

az containerapp job update -g $rg -n $job `
  --image "$loginServer/storefront-migrations:$tag"
$execution = az containerapp job start -g $rg -n $job --query name -o tsv
az containerapp job execution show -g $rg -n $job `
  --job-execution-name $execution --output table

az containerapp update -g $rg -n $appName `
  --image "$loginServer/storefront-api:$tag" `
  --revision-suffix "r$(Get-Date -Format yyyyMMddHHmmss)"
```

Do not shift traffic until the job reports `Succeeded`.

## 9. Progressive traffic

```powershell
$revisions = az containerapp revision list -g $rg -n $appName | ConvertFrom-Json
$stable = ($revisions | Where-Object { $_.properties.trafficWeight -gt 0 } | Select-Object -First 1).name
$candidate = ($revisions | Sort-Object { $_.properties.createdTime } | Select-Object -Last 1).name

.\scripts\deploy-canary.ps1 `
  -ResourceGroup $rg `
  -ContainerApp $appName `
  -StableRevision $stable `
  -CandidateRevision $candidate
```

## 10. GitHub workflow invocation

```powershell
gh workflow run cd.yml --ref main -f image_tag="$(git rev-parse HEAD)"
gh run list --workflow cd.yml --limit 5
gh run watch
```

The reusable environment workflow runs migrations before traffic, executes progressive gates and routes 100% back to the captured stable revision on failure.

## 11. Smoke and evidence

```powershell
$fqdn = az containerapp show -g $rg -n $appName `
  --query properties.configuration.ingress.fqdn -o tsv
Invoke-RestMethod "https://$fqdn/health/live"
Invoke-RestMethod "https://$fqdn/health/ready"
Invoke-RestMethod "https://$fqdn/health/startup"
Invoke-WebRequest "https://$fqdn/metrics"
```

Attach command output, deployment ID, image digest, migration execution ID, revision names and gate metrics to the change record.
