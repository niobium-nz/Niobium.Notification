# Durable provider HTTP configuration

The durable provider owns the submission schedule. Its HTTP client must neither
follow redirects nor retry outside that schedule. These are separate settings:
the shared redirect setting does not remove resilience handlers.

## Shared framework support

The owner requested moving redirect configuration into Niobium in
[PR #2's transport review](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4500908254).
[Niobium PR #2](https://github.com/niobium-nz/Niobium/pull/2) adds
`ConfigureAutoRedirect(bool)` to `Niobium.Platform`. The extension configures an
existing `SocketsHttpHandler` or `HttpClientHandler`, preserving its connection
lifetime, proxy/cookie settings and delegating handlers. It can be used for one
client or for shared defaults. The policy runs after custom transport selection,
and explicit client settings take precedence in either registration order. See the
[framework guide at the reviewed candidate](https://github.com/niobium-nz/Niobium/blob/63d61bc8a7302ba213a352705a8891ddeecf4c13/docs/http-clients.md).

Notification's current published Platform dependency is 4.0.60 and has no such API.
The current primary-handler factory therefore remains until the framework release
is available. Removing it now would re-enable redirects; copying the framework
extension into this application would duplicate the support the owner requested.

## Adoption after release

1. Verify the approved framework release contains `ConfigureAutoRedirect` and pin
   its actual package version in the Server project. Align any required direct
   dependencies with the published dependency graph and verify restore.
2. Import `Niobium.Platform` in the Server dependency module and replace
   `.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })`
   with `.ConfigureAutoRedirect(false)`. Retain the separate removal of resilience
   handlers, since the durable flow owns all retries.
3. Update the composition test to inspect the factory's `SocketsHttpHandler` and
   still require redirects disabled and no resilience handler. Run the Release
   build and all application tests against the released packages, then follow the
   existing deployment/review sequence when authorized.

Do not pin an unpublished placeholder version, add a cross-repository
ProjectReference or remove the transport control before the dependency is ready.
The framework PR must be reviewed before its merge triggers package publication.
Notification remains unmerged, undeployed and disabled by default.

## Candidate validation on 2026-11-07

An isolated copy of Notification head `49faf17589b7d2950197dc7cf9bf87dbdfa60a7d`
was tested with the two source edits above and local candidate packages built from
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
