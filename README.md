# NimBus Adapter Kit

Patterns, an agent skill and a `dotnet new` template for building
[NimBus](https://github.com/akakaule/NimBus) adapters. An adapter is the service that connects an
external system to a NimBus event bus on Azure Service Bus. The kit covers:
- Dataverse / Dynamics 365 CE;
- Business Central;
- Dynamics 365 Finance & Operations;
- any REST/OData API.

Production adapters on the NimBus architecture learned these lessons the hard way:
- session keys;
- idempotent upserts;
- echo loops;
- missing references;
- throttling and update windows;
- simulators and tests.

The kit starts you from a scaffold that already gets them right, and gives your coding agent the
same knowledge.

| Part | Where | What it gives you |
|---|---|---|
| **Pattern catalog** | [`skills/nimbus-adapter/references/patterns/`](skills/nimbus-adapter/references/patterns/README.md) | 29 patterns, one recurring adapter problem each: when it applies, the NimBus API that solves it, the tests to write, and the anti-patterns to avoid |
| **System packs** | [`skills/nimbus-adapter/references/systems/`](skills/nimbus-adapter/references/systems/) | Auth, change detection, writes, throttling, echo identity and gotchas for Dataverse/CE, Business Central and F&O. Each fact is tagged *proven in production* or *Microsoft docs, verify*. Includes simulator specs and drop-in client snippets |
| **Agent skill** | [`skills/nimbus-adapter/SKILL.md`](skills/nimbus-adapter/SKILL.md) | `nimbus-adapter`, with modes `new` (scaffold an adapter), `flow` (add one integration flow) and `review` (check an adapter against the checklist). Works in Claude Code, Codex and GitHub Copilot |
| **Template** | [`skills/nimbus-adapter/template/`](skills/nimbus-adapter/template/README.md) | `dotnet new nimbus-adapter`, described below |

The template generates a Worker / Container App adapter with:
- the NimBus wiring;
- typed client failures, retry rules, a circuit breaker and an inbox;
- a vendor-API simulator;
- 70 to 84 tests, depending on the options;
- an Aspire AppHost, Bicep and an Azure DevOps pipeline.

## Install

### In an adapter repo, for every agent: AGR

[AGR](https://github.com/computerlovetech/agr) installs skills from git into `.claude/skills/`
(Claude Code and Copilot) and `.agents/skills/` (Codex). Do this once per workstation:
1. Install uv.
2. Run `uv tool install agr`.

Then, in the adapter repo:

```toml
# agr.toml (the template generates this file)
default_source = "github"
tools = ["claude", "codex"]
dependencies = [
    { handle = "akakaule/nimbus-adapter-kit/nimbus-adapter", type = "skill" },
    { handle = "akakaule/NimBus/adapter-docs", type = "skill" },
]

[[source]]
name = "github"
type = "git"
url = "https://github.com/{owner}/{repo}.git"
```

```powershell
agr sync      # installs the skills; commit agr.lock, ignore .claude/skills and .agents/skills
```

This repo is private: you need read access, and git must be signed in to GitHub.

### For yourself, in Claude Code: plugin

```
/plugin marketplace add akakaule/nimbus-adapter-kit
/plugin install nimbus-adapter@nimbus-adapter-kit
```

This is useful before an adapter repo exists. Then ask Claude to "scaffold a NimBus adapter for
Business Central".

### Only the template

```powershell
git clone https://github.com/akakaule/nimbus-adapter-kit
dotnet new install ./nimbus-adapter-kit/skills/nimbus-adapter/template
dotnet new nimbus-adapter -n Contoso.ErpAdapter --SystemName BusinessCentral
```

Options:
- `--Ingress true` adds the system → bus path. Change notifications arrive on a queue; the adapter
  re-reads each record and publishes it with a deterministic MessageId.
- `--Endpoint <Name>Endpoint` sets the endpoint (topic) name when it isn't `<SystemName>Endpoint`.
- `--Contracts package` takes the catalog from a shared platform package instead of a local
  project. It also needs `--ContractsPackageId`, `--ContractsPackageVersion`, `--PlatformType` and
  `--ContractsFeed`.
- `--Inbox false` leaves out the SQL inbox.
- `--NimBusVersion <x.y.z>` pins NimBus; match your Resolver and WebApp.

## Quick start with an agent

| You say | The skill |
|---|---|
| "Create a NimBus adapter for Business Central: customers from CRM, quotes back" | `new`: interviews you, writes `docs/adapter-brief.md`, generates the solution, specialises the client from the BC pack, builds the first flow, runs the tests |
| "Add a flow: Dataverse contact changes update the ERP contact" | `flow`: works through the flow decisions, records them in `docs/flows/`, then adds the catalog, client, mapper, handler and tests |
| "Review this adapter" / "Review this PR" | `review`: the checklist, with findings by severity and links to the patterns |

## Contributing

- Read [AGENTS.md](AGENTS.md). Content stays anonymized, and every system fact is tagged.
- Run these before pushing (CI runs the same):

  ```powershell
  pwsh eng/instantiate-matrix.ps1       # every template combination: instantiate, build, test, Bicep
  pwsh eng/verify-snippets.ps1          # every system pack's snippets compile inside the adapter
  pwsh eng/check-content.ps1            # stale NimBus API names, skill frontmatter, links
  pwsh eng/check-anonymization.ps1 -DenylistFile <your local denylist>
  ```

- Versioning: tag `vX.Y.Z` when the skill or template semantics change. Consumers pin the version
  through `agr.lock`. See [CHANGELOG.md](CHANGELOG.md).

## License

[MIT](LICENSE). Adapters you generate from the template are your own code; the license covers the
kit itself (skill, patterns, packs and template).
