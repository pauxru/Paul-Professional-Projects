# Terraform Reference

This directory mirrors the Bicep architecture with root composition, environment JSON variable files, modules and an Azure Storage remote-state example.

Expected operator sequence:

```powershell
$env:TF_VAR_postgres_administrator_password = "<approved-secret-source>"
$env:TF_VAR_jwt_signing_key = "<approved-secret-source>"
terraform init -backend-config=backend.example.hcl
terraform fmt -check -recursive
terraform validate
terraform plan -var-file=environments\dev.tfvars.json
```

The Terraform binary was unavailable on the build host. None of these commands ran; syntax/provider compatibility is unverified. No state or Azure resource was created.
