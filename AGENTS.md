# NimBus Adapter Kit — Agent Instructions

This repo ships one agent skill (`skills/nimbus-adapter/`) and a `dotnet new` template inside it
(`skills/nimbus-adapter/template/`). Customer teams install it into their adapter repos with AGR
or as a Claude Code plugin.

## Rules that are specific to this repo

- **Anonymized content only.** This kit is shared with many customers. Never write customer
  names, customer repo names or paths, customer endpoint/entity prefixes, or code copied
  verbatim from a customer adapter. State provenance generically, e.g. "production Dataverse CE
  adapter". CI runs a denylist scan; a hit fails the build.
- **Every fact about an external system is tagged.** Use **[proven in production]** for
  behaviour observed in a running adapter, and **[Microsoft docs, verify]** for anything taken
  from documentation. Never present an untested claim as proven.
- **NimBus API names must match the pinned NimBus version** (`NimBusVersion` in
  `skills/nimbus-adapter/template/Directory.Packages.props`). Check the API against the NimBus
  source or package before you write it into a pattern or snippet. CI greps for known-stale
  names.
- **The template must compile in place and when instantiated.** Run `eng/instantiate-matrix.ps1`
  before you push a template change. Release builds treat CS warnings as errors.
- **Skill files must stay portable.**
  - Frontmatter is only `name` and `description`.
  - Use no tool names specific to one agent.
  - Commands are `dotnet`/`az` only (customers are on Windows).
  - Paths are relative to `SKILL.md`.

## Layout

- `skills/nimbus-adapter/SKILL.md`: mode routing and the hard rules.
- `skills/nimbus-adapter/references/`: `modes/`, `patterns/`, `systems/`, `checklists/`.
- `skills/nimbus-adapter/template/`: the `dotnet new nimbus-adapter` template.
- `tests/Kit.Snippets/`: compiles every system-pack snippet.
- `eng/`: the build and verification scripts used by CI.
- `docs/plan/`: plans.

## Conventions

- Conventional Commits (`feat(template): …`, `docs(patterns): …`).
- C# in the template follows NimBus conventions:
  - file-scoped namespaces, with nullable enabled;
  - MSTest, with test files starting `#pragma warning disable CA1707, CA2007`;
  - XML doc comments on public types.
