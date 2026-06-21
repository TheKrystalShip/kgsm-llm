# KGSM.Lib

KGSM-Lib is a C# library designed to interact with [KGSM][1], a lightweight 
game server manager for Linux. This library simplifies integration with KGSM 
by providing an intuitive API to manage game servers, blueprints, and 
instances, as well as listening to real-time events through a Unix Domain 
Socket.

## Features

- **SOLID Architecture**:
  Built following SOLID principles with clear interfaces and separation of concerns.

- **Event Handling**:
  Subscribe to real-time events emitted by KGSM for actions such as server 
  installations, startups, shutdowns, and more.

- **Blueprint Management**:
  Create, list, and manage server blueprints easily through simple method calls.

- **Instance Management**:
  Install, start, stop, restart, update, and manage game server instances 
  using a unified API.

- **Ad-Hoc Commands**:
  Execute custom commands that aren't explicitly mapped within the library.

- **Dependency Injection Support**:
  Easy integration with Microsoft's dependency injection container.

- **Logging Support**:
  Integrated with Microsoft's logging abstractions.

## Installation

You can install KGSM-Lib via NuGet:

```sh
dotnet add package TheKrystalShip.KGSM.Lib
```

Or using the NuGet package manager:

```sh
Install-Package TheKrystalShip.KGSM.Lib
```

## Usage Examples

### Basic Setup

```csharp
// Direct instantiation
using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Services;
using Microsoft.Extensions.Logging;

// Create a logger factory
using var loggerFactory = LoggerFactory.Create(builder => 
    builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

// Create the process runner
var processRunner = new ProcessRunner(
    loggerFactory.CreateLogger<ProcessRunner>());

// Create the Unix socket client
var socketClient = new UnixSocketClient(
    "/path/to/kgsm.sock", 
    loggerFactory.CreateLogger<UnixSocketClient>());

// Create the event service
var eventService = new EventService(
    socketClient, 
    loggerFactory.CreateLogger<EventService>());

// Create the blueprint service
var blueprintService = new BlueprintService(
    processRunner, 
    "/path/to/kgsm.sh", 
    loggerFactory.CreateLogger<BlueprintService>());

// Create the instance service
var instanceService = new InstanceService(
    processRunner, 
    "/path/to/kgsm.sh", 
    loggerFactory.CreateLogger<InstanceService>());

// Create the KGSM client
var kgsmClient = new KgsmClient(
    "/path/to/kgsm.sh",
    processRunner,
    blueprintService,
    instanceService,
    eventService,
    loggerFactory.CreateLogger<KgsmClient>());
```

### Using Dependency Injection

```csharp
using Microsoft.Extensions.DependencyInjection;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Core.Interfaces;

// Setup services
var services = new ServiceCollection();

// Add logging
services.AddLogging(builder => 
    builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

// Add KGSM services
services.AddKgsmServices("/path/to/kgsm.sh", "/path/to/kgsm.sock");

// Build service provider
var serviceProvider = services.BuildServiceProvider();

// Get KGSM client
var kgsmClient = serviceProvider.GetRequiredService<IKgsmClient>();
```

### Listening for Events

```csharp
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Core.Interfaces;

// Get the KGSM client from dependency injection
var kgsmClient = serviceProvider.GetRequiredService<IKgsmClient>();

// Register event handlers
kgsmClient.Events.RegisterHandler<InstanceInstalledData>(async data => {
    Console.WriteLine($"New instance installed: {data.InstanceName} using blueprint {data.Blueprint}");
    return Task.CompletedTask;
});

// Initialize event listening
kgsmClient.Events.Initialize();
```

### Managing Blueprints

```csharp
// Get all blueprints
Dictionary<string, Blueprint> blueprints = kgsmClient.Blueprints.GetAll();
foreach (var blueprint in blueprints)
{
    Console.WriteLine($"{blueprint.Key}: {blueprint.Value}");
}

// Create a new blueprint
Blueprint blueprint = new Blueprint
{
    Name = "MyGameServer",
    Ports = "27015",
    ExecutableFile = "server_bin",
    SteamAppId = "123456",
    IsSteamAccountRequired = true
};

kgsmClient.Blueprints.Create(blueprint);
```

### Managing Instances

```csharp
// Get all instances
Dictionary<string, Instance> instances = kgsmClient.Instances.GetAll();

// Install a new instance
kgsmClient.Instances.Install("MyBlueprint", "/install/path");

// Start an instance
kgsmClient.Instances.Start("MyInstance");

// Get logs
KgsmResult logs = kgsmClient.Instances.GetLogs("MyInstance");
Console.WriteLine(logs.Stdout);

// Check if instance is active
bool isActive = kgsmClient.Instances.IsActive("MyInstance");
Console.WriteLine($"Instance is active: {isActive}");

// Create a backup
kgsmClient.Instances.CreateBackup("MyInstance");

// Get backups
KgsmResult backups = kgsmClient.Instances.GetBackups("MyInstance");
Console.WriteLine(backups.Stdout);
```

### Ad-Hoc Commands

```csharp
// Execute a custom command
KgsmResult result = kgsmClient.AdHoc("--custom-command", "argument");
Console.WriteLine(result.Stdout);
```

## Project Structure

The library is organized following SOLID principles:

- **Core**: Contains interfaces and core models
- **Services**: Contains implementations of interfaces
- **Events**: Contains event-related functionality
- **Exceptions**: Contains custom exceptions
- **Extensions**: Contains extension methods for dependency injection

## License

This project is licensed under the GPL-3.0 license. See the [LICENSE][2] file for details.

[1]: https://github.com/TheKrystalShip/KGSM
[2]: LICENSE
