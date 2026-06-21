# Logging convention (KGSM ecosystem)

The single, shared rule for how the **.NET** projects do logging. It is a **documented
convention, not a shared package** — there is no `TheKrystalShip.Logging` to depend on. Each
project applies the same small setup itself, so a leaf stays independently deployable (keystone
§4) and the AOT daemons carry no extra coupling.

> **Why a convention and not a library:** the projects span Native-AOT daemons, ASP.NET hosts,
> and a bare console app — one package can't fit all three without dragging hosting into the
> AOT helpers. The setup is ~3 lines; copy it, don't abstract it.

## The rules (all .NET projects)

1. **Framework:** Microsoft.Extensions.Logging (`ILogger<T>`) only. **No** Serilog/NLog/log4net.
2. **One sink, journald-native:** the **SystemdConsole** formatter (`AddSystemdConsole()`), from
   the `Microsoft.Extensions.Logging.Console` package/shared-framework. It emits `<N>` syslog
   **priority prefixes** (`<3>` err, `<4>` warn, `<6>` info, `<7>` debug) so `journalctl -p err`
   can filter by level, and it omits the timestamp/colour journald already adds. Every project is
   deployed as a **systemd service → journald**, so this is the correct console formatter. No file
   logging — journald owns retention. (Reach for `AddJsonConsole` only if a host ever ships logs to
   a non-journald collector; none do today.)
3. **Default level `Information`**, set in **`appsettings.json`** under `Logging:LogLevel:Default`,
   **overridable by environment variables** — `Logging__LogLevel__Default=Debug` (double-underscore
   is the .NET config convention) wins over the file. This is the runtime "turn up the logs when
   something's wrong" knob, and it works identically on every service, AOT or JIT.
4. **ASP.NET / Kestrel hosts** also pin `"Microsoft.AspNetCore": "Warning"` in the `Logging` block
   to keep framework request noise out of the log.
5. **Structured templates only:** `logger.LogInformation("… {Id}", id)` — never string
   interpolation (`$"…"`). Named placeholders are what make journald fields queryable.
6. **Secrets never reach the log channel.** A client whose URL *is* the secret (e.g. a Discord/Slack
   webhook) must strip the HTTP loggers: `AddHttpClient<…>(…).RemoveAllLoggers()`. The default
   `IHttpClientFactory` logger writes `POST {uri}` at Information — that would leak the token. See
   `kgsm-api/src/Api/Startup.cs`.
7. **AOT stays 0-warning.** These packages are AOT-safe — the logging config binder reads the
   `LogLevel` keys directly (no reflection binding of a complex type). `dotnet publish -r linux-x64`
   must remain a clean ILC pass (0 IL2026/IL3050/ILC) on firewall/monitor/watchdog. Verified: the
   firewall AOT binary grew **~0.8 MiB (+23%)** adopting this — accepted.

## How to apply it, per host flavor

The body is always the same: clear the default providers, add the Systemd console, let the
`Logging` config section drive levels. Only the builder handle differs.

**ASP.NET generic host** — `Host.CreateDefaultBuilder` (kgsm-api). The default builder already
binds the `Logging` section + env; just swap providers:
```csharp
.ConfigureLogging((_, logging) =>
{
    logging.ClearProviders();
    logging.AddSystemdConsole();
})
```

**ASP.NET minimal host** — `WebApplication.CreateBuilder` (kgsm-llm Service):
```csharp
builder.Logging.ClearProviders();
builder.Logging.AddSystemdConsole();
```

**AOT slim host** — `WebApplication.CreateSlimBuilder` (kgsm-monitor, kgsm-watchdog). Bind the
section **explicitly** — don't rely on the slim builder doing it implicitly — and ship an
`appsettings.json` (the Web SDK copies it to the publish dir automatically):
```csharp
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddSystemdConsole();
```

**Bare console app** — plain `Microsoft.NET.Sdk`, no host (kgsm-firewall daemon). Build the config
+ factory by hand; add `Microsoft.Extensions.Logging.Console` +
`Microsoft.Extensions.Configuration.{Json,EnvironmentVariables}`:
```csharp
IConfiguration config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

return LoggerFactory.Create(builder =>
{
    builder.AddConfiguration(config.GetSection("Logging"));
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddSystemdConsole();
});
```

The standard `appsettings.json` `Logging` block:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```
(Drop the `Microsoft.AspNetCore` line in non-ASP.NET apps like the firewall — the category
never appears there.)

## The CLI variant (deliberate exception)

`kgsm-llm`'s **CLI** and **Eval** entrypoints are **interactive tools, not services**. The Systemd
formatter's `<N>` prefixes are meant for journald and look wrong in a terminal, and a chatty
Information default would pollute the reply stream. They keep the tuned console-app setup and are
**correct as-is** — do not "standardize" them onto SystemdConsole:

- `AddSimpleConsole` routed to **stderr** (`LogToStandardErrorThreshold = Trace`) so **stdout stays
  clean** for the actual reply/scorecard output.
- **Quiet by default** (`SetMinimumLevel(Warning)`), `--verbose` → `Debug`.

The shared rules above (MEL only, structured templates, no secrets) still apply.

## Behavioural notes

- **Async flush.** The console logger queues on a background thread and flushes on graceful
  shutdown (host stop / `LoggerFactory` dispose). A **hard** kill (SIGKILL, native crash) can drop
  the last buffered lines. This is standard MEL behaviour and uniform across all services — accepted.
- **`ClearProviders()` keeps levels.** It removes only `ILoggerProvider` registrations, not the
  config-based filters (`IConfigure<LoggerFilterOptions>`), so appsettings/env levels survive it.

## Per-project status

| Project | Host | Logging |
|---|---|---|
| kgsm-api | `Host.CreateDefaultBuilder` (JIT) | SystemdConsole, appsettings+env ✅ |
| kgsm-firewall (daemon) | bare console (AOT) | SystemdConsole, appsettings+env ✅ |
| kgsm-llm Service | `WebApplication.CreateBuilder` (JIT) | SystemdConsole, appsettings+env ✅ |
| kgsm-llm CLI / Eval | console host (JIT) | SimpleConsole→stderr, Warning/`--verbose` (CLI variant) ✅ |
| kgsm-monitor | `CreateSlimBuilder` (AOT) | SystemdConsole, appsettings+env ✅ |
| kgsm-watchdog | `CreateSlimBuilder` (AOT) | SystemdConsole, appsettings+env ✅ |

> **kgsm** (bash) is out of scope here — it has its own `core/logging.sh` (levels + rotation;
> file logging off by default). To be revisited separately.
