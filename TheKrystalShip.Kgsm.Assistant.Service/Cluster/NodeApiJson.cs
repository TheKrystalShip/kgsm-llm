using System.Text.Json.Serialization;

namespace TheKrystalShip.Kgsm.Assistant.Service.Cluster;

/// <summary>The shapes a node answers in, generated rather than reflected over.</summary>
[JsonSerializable(typeof(List<NodeServer>))]
[JsonSerializable(typeof(List<NodeLibraryEntry>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class NodeApiJson : JsonSerializerContext;
