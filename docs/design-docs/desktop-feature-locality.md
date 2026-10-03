# Desktop Feature Locality

Status: accepted

## Decision

Do not solve routed-feature completeness with a global `FeatureRegistry`,
feature-module framework or second router. Extend the existing typed-navigation,
Microsoft DI and Avalonia presentation owners, and verify their relationship
with executable tests.

Shell order, title, icon and selection state are local presentation facts.
Parent route and payload are caller/workflow facts. Neither belongs in a global
descriptor beside Application route identity, Desktop composition and Avalonia
templates.

## Why

A global registry would centralize facts that change for different reasons and
already have different authoritative owners. It would become a parallel owner
for navigation, composition and presentation while still needing the existing
router, container and templates. That adds synchronization rather than removing
it.

Cross-owner completeness is instead an executable contract: a typed route maps
to a ViewModel, production DI resolves it, and Avalonia can present it. A Shell
may keep a small local descriptor when that removes a local duplicate, but it
must not become a repository-wide feature manifest.

Current owners, invariants and maintenance proof are documented in
`../../ARCHITECTURE.md` and `../maintenance.md#desktop-host`.
