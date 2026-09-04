locals {
  services = {
    key_vault = {
      resource_id = var.key_vault_id
      subresource = "vault"
    }
    registry = {
      resource_id = var.registry_id
      subresource = "registry"
    }
    redis = {
      resource_id = var.redis_id
      subresource = "redisCache"
    }
    service_bus = {
      resource_id = var.service_bus_id
      subresource = "namespace"
    }
  }
}

resource "azurerm_private_endpoint" "this" {
  for_each            = local.services
  name                = "pe-${replace(each.key, "_", "-")}-${var.name_prefix}"
  location            = var.location
  resource_group_name = var.resource_group_name
  subnet_id           = var.subnet_id
  tags                = var.tags

  private_service_connection {
    name                           = each.key
    private_connection_resource_id = each.value.resource_id
    subresource_names              = [each.value.subresource]
    is_manual_connection           = false
  }
}
