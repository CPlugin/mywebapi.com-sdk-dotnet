# Changelog

Versions follow [semver](https://semver.org/); while the major version is 0, a minor release may contain breaking changes, listed under **Breaking**.

## 0.3.2

Batched ticks on the MT4 and MT5 v2 hubs: a symbol's ticks arrive as one event per batch instead of one event per tick. Additive — existing methods and signatures are unchanged.

### Added

- **Batched ticks, MT5 v2 hub.** `MT5V2SignalRClient.OnTicksBatch` receives `MT5TicksBatchPayload` — `Symbol` plus `Ticks` (`MT5TickPayload`, oldest first, never empty). `SubscribeToTicksBatchAsync(symbol, intervalMs = 200, maxBatchSize = 200)` starts the feed; the server sends a batch when its oldest tick has waited `intervalMs` milliseconds or as soon as `maxBatchSize` ticks are buffered. `UnsubscribeFromTicksBatchAsync(symbol)` stops it.
- **Batched ticks, MT4 v2 hub.** The same three members on `MT4V2SignalRClient` with `TicksBatchPayload` (`Symbol` plus `Ticks` of `TickPayload`). Repeating the subscribe call for a symbol replaces the previous subscription, so a new interval and size take effect.
- `intervalMs` accepts 20..60000 and `maxBatchSize` 1..10000; any other value is refused with a `HubException`. A batched subscription receives no per-tick `OnTick` events, and it lives until the connection closes.

## 0.3.1

Market data of MT5 v2: historical bars and ticks over REST, live ticks over the hub. Additive — existing methods and signatures are unchanged.

### Added

- **MT5 chart history.** `MT5Endpoints.ChartRequestAsync(symbol, period, from, to, limit)` returns `MT5ChartBarPage`: `Bars` ordered by time ascending (each with `Time`, `DateTime`, `Open`, `High`, `Low`, `Close`, `TickVolume`, `Spread`, `Volume`), `Total` and `Truncated`. `period` is `MT5ChartPeriod` — `M1`, `M5`, `M15`, `M30`, `H1`, `H4` or `D1`; the server rejects `W1` and `MN1`. Times are the trading times of the trade server, not UTC.
- **MT5 tick history.** `MT5Endpoints.TicksHistoryAsync(symbol, from, to, limit)` returns `MT5TickPage`: `Ticks` ordered by time ascending (each with `TimeMsc`, `Time`, `DateTime`, `Bid`, `Ask`, `Last`, `Volume`, `VolumeExt`, `Flags`), `Total` and `Truncated`. Prices are returned as the platform holds them, without rescaling; `flags` names the set tick-kind bits, as in the streaming events.
- **Live MT5 ticks.** `MT5V2SignalRClient` covers the hub's tick feed: `OnTick` (callback payload `MT5TickPayload` — `Symbol`, `Bid`, `Ask`, `Last`, `LastTime`, `VolumeExt`, `Flags`), `SubscribeToTicksAsync(symbol)` / `UnsubscribeFromTicksAsync(symbol)` for the callback model, and `StreamTicksAsync(symbol?)` for the stream model (`null` = every symbol). The trade platform is bound at connection time through the `tradePlatform` query parameter.
- `spec/v2.json` refreshed from the live v2 document; the 13 MT4 operations of the x86 sidecar (plugins, mail, news, binary external commands, snapshots, sync) are carried over from the previous spec because that build does not compile them, so the facade keeps its full surface (174 operations). The endpoint facade and the DTOs are regenerated from it.

## 0.3.0

Made for servers that support request timeouts; an older server does not read the `X-Request-Timeout` header, and the SDK then works as before.

### Added

- **Request timeouts.** `CallOptions.RequestTimeout` (per call) and `CPluginWebApiClientOptions.RequestTimeout` (client-wide default) set the server-side deadline, 1–300 s, sent as the `X-Request-Timeout` header. Without them the server applies its default per operation: trade 5 s, read 10 s, change 15 s, history 30 s, maintenance 60 s; each method's XML documentation names its own.
- The client-side wait is sized from the server-side deadline: deadline + 30 s (the server may extend a deadline by up to 20 s while it connects to the platform), never less than `CPluginWebApiClientOptions.Timeout`, so the server's `Timeout` / `OutcomeUnknown` answer normally arrives before the client gives up. Every one of the 172 operations has a server-side deadline and honours the header, the x86-sidecar ones (plugins, mail, news, snapshots, sync, binary external commands) included. A client-side expiry of a change or a trade is an unknown outcome, like `OutcomeUnknown`.
- New error codes in `ApiError.Code` and `WebApiErrorCode`: `Timeout` (a read did not finish; safe to repeat), `OutcomeUnknown` (a change or trade did not finish and may still be applied; also the answer to a repeat whose `Idempotency-Key` is still running), `Busy` (refused before it was sent to the trading platform; safe to repeat).
- `ApiError.Outcome` (the `X-Request-Outcome` header: `timeout`, `unknown`, `not-started`, `in-progress`), `ApiError.AppliedRequestTimeout` (`X-Request-Timeout-Applied`), and the helpers `IsTimeout`, `IsOutcomeUnknown`, `IsBusy`, `IsInProgress`, `IsSafeToRetry`. Constants in `ApiErrorCodes` and `RequestOutcomes`.
- README section "Timeouts and retries": defaults, options, codes, recovery with `Idempotency-Key`.
- **PATCH methods now take the patch object.** The six JSON Merge Patch operations (`MT4Endpoints.GroupRecordAsync`, `SymbolConfigAsync`, `UserRecordAsync`; `MT5Endpoints.GroupRecordAsync`, `UserRecordAsync`, `SymbolRecordAsync`) accept `object body` — only the fields to change, e.g. `new { Leverage = 100 }`; in 0.2.x they sent no body at all. `ExternalCommandJSONAsync` takes the command as a `JsonNode`.

### Changed

- The SDK's `HttpClient` no longer has a fixed timeout; each request gets its own deadline as described above. `CPluginWebApiClientOptions.Timeout` is now the minimum wait per request, and `ListTradePlatformsAsync` honours it the same way. A client-side deadline still throws `TaskCanceledException` wrapping a `TimeoutException`.
- DI pipeline (`AddCPluginWebApiSdk`): the standard resilience handler's attempt timeout (10 s) and total timeout (30 s) cut deadlines longer than that short; they are now backstops sized to the longest deadline (300 s + 30 s), and the circuit breaker's sampling window follows (twice the attempt timeout). The retry policy additionally never retries a response whose `X-Request-Outcome` is `unknown` or `in-progress`. With `Timeout = Timeout.InfiniteTimeSpan` the DI pipeline still ends an attempt after 1 hour. Writes were, and remain, never retried automatically.
- `ApiConnection` passes the deadline to reading the response stream as well (net8.0).
- Options validation rejects a non-positive `Timeout` (other than `Timeout.InfiniteTimeSpan`) and a `RequestTimeout` outside 1–300 s.

### Breaking

- The PATCH methods listed above and `ExternalCommandJSONAsync` gained a required `body` parameter before `options`.

- **Flag sets are strings.** The API sends flag sets as the names of the set bits joined by `", "` (`"Enabled, Password"`, `"None"` when empty). The former enums (`UsersRights`, `GroupRights`, `TickRequestFlags`, `TradeActivationFlags`, `TradeModifyFlags`, `EnCommReasonFlags`, `EnExpirationFlags`, `EnFillingFlags`, `EnInstantFlags`, `EnMarginCalcFlags`, `EnMarginFlags`, `EnOrderFlags`, `EnPermissionsFlags`, `EnReportsFlags`, `EnRequestFlags`, `EnSwapFlags`, `EnTickFlags`, `EnTradeFlags`, `EnTradeRightsFlags`) numbered their members sequentially rather than by bit, so a combination of flags could not be represented correctly; the properties and the `flags` parameter of `TicksRequestAsync` are now `string?`.
- `WebApiErrorCode` gains `Timeout`, `OutcomeUnknown` and `Busy` before `Internal`, so the numeric value of `Internal` changes from 7 to 10. The wire format uses names and is unaffected; code compiled against 0.2.x must be recompiled.
- Clients of 0.2.x cannot parse an envelope carrying one of the new error codes and throw `JsonException` instead of `ApiError`. The server returns these codes under its default deadlines too, without any opt-in by the client, so upgrade once the server supports request timeouts.

## 0.2.1 and earlier

See the [tags](https://github.com/CPlugin/mywebapi.com-sdk-dotnet/tags) of this repository.
