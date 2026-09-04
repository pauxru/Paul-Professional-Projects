resource "azurerm_postgresql_flexible_server" "this" {
  name                          = substr("psql-${var.name_prefix}", 0, 63)
  resource_group_name           = var.resource_group_name
  location                      = var.location
  version                       = "16"
  administrator_login           = "storefrontadmin"
  administrator_password        = var.postgres_administrator_password
  zone                          = "1"
  storage_mb                    = var.postgres_storage_mb
  sku_name                      = var.postgres_sku_name
  backup_retention_days         = var.postgres_zone_redundant ? 35 : 7
  geo_redundant_backup_enabled  = var.postgres_zone_redundant
  delegated_subnet_id           = var.delegated_subnet_id
  private_dns_zone_id           = var.private_dns_zone_id
  public_network_access_enabled = var.public_network_access_enabled
  tags                          = var.tags

  dynamic "high_availability" {
    for_each = var.postgres_zone_redundant ? [1] : []
    content {
      mode                      = "ZoneRedundant"
      standby_availability_zone = "2"
    }
  }
}

resource "azurerm_postgresql_flexible_server_database" "storefront" {
  name      = "storefront"
  server_id = azurerm_postgresql_flexible_server.this.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

resource "azurerm_redis_cache" "this" {
  name                          = substr("redis-${var.name_prefix}", 0, 63)
  location                      = var.location
  resource_group_name           = var.resource_group_name
  capacity                      = var.redis_capacity
  family                        = var.redis_family
  sku_name                      = var.redis_sku_name
  non_ssl_port_enabled          = false
  minimum_tls_version           = "1.2"
  public_network_access_enabled = var.public_network_access_enabled
  tags                          = var.tags

  redis_configuration {
    maxmemory_policy = "volatile-lru"
  }
}
