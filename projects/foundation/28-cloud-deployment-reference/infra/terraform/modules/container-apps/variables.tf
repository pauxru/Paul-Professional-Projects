variable "name_prefix" { type = string }
variable "location" { type = string }
variable "resource_group_name" { type = string }
variable "tags" { type = map(string) }
variable "environment_name" { type = string }
variable "log_analytics_workspace_id" { type = string }
variable "log_analytics_primary_shared_key" {
  type      = string
  sensitive = true
}
variable "infrastructure_subnet_id" {
  type    = string
  default = null
}
variable "registry_server" { type = string }
variable "identity_id" { type = string }
variable "key_vault_uri" { type = string }
variable "image_tag" { type = string }
variable "min_replicas" { type = number }
variable "max_replicas" { type = number }
variable "service_bus_fully_qualified_domain" { type = string }
variable "oidc_authority" { type = string }
