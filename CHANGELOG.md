# Changelog

## [1.2.2](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.2.1...v1.2.2) (2026-05-12)


### Bug Fixes

* **readme:** absolute GitHub URLs so nuget.org links resolve ([#33](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues/33)) ([3a96825](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/3a9682575d250f42e060bfd3ae329468c76bf530))

## [1.2.1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.2.0...v1.2.1) (2026-05-03)


### Bug Fixes

* **release-please:** drop pre-major flags (package is post-1.0) ([#28](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues/28)) ([53a2174](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/53a2174c65b8eef51153721bc2a3fd3c72a8e86b))

## [1.2.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.1.3...v1.2.0) (2026-05-01)


### Features

* lock public API surface (PublicApiAnalyzers + api-compat gate) ([#26](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues/26)) ([75467d7](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/75467d7aa5677826c18ccce232f0450cdb36dfae))

## [1.1.3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.1.2...v1.1.3) (2026-04-30)


### Bug Fixes

* stop publishing broken stand-alone generator nupkg ([66ee0d4](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/66ee0d48b871f10c72bcf3ee1968181c27e45456))
* stop publishing broken stand-alone generator nupkg ([bf9ae2c](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/bf9ae2cc941fbbe66bbaab333bbec92fdb7d7e0a))

## [1.1.2](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.1.1...v1.1.2) (2026-04-29)


### Documentation

* **readme:** standardize 5-badge set ([dc79e5e](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/dc79e5e32fecfa13f5845c37c66f3e714f52194b))
* **readme:** standardize 5-badge set (NuGet/Build/License/AOT/Sponsors) ([3042b67](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/3042b6754272908f514f60e7fe936f9bd8940675))

## [1.1.1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.1.0...v1.1.1) (2026-04-28)


### Documentation

* add GitHub Sponsors badge to README ([acdc4e3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/acdc4e359dff148e04e6648ca10a15f3def285e6))
* add GitHub Sponsors badge to README ([562924e](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/562924e375b55967e8a5b5684e9d44f5000b758b))

## [1.1.0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.0.3...v1.1.0) (2026-04-24)


### Features

* non-throwing Result path + cross-repo NuGet isolation ([#17](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues/17)) ([82813e0](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/82813e01b743b6f1c3ef507faefe0b6997dc84cd))

## [1.0.3](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.0.2...v1.0.3) (2026-04-23)


### Bug Fixes

* **generator:** annotate TImpl with [DynamicallyAccessedMembers] on Add{Service}Resilience ([#11](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues/11)) ([eccaae9](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/eccaae96e09b172640bc14f6ac4c1949e68f69c8))

## [1.0.2](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.0.1...v1.0.2) (2026-04-22)


### Bug Fixes

* **packaging:** ensure 1.0.1 package reaches NuGet ([2ebe8de](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/2ebe8de8a69b31f37e1a45348e07ae63b9e8cf5c))

## [1.0.1](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/compare/v1.0.0...v1.0.1) (2026-04-22)


### Bug Fixes

* **packaging:** publish Z logo icon to NuGet ([#4](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/issues/4)) ([b5eb301](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/b5eb301c016840665b34709600efb6f22e7d0f45))

## 1.0.0 (2026-04-19)


### Features

* **benchmarks:** add 5 missing scenarios; fix ReferenceOutputAssembly ([2e11bd7](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/2e11bd798802719b2477436d2f75b82382ccb1f3))
* **benchmarks:** add BenchmarkDotNet project with baseline and policy benchmarks ([fd54402](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/fd544025da7f6f2d99a499ace09ace90b9c53f4d))
* **core:** add policy attribute types and ResilienceException ([d9a967c](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/d9a967c74b13e21e0a1d6347136d6632efafbe36))
* **core:** add policy objects (CircuitBreakerFsm, CircuitBreakerPolicy, RateLimiter, RetryPolicy, TimeoutPolicy) ([38043aa](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/38043aa12441bba552236ce47cbc61609b653a5e))
* **generator:** add ResilienceGenerator parsing pipeline ([a400ef9](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/a400ef981e94fc44f03ee7d7e371564fe478aec8))
* **generator:** add ResilienceModel and diagnostics (ZR0001, ZR0002) ([059f832](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/059f83230335623c520df99707e66fb151c8a779))
* **generator:** add ResilienceWriter — proxy class and DI extension emission ([95ac743](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/95ac743df839925512e3e7394af9f61bde8d5f1d))


### Bug Fixes

* **core:** fix timer race in CircuitBreakerPolicy and token CAS race in RateLimiter ([1e67667](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/1e676678d6ab63188075f9f690543fe7fd3d09ae))
* **core:** require MaxPerSecond, add missing XML docs on ResiliencePolicy and ResilienceException ([24b26a6](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/24b26a6c7c2fdc612668e3bbd4223a549289c4e4))
* **generator:** bake retry config as literals per-method to honour method-level overrides ([7416b4a](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/7416b4ac32ca64805a2bcec2c1510ca6f413b12c))
* **generator:** fix retry catch filter; store CT param name in model; use Thread.Sleep for sync retry ([82a716f](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/82a716f0e6c23ca576b4d7d2d374b917c1d009e2))
* **generator:** skip empty try/catch in single-call path; fix xUnit1031 in rate-limit test ([3ef4dba](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/3ef4dbaa56d96628b4ece4429662a5df0727bdd9))
* **generator:** use FullyQualifiedFormat in SignaturesMatch; add fallback comment; minor cleanup ([f448cf6](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/f448cf6e277826f62118646f0a61a2d214fdfa4a))


### Refactoring

* **generator:** use RateLimitScope enum; remove redundant FallbackMethod from CircuitBreakerConfig ([96bd935](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/96bd9350a780034ef96f67ca1d2cf8b80b976829))


### Documentation

* add documentation stubs (getting-started, attributes, performance) ([4f7c41b](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/4f7c41b3e2722ee782c505663ad420a3ee5b7fbf))
* add extensive documentation matching ZeroAlloc.StateMachine quality ([226bdf7](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/226bdf7854978d00e224f518162140266f403d99))


### Tests

* **core:** add policy unit tests and proxy integration tests ([c789afa](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/c789afa80d8984e21034089cd3ed02743ec48b01))
* **core:** add timeout, halfOpenProbes, DI registration, and CB no-fallback tests ([d2f4dd7](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/d2f4dd76e98f26e7cb83b59bd7468a9e5aaa4774))
* **generator:** add snapshot and diagnostic tests ([157f1e6](https://github.com/ZeroAlloc-Net/ZeroAlloc.Resilience/commit/157f1e62b148fbe0f4b4f20ab36f8abb9e1ca092))

## Changelog

All notable changes to this project will be documented in this file.

See [Conventional Commits](https://conventionalcommits.org) for commit guidelines.
