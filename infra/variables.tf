variable "prefix" {
  type = string
  default = "peritial"
}
variable "environment" {
  type = string
  default = "dev"
}
variable "location" {
  type = string
  default = "westeurope"
}
variable "api_image" {
  type = string
  description = "Published OCI image API/MCP"
}
variable "frontend_image" {
  type = string
  description = "Published OCI image PWA"
}
variable "postgres_admin_login" {
  type = string
  default = "peritialadmin"
}
variable "postgres_password" {
  type = string
  sensitive = true
}
variable "oidc_authority" {
  type = string
}
variable "oidc_audience" {
  type = string
}
variable "frontend_origin" {
  type = string
  description = "Exact HTTPS frontend origin for CORS"
}
variable "resource_url" {
  type = string
  description = "Exact public HTTPS MCP protected resource"
}
