resource "azurerm_container_app_environment" "this" {
  name                           = "cae-${var.name_prefix}"
  location                       = var.location
  resource_group_name            = var.resource_group_name
  log_analytics_workspace_id     = var.log_analytics_workspace_id
  infrastructure_subnet_id       = var.infrastructure_subnet_id
  internal_load_balancer_enabled = false
  tags                           = var.tags
}

locals {
  secret_uris = {
    database             = "${var.key_vault_uri}secrets/storefront--database--connectionstring"
    redis                = "${var.key_vault_uri}secrets/storefront--cache--connectionstring"
    application_insights = "${var.key_vault_uri}secrets/storefront--observability--applicationinsightsconnectionstring"
    jwt                  = "${var.key_vault_uri}secrets/storefront--security--signingkey"
  }
}

resource "azurerm_container_app" "api" {
  name                         = "ca-${var.name_prefix}-api"
  container_app_environment_id = azurerm_container_app_environment.this.id
  resource_group_name          = var.resource_group_name
  revision_mode                = "Multiple"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  registry {
    server   = var.registry_server
    identity = var.identity_id
  }

  dynamic "secret" {
    for_each = local.secret_uris
    content {
      name                = replace(secret.key, "_", "-")
      identity            = var.identity_id
      key_vault_secret_id = secret.value
    }
  }

  ingress {
    external_enabled           = true
    target_port                = 8080
    allow_insecure_connections = false

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    http_scale_rule {
      name                = "http"
      concurrent_requests = 50
    }

    container {
      name   = "api"
      image  = "${var.registry_server}/storefront-api:${var.image_tag}"
      cpu    = 0.5
      memory = "1Gi"

      env { name = "ASPNETCORE_ENVIRONMENT" value = "Production" }
      env { name = "ASPNETCORE_URLS" value = "http://+:8080" }
      env { name = "Database__Provider" value = "Postgres" }
      env { name = "Database__ConnectionString" secret_name = "database" }
      env { name = "Cache__Provider" value = "Redis" }
      env { name = "Cache__ConnectionString" secret_name = "redis" }
      env { name = "Messaging__Provider" value = "ServiceBus" }
      env { name = "Messaging__FullyQualifiedNamespace" value = var.service_bus_fully_qualified_domain }
      env { name = "Messaging__QueueName" value = "storefront-orders" }
      env { name = "KeyVault__Enabled" value = "true" }
      env { name = "KeyVault__VaultUri" value = var.key_vault_uri }
      env { name = "Security__SigningKey" secret_name = "jwt" }
      env { name = "Security__Authority" value = var.oidc_authority }
      env { name = "Observability__Exporter" value = "AzureMonitor" }
      env { name = "Observability__ApplicationInsightsConnectionString" secret_name = "application-insights" }
      env { name = "Deployment__Environment" value = var.environment_name }

      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/startup"
        interval_seconds        = 2
        failure_count_threshold = 30
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 10
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/ready"
        interval_seconds        = 5
        failure_count_threshold = 3
      }
    }
  }
}

resource "azurerm_container_app_job" "migrations" {
  name                         = "caj-${var.name_prefix}-migrations"
  location                     = var.location
  resource_group_name          = var.resource_group_name
  container_app_environment_id = azurerm_container_app_environment.this.id
  replica_timeout_in_seconds   = 1800
  replica_retry_limit          = 1
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  registry {
    server   = var.registry_server
    identity = var.identity_id
  }

  secret {
    name                = "database"
    identity            = var.identity_id
    key_vault_secret_id = local.secret_uris.database
  }

  template {
    container {
      name   = "migration-runner"
      image  = "${var.registry_server}/storefront-migrations:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"
      env { name = "Database__Provider" value = "Postgres" }
      env { name = "Database__ConnectionString" secret_name = "database" }
    }
  }
}
