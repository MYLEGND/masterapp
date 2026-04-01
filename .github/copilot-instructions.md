# AI Coding Agent Instructions for MASTERAPP

## Architecture Overview
This is a modular .NET 10.0 solution following Domain-Driven Design (DDD) principles:
- **Domain**: Core business entities and logic (e.g., `ClientProfile`, `BookkeepingEntry`)
- **Infrastructure**: Data access layer with EF Core `MasterAppDbContext` and migrations
- **Web Apps**: Multiple ASP.NET Core MVC applications (AgentPortal, ClientApp, etc.) sharing Domain/Infrastructure

Data flows: Controllers → Scoped Services → DbContext → Entities. Authentication is enforced globally via Azure AD/Microsoft Identity.

## Key Patterns & Conventions
- **Entities**: Use `Guid` for primary keys, `string` for Azure AD user IDs (e.g., `ClientUserId`). Include detailed properties with validation comments (e.g., CRM status values: "Lead | Prospect | Active | Dormant").
- **DbContext**: Fluent API configurations for unique indexes (e.g., `ClientUserId` unique), max lengths, and relationships. Store complex state as JSON (e.g., `FinanceToolState.JsonState`).
- **Controllers**: Standard MVC with Razor views. Inherit from `Controller`, use `View()` for responses.
- **Services**: Scoped DI services (e.g., `ClientProvisioningService`) for business logic.
- **Configuration**: Nullable enabled, implicit usings. Custom `.csproj` excludes prevent build artifact pollution (e.g., `bin/**`, `**/*.bak`).
- **Authentication**: Global `AuthorizeFilter` policy requires authenticated users. Uses Microsoft Identity Web with Azure AD.

## Developer Workflows
- **Build**: `dotnet build MasterApp.slnx` (builds all projects)
- **Run Locally**: `dotnet run --project AgentPortal` (uses local SQLite from `appsettings.json`)
- **Database Migrations**: `dotnet ef database update --project Infrastructure --startup-project AgentPortal` (applies pending migrations)
- **Debugging**: Standard .NET debugging in VS Code with C# extension; breakpoints in controllers/services work as expected

## Integration Points
- **Azure AD**: Configured in `appsettings.json` with tenant/client IDs; scopes include `User.Read`, `Calendars.ReadWrite`
- **Microsoft Graph**: Used for user/calendar operations; client configured separately in `GraphProvisioning`
- **Database**: Local SQLite (`masterapp.db`) for development; cloud SQL Server detected via connection string patterns (contains `.database.windows.net`)
- **Cross-App Communication**: Shared entities allow data consistency across AgentPortal/ClientApp; provisioning service handles client setup

## Examples
- Adding a new entity: Define in `Domain/Entities/`, add `DbSet<T>` in `MasterAppDbContext`, configure in `OnModelCreating`, create migration with `dotnet ef migrations add`
- New controller action: Inherit from `Controller`, inject services via constructor, return `View(model)` or JSON
- Updating CRM fields: Follow `ClientProfile` pattern with nullable strings and specific allowed values