# 28. Deploy and pipeline

**Confidence:** production. **Applies to:** every adapter.

## Problem

Adapters were deployed in as many ways as there were adapters:
- different branching and pipelines;
- no test step in at least one deployment pipeline;
- pull-request pipelines deleted, so tests silently stopped running;
- Logic App content promoted by hand;
- secrets overwritten by deployments.

## Solution: the template's pipeline and Bicep

- **One pipeline per adapter repo** (`azure-pipelines.yml`):
  1. **Build in Release.** Compiler warnings and vulnerable packages are errors.
  2. **Test**: the gate; results are published.
  3. **Validate the Bicep.**
  4. **Container image** with `dotnet publish -t:PublishContainer` (no Dockerfile).
  5. **Deploy** dev → test → prod: one stage template per environment, with approvals and checks
     configured on the Azure DevOps *environments*.
- **Topology before code.** Each deploy stage first runs
  `nb topology apply --solution-id … --environment … --resource-group … --assembly <catalog dll>`,
  or `--platform-package Id@Version` when the catalog is a shared package. The platform pipeline
  then applies it instead of each adapter. Then it deploys the Container App.
- **Infrastructure as code** (`deploy/main.bicep`):
  - a user-assigned identity, with Service Bus and ACR roles;
  - a Container App without ingress;
  - a KEDA `azure-servicebus` scale rule authenticating with the identity;
  - secrets as Key Vault references;
  - parameters per environment in `main.<env>.bicepparam`.
- **Pin versions together.** The NimBus package version in `Directory.Packages.props` and the
  `nb` CLI version in the pipeline must match the version the Resolver and WebApp run.
- **PRs build and test only.** `main` deploys.

## Tests

- `az bicep build` and `az bicep build-params` in CI.
- `az deployment group what-if` before the first real deployment.
- Optional: a test that greps the Bicep for `listKeys(` and secret literals.

## Anti-patterns

- A deploy without the test stage.
- A site or zip deploy that replaces all app settings (Bicep must own them).
- Promotion by copying files between environments.
- Topology applied by the adapter at start-up.

## Provenance

- An adapter-template user story (a test-gated build, versioned deploys with approvals, a shared
  branching strategy).
- Pipeline test gates removed in a reorganization (production CRM/NAV adapters).
- Logic App drift on promotion (production F&O adapter).
- `nb topology apply` from a catalog package (NimBus CLI).
