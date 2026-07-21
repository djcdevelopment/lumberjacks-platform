# ADR 0009: EF Core as Query Layer Without Migrations

**Status:** Accepted
**Date:** 2026-03-26
**Depends on:** ADR 0004 (PostgreSQL), ADR 0005 (.NET Runtime)

## Context

The .NET backend (ADR 0005) needs to read and write PostgreSQL (ADR 0004). The database schema was originally created by the TypeScript services and contains production data (events, player progress, guild progress). We needed an approach to data access that:

1. Maps cleanly to the existing table schema
2. Doesn't risk altering tables that already contain data
3. Provides type-safe queries without raw SQL everywhere
4. Stays simple — this is a query layer, not a domain model framework

## Decision

**EF Core is used as a typed query layer only.** No automatic migrations. No `dotnet ef database update`. Tables are managed externally (currently hand-created, future: versioned SQL scripts).

The `GameDbContext` uses explicit configuration:
- `ToTable("table_name")` — maps to existing tables
- `HasColumnName("column_name")` — maps to existing snake_case columns
- `HasColumnType("jsonb")` / `HasColumnType("uuid")` — matches Postgres types
- Value conversions where .NET types differ from Postgres types (e.g., `string` ↔ `uuid`)

## Rationale

**Schema already exists.** The TypeScript services created the tables with specific column types (uuid, jsonb, timestamptz). EF Core migrations would either try to recreate them (conflict) or generate no-op migrations (noise). Neither adds value.

**Explicit mapping is safer.** By spelling out every column name and type, the mapping is visible and auditable. No convention-based surprises where EF Core infers a column name that doesn't match the actual schema.

**Query benefits without ORM overhead.** We get LINQ-to-SQL, parameterized queries, connection pooling, and change tracking for simple CRUD — without taking on the complexity of a full ORM migration strategy, seed data, or model snapshots.

**Future migration path.** When the schema needs to evolve, we'll use versioned SQL migration scripts (e.g., `migrations/V001__add_column.sql`) applied by a dedicated tool (dbmate, Flyway, or plain psql). This keeps schema changes explicit and reviewable rather than auto-generated.

## Consequences

- No `Migrations/` folder in the repository
- Schema changes require manual SQL scripts and coordination
- `GameDbContext.OnModelCreating()` is the source of truth for how .NET maps to Postgres
- Developers must keep entity classes and column mappings in sync with the actual database schema
- The `Game.Persistence` project references `Npgsql.EntityFrameworkCore.PostgreSQL` but not `Microsoft.EntityFrameworkCore.Design` at runtime (design-time tooling only)
