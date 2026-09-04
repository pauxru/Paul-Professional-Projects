variable "name_prefix" { type = string }
variable "location" { type = string }
variable "resource_group_name" { type = string }
variable "tags" { type = map(string) }
variable "sku" { type = string }
variable "public_network_access_enabled" { type = bool }
