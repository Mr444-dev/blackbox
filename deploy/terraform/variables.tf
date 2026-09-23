variable "environment" {
  type        = string
  description = "Target deployment environment"
  default     = "prod"
}

variable "location" {
  type        = string
  description = "Azure Region for Blackbox cloud resources"
  default     = "polandcentral"
}

variable "resource_group_name" {
  type        = string
  description = "Name of the resource group"
  default     = "rg-blackbox-prod"
}

variable "node_count" {
  type        = number
  description = "Initial number of AKS worker nodes"
  default     = 3
}

variable "vm_size" {
  type        = string
  description = "Azure VM Size for AKS node pool"
  default     = "Standard_D2s_v5"
}

variable "postgres_admin_login" {
  type        = string
  description = "PostgreSQL flexible server administrator username"
  default     = "blackbox_admin"
}
