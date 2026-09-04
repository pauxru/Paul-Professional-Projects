output "api_fqdn" { value = azurerm_container_app.api.ingress[0].fqdn }
output "migration_job_name" { value = azurerm_container_app_job.migrations.name }
