locals {
  name = "${var.prefix}-${var.environment}"
  tags = { application = "peritial", environment = var.environment }
}

resource "azurerm_resource_group" "this" {
  name = "rg-${local.name}"
  location = var.location
  tags = local.tags
}
resource "azurerm_virtual_network" "this" {
  name = "vnet-${local.name}"
  location = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  address_space = ["10.56.0.0/16"]
  tags = local.tags
}
resource "azurerm_subnet" "apps" {
  name = "snet-apps"
  resource_group_name = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes = ["10.56.0.0/23"]
  delegation {
    name = "containerapps"
    service_delegation {
      name = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}
resource "azurerm_subnet" "db" {
  name = "snet-postgres"
  resource_group_name = azurerm_resource_group.this.name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes = ["10.56.2.0/24"]
  delegation {
    name = "postgres"
    service_delegation {
      name = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}
resource "azurerm_private_dns_zone" "db" {
  name = "private.postgres.database.azure.com"
  resource_group_name = azurerm_resource_group.this.name
}
resource "azurerm_private_dns_zone_virtual_network_link" "db" {
  name = "link-${local.name}"
  resource_group_name = azurerm_resource_group.this.name
  private_dns_zone_name = azurerm_private_dns_zone.db.name
  virtual_network_id = azurerm_virtual_network.this.id
}
resource "azurerm_postgresql_flexible_server" "db" {
  name = "pg-${local.name}"
  resource_group_name = azurerm_resource_group.this.name
  location = azurerm_resource_group.this.location
  version = "17"
  delegated_subnet_id = azurerm_subnet.db.id
  private_dns_zone_id = azurerm_private_dns_zone.db.id
  public_network_access_enabled = false
  administrator_login = var.postgres_admin_login
  administrator_password = var.postgres_password
  storage_mb = 32768
  sku_name = "B_Standard_B1ms"
  backup_retention_days = 7
  tags = local.tags
  depends_on = [azurerm_private_dns_zone_virtual_network_link.db]
}
resource "azurerm_postgresql_flexible_server_database" "app" {
  name = "peritial"
  server_id = azurerm_postgresql_flexible_server.db.id
  charset = "UTF8"
  collation = "en_US.utf8"
}
resource "azurerm_storage_account" "documents" {
  name = substr(replace("${var.prefix}${var.environment}docs", "-", ""), 0, 24)
  resource_group_name = azurerm_resource_group.this.name
  location = azurerm_resource_group.this.location
  account_tier = "Standard"
  account_replication_type = "LRS"
  min_tls_version = "TLS1_2"
  https_traffic_only_enabled = true
  allow_nested_items_to_be_public = false
  tags = local.tags
}
resource "azurerm_storage_container" "documents" {
  name = "documents"
  storage_account_id = azurerm_storage_account.documents.id
  container_access_type = "private"
}
resource "azurerm_user_assigned_identity" "api" {
  name = "id-${local.name}-api"
  resource_group_name = azurerm_resource_group.this.name
  location = azurerm_resource_group.this.location
  tags = local.tags
}
resource "azurerm_role_assignment" "blob" {
  scope = azurerm_storage_account.documents.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id = azurerm_user_assigned_identity.api.principal_id
}
resource "azurerm_log_analytics_workspace" "this" {
  name = "log-${local.name}"
  resource_group_name = azurerm_resource_group.this.name
  location = azurerm_resource_group.this.location
  sku = "PerGB2018"
  retention_in_days = 30
  tags = local.tags
}
resource "azurerm_container_app_environment" "this" {
  name = "cae-${local.name}"
  resource_group_name = azurerm_resource_group.this.name
  location = azurerm_resource_group.this.location
  infrastructure_subnet_id = azurerm_subnet.apps.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id
  tags = local.tags
}
resource "azurerm_container_app" "api" {
  name = "ca-${local.name}-api"
  resource_group_name = azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode = "Single"
  identity {
    type = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.api.id]
  }
  secret {
    name = "postgres-connection"
    value = "Host=${azurerm_postgresql_flexible_server.db.fqdn};Database=peritial;Username=${var.postgres_app_login};Password=${var.postgres_app_password};SSL Mode=Require"
  }
  template {
    min_replicas = 1
    max_replicas = 3
    container {
      name = "api"
      image = var.api_image
      cpu = 0.5
      memory = "1Gi"
      env {
        name = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name = "ConnectionStrings__Postgres"
        secret_name = "postgres-connection"
      }
      env {
        name = "Database__Bootstrap"
        value = "false"
      }
      env {
        name = "Auth__Authority"
        value = var.oidc_authority
      }
      env {
        name = "Auth__Audience"
        value = var.oidc_audience
      }
      env {
        name = "Auth__Resource"
        value = var.resource_url
      }
      env {
        name = "Auth__RequireHttpsMetadata"
        value = "true"
      }
      env {
        name = "Cors__Origin"
        value = var.frontend_origin
      }
      env {
        name = "Storage__Provider"
        value = "Azure"
      }
      env {
        name = "Storage__Azure__ServiceUri"
        value = azurerm_storage_account.documents.primary_blob_endpoint
      }
      env {
        name = "Storage__Azure__Container"
        value = azurerm_storage_container.documents.name
      }
      env {
        name = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.api.client_id
      }
    }
  }
  ingress {
    external_enabled = true
    target_port = 8080
    traffic_weight {
      latest_revision = true
      percentage = 100
    }
  }
  tags = local.tags
  depends_on = [azurerm_role_assignment.blob]
}
resource "azurerm_container_app" "web" {
  name = "ca-${local.name}-web"
  resource_group_name = azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode = "Single"
  template {
    min_replicas = 1
    max_replicas = 2
    container {
      name = "web"
      image = var.frontend_image
      cpu = 0.25
      memory = "0.5Gi"
      env {
        name = "API_UPSTREAM"
        value = "https://${azurerm_container_app.api.latest_revision_fqdn}"
      }
    }
  }
  ingress {
    external_enabled = true
    target_port = 80
    traffic_weight {
      latest_revision = true
      percentage = 100
    }
  }
  tags = local.tags
}
