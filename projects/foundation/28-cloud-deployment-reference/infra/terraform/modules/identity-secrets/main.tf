data "azurerm_client_config" "current" {}

resource "azurerm_user_assigned_identity" "this" {
  name                = "id-${var.name_prefix}"
  location            = var.location
  resource_group_name = var.resource_group_name
  tags                = var.tags
}

resource "azurerm_key_vault" "this" {
  name                          = substr(replace("kv-${var.name_prefix}", "-", ""), 0, 24)
  location                      = var.location
  resource_group_name           = var.resource_group_name
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  purge_protection_enabled      = contains(var.name_prefix, "prod")
  soft_delete_retention_days    = 90
  public_network_access_enabled = var.public_network_access_enabled
  tags                          = var.tags
}

resource "azurerm_role_assignment" "key_vault_secrets_user" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.this.principal_id
}

resource "azurerm_role_assignment" "acr_pull" {
  scope                = var.registry_id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.this.principal_id
}

resource "azurerm_role_assignment" "service_bus_sender" {
  scope                = var.service_bus_namespace_id
  role_definition_name = "Azure Service Bus Data Sender"
  principal_id         = azurerm_user_assigned_identity.this.principal_id
}

resource "azurerm_role_assignment" "service_bus_receiver" {
  scope                = var.service_bus_namespace_id
  role_definition_name = "Azure Service Bus Data Receiver"
  principal_id         = azurerm_user_assigned_identity.this.principal_id
}

resource "azurerm_key_vault_secret" "database" {
  name         = "storefront--database--connectionstring"
  value        = "Host=${var.postgres_host};Port=5432;Database=storefront;Username=storefrontadmin;Password=${var.postgres_administrator_password};SSL Mode=Require;Trust Server Certificate=false"
  key_vault_id = azurerm_key_vault.this.id
}

resource "azurerm_key_vault_secret" "redis" {
  name         = "storefront--cache--connectionstring"
  value        = var.redis_connection_string
  key_vault_id = azurerm_key_vault.this.id
}

resource "azurerm_key_vault_secret" "application_insights" {
  name         = "storefront--observability--applicationinsightsconnectionstring"
  value        = var.application_insights_connection_string
  key_vault_id = azurerm_key_vault.this.id
}

resource "azurerm_key_vault_secret" "jwt" {
  name         = "storefront--security--signingkey"
  value        = var.jwt_signing_key
  key_vault_id = azurerm_key_vault.this.id
}
