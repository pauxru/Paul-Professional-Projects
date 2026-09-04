resource "azurerm_servicebus_namespace" "this" {
  name                          = substr("sb-${var.name_prefix}", 0, 50)
  location                      = var.location
  resource_group_name           = var.resource_group_name
  sku                           = var.sku
  minimum_tls_version           = "1.2"
  local_auth_enabled            = false
  public_network_access_enabled = var.public_network_access_enabled
  zone_redundant                = var.sku == "Premium"
  tags                          = var.tags
}

resource "azurerm_servicebus_queue" "orders" {
  name                                    = "storefront-orders"
  namespace_id                            = azurerm_servicebus_namespace.this.id
  lock_duration                           = "PT1M"
  max_delivery_count                      = 10
  dead_lettering_on_message_expiration     = true
  default_message_ttl                     = "P14D"
  partitioning_enabled                    = var.sku != "Premium"
  requires_duplicate_detection            = true
  duplicate_detection_history_time_window = "PT10M"
}
