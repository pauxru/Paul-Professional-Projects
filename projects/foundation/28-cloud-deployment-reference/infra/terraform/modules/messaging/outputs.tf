output "namespace_id" { value = azurerm_servicebus_namespace.this.id }
output "fully_qualified_namespace" { value = "${azurerm_servicebus_namespace.this.name}.servicebus.windows.net" }
