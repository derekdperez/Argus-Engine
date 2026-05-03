# Build Fix 2.6.1

This patch fixes NuGet restore failures introduced by the OpenTelemetry package references in the executable projects.

## Changes

- Bumped deployment version from `2.6.0` to `2.6.1`.
- Updated worker and Gatekeeper OpenTelemetry package references to match the Infrastructure project:
  - `OpenTelemetry.Extensions.Hosting` `1.15.3`
  - `OpenTelemetry.Exporter.OpenTelemetryProtocol` `1.15.3`
  - `OpenTelemetry.Instrumentation.Http` `1.15.1`
  - `OpenTelemetry.Instrumentation.Runtime` `1.15.1`
  - `OpenTelemetry.Instrumentation.Process` `1.15.1-beta.1`

`OpenTelemetry.Instrumentation.Process` is still a beta OpenTelemetry .NET instrumentation package, so a beta version is required when using `AddProcessInstrumentation()`.
