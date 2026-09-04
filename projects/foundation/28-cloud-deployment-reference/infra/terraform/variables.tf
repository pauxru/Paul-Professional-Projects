variable "environment" {
  type = string
  validation {
    condition     = contains(["dev", "staging", "prod"], var.environment)
    error_message = "Environment must be dev, staging, or prod."
  }
}

variable "location" {
  type    = string
  default = "eastus2"
}

variable "name_prefix" {
  type    = string
  default = "contoso-storefront"
}

variable "required_tags" {
  type = map(string)
}

variable "container_min_replicas" {
  type = number
}

variable "container_max_replicas" {
  type = number
}

variable "postgres_sku_name" {
  type = string
}

variable "postgres_storage_mb" {
  type = number
}

variable "postgres_zone_redundant" {
  type = bool
}

variable "redis_sku_name" {
  type = string
}

variable "redis_family" {
  type = string
}

variable "redis_capacity" {
  type = number
}

variable "service_bus_sku" {
  type = string
}

variable "use_private_endpoints" {
  type = bool
}

variable "image_tag" {
  type    = string
  default = "latest"
}

variable "postgres_administrator_password" {
  type      = string
  sensitive = true
}

variable "jwt_signing_key" {
  type      = string
  sensitive = true
}
