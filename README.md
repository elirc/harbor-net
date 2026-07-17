# harbor-net

An Intercom-style customer-messaging platform — C#/.NET backend only.

Workspaces, inboxes, contacts, teammates, teams, conversations with threaded
messages and internal notes, conversation states (open / snoozed / closed),
assignment, tags, canned replies, simple SLA tracking, and search/filter
endpoints.

## Tech stack

- .NET 10 / ASP.NET Core Web API (controllers)
- EF Core 10 with SQLite
- xUnit (unit + `WebApplicationFactory` integration tests)

## Solution layout

```
HarborNet.slnx
src/
  Harbor.Domain/          # entities, enums, domain logic (no dependencies)
  Harbor.Infrastructure/  # EF Core DbContext, migrations, seeding
  Harbor.Api/             # ASP.NET Core Web API host
tests/
  Harbor.Tests/           # unit + integration tests
```

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Harbor.Api
```

The API listens on the ports in `src/Harbor.Api/Properties/launchSettings.json`.
A SQLite database (`harbor.db`) is created and seeded automatically in
Development.
