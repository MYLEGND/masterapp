# Native runtime observations

`LegendApiClient` retains ownership of bearer authentication and HTTP transport.
Its diagnostics interceptor records bounded HTTP/network observations, while the
existing repository request wrapper records caught non-transport failures using
compiler frame symbols only. `LegendLogger` queues a fixed authentication failure
observation. Uploads use `POST /api/v1/mobile/runtime-diagnostics` through that same
client and token authority. An absent token prevents upload before network access.
The server alone owns issue classification and Founder diagnostic access.

Payloads omit request/response bodies, URL queries, dynamic path values, exception
messages, credentials and user identifiers. The queue is app-private under
`noBackupFilesDir`, capped at 20 deduplicated events / 64 KiB, with at most three
attempts per event and three sends per minute. Successful authenticated requests
also offer replay after relaunch. There is no timer or autonomous credential flow;
upload failures cannot recursively create diagnostics.

Application initialization chains the existing uncaught-exception handler. Before
calling it, a best-effort local write saves one fixed, sanitized crash observation
(at most 4 KiB). This handler performs no network operation and takes no queue
lock. The original throwable reaches the previous handler even when writing fails.
OOM, native signals, forced process death and storage failures can prevent capture;
this is not a guarantee that all crashes are observed. The next authenticated
launch replays an available snapshot through the normal queue.

Gradle obtains `BuildConfig.GIT_COMMIT_HASH` from the build checkout, and the event
includes the generated app version/code. Client release metadata remains an
unverified hint at the server. No new diagnostic UI or management authority is
exposed to non-Founder users; this module only submits observations.

Focused JVM tests: `:app:testDebugUnitTest --tests
com.mylegnd.legend.registered.RuntimeDiagnosticsTest`. These cover payload privacy,
queue bounds, deduplication, persistence/retry exhaustion, refusal of unauthenticated
uploads, and preservation of the prior crash handler even on write failure. Run
with the normal checkout guard and intended test ref; no signed build, installation
or deployment is required. Device-level crash delivery and live ingestion remain
separate verification steps.
