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

The owner's [package review](https://github.com/niobium-nz/Niobium.Notification/pull/2#discussion_r4501144615)
clarifies that framework releases also update affected dependent libraries. Upgrade
existing direct Niobium references across the solution together, using each
package's actual latest compatible release. Do not add lower-level references only
to force versions that upgraded integration packages already provide.
The framework convention is documented in
[Niobium PR #3](https://github.com/niobium-nz/Niobium/pull/3).

The three extra Server references to Platform, Abstractions and Helper are removed.
The current direct references, checked against NuGet.org on 2026-11-07, are:

| Project | Existing Niobium package references | Version |
| --- | --- | --- |
| Contracts | Abstractions, Messaging | 4.2.66 |
| Core | File, Messaging | 4.2.66 |
| Core | Platform.Notification.Email | 4.2.70 |
| Server | Platform.Functions, Platform.StorageTable, Platform.Blob, Platform.ServiceBus, Platform.Notification.Email.Resend, Platform.Captcha.ReCaptcha | 4.2.70 |

Core's Email package depends on Platform 4.2.70. Server references Core and receives
Platform transitively, alongside its other integration dependencies. The resolved
runtime graph contains Abstractions/Helper, Database.StorageTable, File.Blob,
Messaging.ServiceBus and Identity at 4.2.66. Those packages identify framework commit
`5c527723a607c1d03c002d4aa70531c9a55a767e`; the 4.2.70 packages identify the release
merge above. No local candidate package or additional lower-level pin is used.

The 4.2.70 StorageTable, Blob and ServiceBus integration packages specify minimum
4.2.0 versions for their adapters, but those exact versions were not published.
NuGet resolves the available 4.2.66 adapters and emits three distinct NU1603 warnings
in Server and the test project. The warnings remain visible. This framework
release-metadata issue needs a packaging fix and release; it does not justify
restoring redundant consumer references. Abstractions/Helper already resolve
without warnings through the upgraded graph.

The Server dependency module imports `Niobium.Platform` and calls
`.ConfigureAutoRedirect(false)` after registering the durable provider's client.
It preserves the factory's `SocketsHttpHandler` and separately removes resilience
handlers so the durable flow owns all retries. `ConfigureAutoRedirect` deliberately
preserves delegating handlers; the framework's
`RedirectPolicy_PreservesExistingResiliencePipeline` test verifies this. Removing
the resilience-removal loop would restore hidden retries even with redirects off.

The loop uses the stable `ConfigureAdditionalHttpMessageHandlers` hook.
`RemoveAllResilienceHandlers()` in the installed Microsoft.Extensions.Http.Resilience
10.9.0 is experimental and produces compiler diagnostic EXTEXP0001. Keep the existing
loop without suppressing that diagnostic or changing shared redirect semantics.
The application no longer constructs
a replacement primary handler to configure redirects. The composition test verifies
both disabled redirects and absent resilience handlers through the real registration.

Do not use unpublished placeholder versions or cross-repository ProjectReferences
for framework adoption. Verify publication, restore and the resulting runtime graph.
Notification remains open for review, undeployed and disabled by default.

## Aligned-package validation on 2026-11-07

The Release build passed with zero errors and the three NU1603 dependency warnings
above, repeated across Server and tests (six warnings in the build summary). All
106 application tests passed, with 98.18% coverage: Contracts 100%, Core 98.99% and
Server 96.93%. The composition test confirms disabled redirects and no resilience
handler after registering the complete host against the upgraded packages.

Four real Table/Blob scenarios passed with the 4.2.66 adapters against disposable
loopback Azurite. They cover conditional writes, competing claims and recovery
with frozen payloads; fixtures were cleaned up and both emulators stopped. See
[the ledger](implementation-progress.md) for commands and the remaining deployed
verification boundary.

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
