# 24. Auth and secrets

**Confidence:** production. **Applies to:** every adapter.

## Problem

Found in production reviews:
- a password and storage keys in source;
- Key Vault secrets overwritten with "placeholder" by a deployment;
- auth disabled "outside prod", or commented out;
- an unauthenticated public POST endpoint;
- hand-written token caches that were not thread-safe;
- a `RootManageSharedAccessKey` used by deploy scripts.

## Solution

- **Managed identity for Azure resources.** The adapter's user-assigned identity gets Service Bus
  *Data Receiver* and *Data Sender* on the namespace, and *AcrPull* on the registry (the
  template's Bicep). The Service Bus connection is just the namespace host name
  (`ConnectionStrings__servicebus = <ns>.servicebus.windows.net`), with `AZURE_CLIENT_ID` set so
  the right identity is used.
- **Entra tokens for the external system.** Use `Azure.Identity` credentials (managed identity in
  Azure, the developer's sign-in locally) through the template's `BearerTokenHandler`, with the
  scope from configuration. The credential caches and refreshes tokens; don't write a token cache.
  Where the system needs an app registration with a secret or certificate (check the pack), keep
  the secret in Key Vault and use `ClientSecretCredential` / `ClientCertificateCredential`.
- **Least privilege in the target**: a dedicated integration user or application user with only
  the roles the flows need. Its id is also the echo identity (`08`).
- **Secrets** come only from Key Vault references (Container Apps secrets with `keyVaultUrl`, or
  bicepparam `getSecret`) and from user secrets locally. Never from appsettings, csproj, pipeline
  YAML or parameter files.
- **Ingress endpoints** (webhook receivers) are authenticated in every environment: Entra ID, or a
  shared secret validated in constant time for systems that can only send a static header. Behind
  them comes a queue, not business logic.
- **SQL**: SqlClient 7.x moved Entra and managed-identity authentication into
  `Microsoft.Data.SqlClient.Extensions.Azure`. Verify managed-identity SQL connections before
  relying on them; otherwise use a Key Vault-held connection string.

## Tests

- An architecture test asserting no connection string with `SharedAccessKey` or `Password=` is in
  the repo's appsettings files (add one if your adapter has several config files).
- Ingress: unauthenticated requests get 401.

## Anti-patterns

The ones listed above. Also: a deploy step that writes secrets into Key Vault from pipeline
variables on every run, and a shared integration user across unrelated adapters.

## Provenance

- Managed identity everywhere with Key Vault configuration only in cloud (production CE adapter).
- A send-only, topic-scoped key replacing deployment secrets (production web-lead adapter).
- Secrets found in source and auth disabled (HR adapter reviews).
- Token cache issues (production CRM and ERP adapters).
