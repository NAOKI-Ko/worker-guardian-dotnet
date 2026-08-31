# Contributing

Contributions that make worker supervision safer, clearer, or more observable are welcome.

1. Discuss significant public API or lifecycle-semantics changes in an issue.
2. Branch from `main` and keep commits focused on meaningful behavior.
3. Add tests for normal operation, failure, cancellation, and concurrency where relevant.
4. Run the Release build, test executable, coverage gate, and `dotnet format` verification.
5. Update architecture and usage documentation for visible behavior changes.
6. Open a pull request describing the problem, design, trade-offs, and verification evidence.

Avoid wall-clock sleeps as synchronization when a signal can express the condition. New public
models should remain immutable where practical, nullable warnings must stay clean, and event
delivery changes must document backpressure behavior. Benchmark results must include the execution
environment and must not be presented as universal.

By participating, you agree to collaborate respectfully and keep review focused on the work.
