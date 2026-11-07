# Durable provider HTTP configuration

The durable provider owns the submission schedule. Its HTTP client must neither
follow redirects nor retry outside that schedule. These are separate settings:
the shared redirect setting does not remove resilience handlers.

## Shared framework support

The owner requested moving redirect configuration into Niobium in
[PR #2's transport review](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4500908254).
[Niobium PR #2](https://github.com/niobium-nz/Niobium/pull/2) introduced
`ConfigureAutoRedirect(bool)` to `Niobium.Platform`. The extension configures an
existing `SocketsHttpHandler` or `HttpClientHandler`, preserving its connection
lifetime, proxy/cookie settings and delegating handlers. It can be used for one
client or for shared defaults. The policy runs after custom transport selection,
and explicit client settings take precedence in either registration order. See the
[framework guide at the release commit](https://github.com/niobium-nz/Niobium/blob/f5b1b46a4a8c83e8771d7a3e9ae9dcc7044b62f0/docs/http-clients.md).

The owner approved the framework merge, publication and Notification package update
on 2026-11-07. Framework PR #2 was merged as
`f5b1b46a4a8c83e8771d7a3e9ae9dcc7044b62f0` and
[Publish run 42358398851](https://github.com/niobium-nz/Niobium/actions/runs/42358398851)
released [Niobium.Platform 4.2.70](https://www.nuget.org/packages/Niobium.Platform/4.2.70).
A clean restore from NuGet.org verified availability after indexing; the package's
repository metadata identifies that exact merge commit.

## Current package and client configuration

The Server project pins these published packages:

| Package | Version | Reason |
| --- | --- | --- |
| Niobium.Platform | 4.2.70 | Shared `ConfigureAutoRedirect` API |
| Niobium.Abstractions | 4.2.66 | Published version satisfying Platform's dependency |
| Niobium.Helper | 4.2.66 | Published version satisfying Platform's dependency |

Platform's package metadata specifies minimum 4.2.0 versions for Abstractions and
Helper, but those exact versions were not published. NuGet resolves their existing
4.2.66 releases and otherwise reports NU1603 approximate-version warnings. The Server
pins these actual versions explicitly; no warning is suppressed. Both packages
identify framework commit `5c527723a607c1d03c002d4aa70531c9a55a767e`, and their source
files match the previously inspected abstraction/helper implementation. The runtime
dependency graph resolves these versions while the Table/Blob adapters remain 4.0.60.
The Contracts project's existing package dependencies are unchanged.

The Server dependency module imports `Niobium.Platform` and calls
`.ConfigureAutoRedirect(false)` after registering the durable provider's client.
It preserves the factory's `SocketsHttpHandler` and separately removes resilience
handlers so the durable flow owns all retries. The application no longer constructs
a replacement primary handler to configure redirects. The composition test verifies
both disabled redirects and absent resilience handlers through the real registration.

Do not use unpublished placeholder versions or cross-repository ProjectReferences
for framework adoption. Verify publication, restore and the resulting runtime graph.
Notification remains open for review, undeployed and disabled by default.

## Published-package validation on 2026-11-07

The Release build passed with zero warnings/errors. All 106 application tests passed,
with 98.17% coverage: Contracts 100%, Core 98.99% and Server 96.92%. Four actual
Table/Blob adapter scenarios passed against disposable loopback Azurite with the
new runtime dependency graph, including conditional writes, competing claims and
restart recovery. The fixtures cleaned up their own resources and both emulators
were stopped. This is local compatibility evidence, not a deployed provider test.

## Historical candidate validation on 2026-11-07

An isolated copy of Notification head `49faf17589b7d2950197dc7cf9bf87dbdfa60a7d`
was tested with the shared extension, updated composition assertion and local
candidate packages built from
framework head `63d61bc8a7302ba213a352705a8891ddeecf4c13`.
Platform, Helper and Abstractions used the clearly local version
`4.2.0-local.redirect.2`; only this scratch copy's package references were changed.
Its own build version was `0.0.0-local.redirect.2` so it did not create a stable
package depending on prerelease inputs. No candidate package was published.

The Release build passed with zero warnings/errors. All 106 application tests
passed, including composition with the real candidate package and its default
`SocketsHttpHandler`. Coverage remained 98.17% overall: Contracts 100%, Core 98.99%
and Server 96.92%. This validates the proposed source integration locally; it does
not establish a published dependency or deployed provider behavior. The framework's
18 tests additionally cover real loopback redirected POSTs and transport settings.
