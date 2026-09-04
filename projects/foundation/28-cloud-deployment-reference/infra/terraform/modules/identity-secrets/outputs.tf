output "identity_id" { value = azurerm_user_assigned_identity.this.id }
output "identity_principal_id" { value = azurerm_user_assigned_identity.this.principal_id }
output "key_vault_uri" { value = azurerm_key_vault.this.vault_uri }
output "key_vault_id" { value = azurerm_key_vault.this.id }
