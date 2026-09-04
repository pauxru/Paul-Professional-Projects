output "resource_group_name" {
  value = azurerm_resource_group.this.name
}

output "api_fqdn" {
  value = module.container_apps.api_fqdn
}

output "registry_login_server" {
  value = module.observability.registry_login_server
}

output "key_vault_uri" {
  value = module.identity_secrets.key_vault_uri
}
