output "api_url" { value = "https://${azurerm_container_app.api.latest_revision_fqdn}" }
output "frontend_url" { value = "https://${azurerm_container_app.web.latest_revision_fqdn}" }
output "postgres_fqdn" { value = azurerm_postgresql_flexible_server.db.fqdn }
output "storage_account_name" { value = azurerm_storage_account.documents.name }
