# Make `LogRecord.Timestamp` and `ObservedTimestamp` nullable

## Motivation

Currently `LogRecord.Timestamp` and `LogRecordData.Timestamp` are typed as
`DateTime`. Per the
[OpenTelemetry Log Data Model](https://opentelemetry.io/docs/specs/otel/logs/data-model/#field-timestamp),
`Timestamp` is **optional** — it represents the time the event occurred *as
reported by the source*. When no timestamp is provided the field should be
absent, not default to `DateTime.MinValue` or be silently set to
`DateTime.UtcNow`.

The same applies to `ObservedTimestamp` — it should be nullable so that the
SDK can distinguish "not yet observed" from an actual timestamp.

## Problem

`LogRecord.Timestamp` was already released as a **stable public API** with the
signature:

```csharp
public DateTime Timestamp { get; set; }
```

Changing it to `DateTime?` is a **breaking change** at both the ABI and source
level:

| Level | Why it breaks |
|---|---|
| **ABI (binary)** | `DateTime` and `Nullable<DateTime>` are different types at the IL level. The property getter/setter signatures change, causing `MissingMethodException` at runtime for consumers compiled against the old assembly. |
| **Source** | Existing code like `DateTime ts = logRecord.Timestamp;` or `logRecord.Timestamp.Kind` no longer compiles without changes. |

We do **not** want to make a major release just for this change.

`ObservedTimestamp` on `LogRecord` is a **new property** (unshipped) and can be
changed directly.

## Proposed Solution: Obsolete + New Nullable Property

### 1. `LogRecordData` (experimental / internal) — change directly

Since `LogRecordData` is behind `[Experimental]` / `internal`, both properties
can be changed to `DateTime?` directly:

```csharp
internal DateTime? TimestampBacking;
internal DateTime? ObservedTimestampBacking = DateTime.UtcNow;

public DateTime? Timestamp
{
    readonly get => this.TimestampBacking;
    set => this.TimestampBacking = value is { Kind: DateTimeKind.Local } v
        ? v.ToUniversalTime() : value;
}

public DateTime? ObservedTimestamp
{
    readonly get => this.ObservedTimestampBacking;
    set => this.ObservedTimestampBacking = value is { Kind: DateTimeKind.Local } v
        ? v.ToUniversalTime() : value;
}
```

Key detail: only `ObservedTimestampBacking` is auto-initialized to
`DateTime.UtcNow` — `TimestampBacking` starts as `null` ("not provided by
source").

### 2. `LogRecord` (stable public class) — preserve ABI, deprecate, add new

**`Timestamp` (stable — must not break):**

```csharp
[Obsolete("Use TimestampUtc instead. This property will be removed in a future major version.")]
public DateTime Timestamp
{
    get => this.Data.Timestamp ?? this.Data.ObservedTimestamp ?? default(DateTime);
    set => this.Data.Timestamp = value;
}
```

The getter falls back to `ObservedTimestamp` (which the SDK always sets at
creation time), preserving the current behavior where `Timestamp` always returns
a real UTC value. This avoids a silent behavioral change where consumers would
suddenly get `default(DateTime)` (0001-01-01).

**`TimestampUtc` (new nullable property):**

```csharp
public DateTime? TimestampUtc
{
    get => this.Data.Timestamp;
    set => this.Data.Timestamp = value;
}
```

**`ObservedTimestamp` (new/unshipped — can change directly):**

```csharp
public DateTime? ObservedTimestamp
{
    get => this.Data.ObservedTimestamp;
    set => this.Data.ObservedTimestamp = value;
}
```

### 3. Internal SDK callers — no changes needed

In `OpenTelemetryLogger.cs` the SDK already sets both backing fields explicitly:

```csharp
data.TimestampBacking = DateTime.UtcNow;
data.ObservedTimestampBacking = data.TimestampBacking;
```

`DateTime` implicitly converts to `DateTime?`, so these assignments continue to
work unchanged.

### 4. Exporters — handle null

Exporters are updated to use the new nullable properties:

```csharp
// OTLP: send 0 if null (per proto spec, 0 means "not set")
var timestamp = logRecord.TimestampUtc.HasValue
    ? (ulong)logRecord.TimestampUtc.Value.ToUnixTimeNanoseconds()
    : 0UL;

var observedTimestamp = logRecord.ObservedTimestamp.HasValue
    ? (logRecord.ObservedTimestamp == logRecord.TimestampUtc ? timestamp
        : (ulong)logRecord.ObservedTimestamp.Value.ToUnixTimeNanoseconds())
    : 0UL;
```

## Backward Compatibility Analysis

| Concern | Impact |
|---|---|
| **ABI compat** | ✅ `DateTime Timestamp { get; set; }` signature is **unchanged** — no `MissingMethodException` |
| **Source compat** | ✅ Existing code `logRecord.Timestamp = x` and `var ts = logRecord.Timestamp` still compiles (with obsolete warning) |
| **Behavioral compat** | ✅ `logRecord.Timestamp` still returns a real UTC value via fallback to `ObservedTimestamp` |
| **New consumers** | ✅ Use `TimestampUtc` to get nullable semantics |
| **Future major** | Remove the obsolete `Timestamp` property |

## Alternatives Considered

### `HasTimestamp` pattern (protobuf-style)

```csharp
public DateTime Timestamp { get; set; }   // returns default if not set
public bool HasTimestamp { get; }          // true if explicitly set
public void ClearTimestamp();              // resets to null internally
```

**Pros:** No new property name.
**Cons:** More API surface (3 members vs 1), awkward usage pattern, no way to
check-and-read atomically, does not align with idiomatic C# nullable patterns.

### Change directly to `DateTime?` (breaking)

Would require a major version bump. Not viable given the project's versioning
constraints.

## Files Changed

| File | Change |
|---|---|
| `src/OpenTelemetry.Api/Logs/LogRecordData.cs` | Backing fields → `DateTime?`, properties → `DateTime?`, only `ObservedTimestampBacking` auto-inits |
| `src/OpenTelemetry/Logs/LogRecord.cs` | `Timestamp` kept + `[Obsolete]` with fallback, added `TimestampUtc`, `ObservedTimestamp` → `DateTime?` |
| `src/OpenTelemetry.Exporter.OpenTelemetryProtocol/…/ProtobufOtlpLogSerializer.cs` | Uses `TimestampUtc`/`ObservedTimestamp` with null handling |
| `src/OpenTelemetry.Exporter.Console/ConsoleLogRecordExporter.cs` | Uses `TimestampUtc` with null check |
| PublicAPI files | Updated / added entries for new surface |
| Tests | Updated assertions for nullable semantics |
