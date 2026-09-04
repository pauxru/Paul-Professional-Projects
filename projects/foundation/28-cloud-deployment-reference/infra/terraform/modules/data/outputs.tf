output "postgres_host" { value = azurerm_postgresql_flexible_server.this.fqdn }
output "redis_host" { value = azurerm_redis_cache.this.hostname }
output "redis_id" { value = azurerm_redis_cache.this.id }
output "redis_connection_string" {
  value     = "${azurerm_redis_cache.this.hostname}:6380,password=${azurerm_redis_cache.this.primary_access_key},ssl=True,abortConnect=False"
  sensitive = true
}
