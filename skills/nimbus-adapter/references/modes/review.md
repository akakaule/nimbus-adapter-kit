# Mode `review`: check an adapter against the patterns

A read-only review. Do not edit, commit or comment on PRs unless the user asks.

## 1. Scope

- **Whole adapter**: every item in [../checklists/review.md](../checklists/review.md).
- **Branch or PR**: the diff against its base (`git diff $(git merge-base <base> HEAD)`), plus
  every checklist item the diff touches. If the branch has a flow record or plan, also check that
  the code does what it says.
- Name the adapter's NimBus version (`<NimBusVersion>`, or the package references). Some items
  depend on it.

## 2. Gather evidence

For each checklist item, find the code that satisfies or violates it. The checklist names what to
search for. Read the code; do not infer from names. Run the build and tests in Release if you can,
and report the results:

```powershell
dotnet build <solution> -c Release
dotnet test <solution> -c Release
```

A failing or missing test gate is itself a finding.

## 3. Report

Group findings by severity, most severe first. Each finding has:

- **Severity**:
  - *Critical*: data loss or corruption, security exposure, or messages lost or stuck.
  - *High*: an outage or operator burden waiting to happen, or missing ordering or idempotency.
  - *Medium*: maintainability, observability or test gaps.
  - *Low*: style and hygiene.
- **Where**: `path:line`.
- **What**: the defect, in one sentence.
- **Why it matters**: the concrete failure scenario.
- **Fix**: the change, with the pattern that describes it (`patterns/NN-*.md`).

End with:
- what is done well (so it survives future changes);
- the checks you ran and their results;
- the items you could not assess, and why.

Optionally write the report to `docs/review.md` in the adapter repo, if the user wants it kept.
