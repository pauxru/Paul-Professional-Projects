resource "azurerm_log_analytics_workspace" "this" {
  name                = "log-${var.name_prefix}"
  location            = var.location
  resource_group_name = var.resource_group_name
  sku                 = "PerGB2018"
  retention_in_days   = var.retention_days
  tags                = var.tags
}

resource "azurerm_application_insights" "this" {
  name                = "appi-${var.name_prefix}"
  location            = var.location
  resource_group_name = var.resource_group_name
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"
  local_authentication_disabled = true
  tags                = var.tags
}

resource "azurerm_container_registry" "this" {
  name                          = substr(replace("acr${var.name_prefix}", "-", ""), 0, 50)
  resource_group_name           = var.resource_group_name
  location                      = var.location
  sku                           = var.registry_sku
  admin_enabled                 = false
  public_network_access_enabled = true
  tags                          = var.tags
}
