# TODO Improvements

- Enforce enum-backed queue state persistence end-to-end (DB converter/migration away from string states).
- Add OpenTelemetry exporters and instrument outbox dispatcher latency/retry metrics.
- Introduce versioned EF migrations and remove remaining startup `EnsureCreated` dependency.
- Add integration tests with Testcontainers/LocalStack for outbox crash-restart, duplicate delivery, and broker outages.
- Add explicit authorization policies + rate limits for diagnostics/maintenance/ops mutation endpoints.
- Add SQS/SNS transport side-by-side configuration and phased cutover plan from RabbitMQ.
- Add integration tests for enum provider jobs using controlled fake tool binaries to validate retry/isolation and outbox emission behavior end-to-end.
- Consider provider-specific queues (`subdomain-enumeration-subfinder`, `subdomain-enumeration-amass`) if worker pools become specialized by installed tooling.
