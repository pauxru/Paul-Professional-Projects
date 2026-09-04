output "workspace_id" { value = azurerm_log_analytics_workspace.this.id }
output "workspace_primary_shared_key" {
  value     = azurerm_log_analytics_workspace.this.primary_shared_key
  sensitive = true
}
output "application_insights_connection_string" {
  value     = azurerm_application_insights.this.connection_string
  sensitive = true
}
output "registry_id" { value = azurerm_container_registry.this.id }
output "registry_login_server" { value = azurerm_container_registry.this.login_server }
