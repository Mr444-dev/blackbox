terraform {
  required_version = ">= 1.8.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
    }
  }
}

resource "random_string" "suffix" {
  length  = 6
  special = false
  upper   = false
}

# 1. Primary Resource Group
resource "azurerm_resource_group" "blackbox" {
  name     = var.resource_group_name
  location = var.location
  tags = {
    Environment = var.environment
    Platform    = "BLACKBOX"
    ManagedBy   = "Terraform"
  }
}

# 2. Virtual Network & Subnets
resource "azurerm_virtual_network" "vnet" {
  name                = "vnet-blackbox-${var.environment}"
  location            = azurerm_resource_group.blackbox.location
  resource_group_name = azurerm_resource_group.blackbox.name
  address_space       = ["10.0.0.0/16"]
}

resource "azurerm_subnet" "aks_subnet" {
  name                 = "snet-aks"
  resource_group_name  = azurerm_resource_group.blackbox.name
  virtual_network_name = azurerm_virtual_network.vnet.name
  address_prefixes     = ["10.0.1.0/24"]
}

resource "azurerm_subnet" "db_subnet" {
  name                 = "snet-postgres"
  resource_group_name  = azurerm_resource_group.blackbox.name
  virtual_network_name = azurerm_virtual_network.vnet.name
  address_prefixes     = ["10.0.2.0/24"]
  delegation {
    name = "fs-delegation"
    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

# 3. Azure Database for PostgreSQL Flexible Server
resource "azurerm_postgresql_flexible_server" "postgres" {
  name                   = "psql-blackbox-${random_string.suffix.result}"
  resource_group_name    = azurerm_resource_group.blackbox.name
  location               = azurerm_resource_group.blackbox.location
  version                = "16"
  delegated_subnet_id    = azurerm_subnet.db_subnet.id
  administrator_login    = var.postgres_admin_login
  administrator_password = var.postgres_admin_password
  storage_mb             = 32768
  sku_name               = "GP_Standard_D2ds_v5"
  zone                   = "1"

  high_availability {
    mode = "ZoneRedundant"
  }
}

resource "azurerm_postgresql_flexible_server_database" "db" {
  name      = "blackbox_db"
  server_id = azurerm_postgresql_flexible_server.postgres.id
  collation = "en_US.utf8"
  charset   = "utf8"
}

# 4. Azure Key Vault
data "azurerm_client_config" "current" {}

resource "azurerm_key_vault" "kv" {
  name                       = "kv-blackbox-${random_string.suffix.result}"
  location                   = azurerm_resource_group.blackbox.location
  resource_group_name        = azurerm_resource_group.blackbox.name
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  soft_delete_retention_days = 7
  purge_protection_enabled   = true
  enable_rbac_authorization  = true
}

# 5. Azure Kubernetes Service (AKS)
resource "azurerm_kubernetes_cluster" "aks" {
  name                = "aks-blackbox-${var.environment}"
  location            = azurerm_resource_group.blackbox.location
  resource_group_name = azurerm_resource_group.blackbox.name
  dns_prefix          = "blackbox-k8s"

  default_node_pool {
    name                = "systempool"
    node_count          = var.node_count
    vm_size             = var.vm_size
    vnet_subnet_id      = azurerm_subnet.aks_subnet.id
    auto_scaling_enabled = true
    min_count           = 2
    max_count           = 6
  }

  identity {
    type = "SystemAssigned"
  }

  network_profile {
    network_plugin    = "azure"
    load_balancer_sku = "standard"
  }

  oidc_issuer_enabled       = true
  workload_identity_enabled = true
}
