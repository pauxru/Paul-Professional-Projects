variable "name_prefix" { type = string }
variable "location" { type = string }
variable "resource_group_name" { type = string }
variable "tags" { type = map(string) }
variable "postgres_sku_name" { type = string }
variable "postgres_storage_mb" { type = number }
variable "postgres_zone_redundant" { type = bool }
variable "postgres_administrator_password" {
  type      = string
  sensitive = true
}
variable "redis_sku_name" { type = string }
variable "redis_family" { type = string }
variable "redis_capacity" { type = number }
variable "delegated_subnet_id" {
  type    = string
  default = null
}
variable "private_dns_zone_id" {
  type    = string
  default = null
}
variable "public_network_access_enabled" { type = bool }
