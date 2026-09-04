locals {
  suffix = "${var.name_prefix}-${var.environment}"
}

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "this" {
  name     = "rg-${local.suffix}"
  location = var.location
  tags     = var.required_tags
}

module "network" {
  source              = "./modules/network"
  name_prefix         = local.suffix
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  tags                = var.required_tags
}

module "observability" {
  source              = "./modules/observability"
  name_prefix         = local.suffix
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  tags                = var.required_tags
  retention_days      = var.environment == "prod" ? 90 : 30
  registry_sku        = var.environment == "prod" ? "Premium" : "Basic"
}

module "data" {
  source                          = "./modules/data"
  name_prefix                     = local.suffix
  location                        = azurerm_resource_group.this.location
  resource_group_name             = azurerm_resource_group.this.name
  tags                            = var.required_tags
  postgres_sku_name               = var.postgres_sku_name
  postgres_storage_mb             = var.postgres_storage_mb
  postgres_zone_redundant         = var.postgres_zone_redundant
  postgres_administrator_password = var.postgres_administrator_password
  redis_sku_name                  = var.redis_sku_name
  redis_family                    = var.redis_family
  redis_capacity                  = var.redis_capacity
  delegated_subnet_id             = var.use_private_endpoints ? module.network.database_subnet_id : null
  private_dns_zone_id             = var.use_private_endpoints ? module.network.postgres_private_dns_zone_id : null
  public_network_access_enabled   = !var.use_private_endpoints
}

module "messaging" {
  source                        = "./modules/messaging"
  name_prefix                   = local.suffix
  location                      = azurerm_resource_group.this.location
  resource_group_name           = azurerm_resource_group.this.name
  tags                          = var.required_tags
  sku                           = var.service_bus_sku
  public_network_access_enabled = !var.use_private_endpoints
}

module "identity_secrets" {
  source                                 = "./modules/identity-secrets"
  name_prefix                            = local.suffix
  location                               = azurerm_resource_group.this.location
  resource_group_name                    = azurerm_resource_group.this.name
  tags                                   = var.required_tags
  registry_id                            = module.observability.registry_id
  service_bus_namespace_id               = module.messaging.namespace_id
  postgres_host                          = module.data.postgres_host
  postgres_administrator_password        = var.postgres_administrator_password
  redis_connection_string                = module.data.redis_connection_string
  application_insights_connection_string = module.observability.application_insights_connection_string
  jwt_signing_key                        = var.jwt_signing_key
  public_network_access_enabled          = !var.use_private_endpoints
}

module "container_apps" {
  source                             = "./modules/container-apps"
  name_prefix                        = local.suffix
  location                           = azurerm_resource_group.this.location
  resource_group_name                = azurerm_resource_group.this.name
  tags                               = var.required_tags
  environment_name                   = var.environment
  log_analytics_workspace_id         = module.observability.workspace_id
  log_analytics_primary_shared_key   = module.observability.workspace_primary_shared_key
  infrastructure_subnet_id           = var.use_private_endpoints ? module.network.container_apps_subnet_id : null
  registry_server                    = module.observability.registry_login_server
  identity_id                        = module.identity_secrets.identity_id
  key_vault_uri                      = module.identity_secrets.key_vault_uri
  image_tag                          = var.image_tag
  min_replicas                       = var.container_min_replicas
  max_replicas                       = var.container_max_replicas
  service_bus_fully_qualified_domain = module.messaging.fully_qualified_namespace
  oidc_authority                     = "https://login.microsoftonline.com/${data.azurerm_client_config.current.tenant_id}/v2.0"

  depends_on = [module.identity_secrets]
}

module "private_endpoints" {
  count               = var.use_private_endpoints ? 1 : 0
  source              = "./modules/private-endpoints"
  name_prefix         = local.suffix
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  tags                = var.required_tags
  subnet_id           = module.network.private_endpoint_subnet_id
  key_vault_id        = module.identity_secrets.key_vault_id
  registry_id         = module.observability.registry_id
  redis_id            = module.data.redis_id
  service_bus_id      = module.messaging.namespace_id
}
