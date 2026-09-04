variable "name_prefix" { type = string }
variable "location" { type = string }
variable "resource_group_name" { type = string }
variable "tags" { type = map(string) }
variable "registry_id" { type = string }
variable "service_bus_namespace_id" { type = string }
variable "postgres_host" { type = string }
variable "postgres_administrator_password" {
  type      = string
  sensitive = true
}
variable "redis_connection_string" {
  type      = string
  sensitive = true
}
variable "application_insights_connection_string" {
  type      = string
  sensitive = true
}
variable "jwt_signing_key" {
  type      = string
  sensitive = true
}
variable "public_network_access_enabled" { type = bool }
