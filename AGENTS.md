# NimBus Adapter Kit — Agent Instructions

This repo ships one agent skill (`skills/nimbus-adapter/`) and a `dotnet new` template inside it
(`skills/nimbus-adapter/template/`). Customer teams install it into their adapter repos with AGR
or as a Claude Code plugin.

## Rules that are specific to this repo

- **Anonymized content only.** This kit is shared with many customers. Never write customer
  names, customer repo names or paths, customer endpoint/entity prefixes, or code copied
  verbatim from a customer adapter. State provenance generically, e.g. "production Dataverse CE
  adapter". CI runs a denylist scan; a hit fails the build.
- **Every fact about an external system is tagged**, and the tags mean exactly this:
  - **[proven in production]**: seen in a production adapter's code, documentation or fix history.
    The kit authors did not re-observe it at runtime.
  - **[proven in the NimBus sample]**: shown in a NimBus sample against a simulator, so it says
    nothing about the real system.
  - **[Microsoft docs, verify]**: taken from documentation, with the source cited.
  - **[unverified]**: neither documented nor observed.

  Never present an untested claim as proven.
- **NimBus API names must match the pinned NimBus version** (`NimBusVersion` in
  `skills/nimbus-adapter/template/Directory.Packages.props`). Check the API against the NimBus
  source or package before you write it into a pattern or snippet. CI greps for known-stale
  names.
- **The template must compile in place and when instantiated.** Run `eng/instantiate-matrix.ps1`
  before you push a template change. Release builds treat CS warnings as errors. Template files
  use template-engine conditionals (`#if (Inbox)`, `<!--#if -->`, `//#if` in Bicep). Bicep files
  must stay LF-only (`.gitattributes`), because the engine's Bicep conditionals fail on CRLF.
- **Snippets compile inside the template's adapter.** They are in namespace
  `TemplateAdapter.Adapter.Clients` and use `ExternalSystem*` names. Run
  `eng/verify-snippets.ps1`.
- **Skill files must stay portable.**
  - Frontmatter is only `name` and `description`.
  - Use no tool names specific to one agent.
  - Commands are Windows-ready: `dotnet`, `az` and `git`, written so they run as-is in PowerShell.
    No bash-only syntax (`$(...)`, `&&` chains with bash semantics, `/tmp`).
  - Paths are relative to `SKILL.md`.

## Layout

- `skills/nimbus-adapter/SKILL.md`: mode routing and the hard rules.
- `skills/nimbus-adapter/references/`: `modes/`, `patterns/`, `systems/`, `checklists/`.
- `skills/nimbus-adapter/template/`: the `dotnet new nimbus-adapter` template.
- `eng/`: the verification scripts CI runs.
  - `instantiate-matrix.ps1`: instantiates every option combination, then builds, tests and
    checks the Bicep.
  - `verify-snippets.ps1`: compiles each pack's snippets inside a copy of the template's adapter.
  - `check-content.ps1`: catches stale API names, checks the skill frontmatter and resolves links.
  - `check-anonymization.ps1`: scans for denylisted terms (the denylist comes from a CI secret or
    a local file).
- `.claude-plugin/`: the Claude Code plugin and marketplace manifests. Validate them with
  `claude plugin validate .`.
- `docs/plan/`: plans.

## Conventions

- Conventional Commits (`feat(template): …`, `docs(patterns): …`).
- C# in the template follows NimBus conventions:
  - file-scoped namespaces, with nullable enabled;
  - MSTest, with test files starting `#pragma warning disable CA1707, CA2007`;
  - XML doc comments on public types.
