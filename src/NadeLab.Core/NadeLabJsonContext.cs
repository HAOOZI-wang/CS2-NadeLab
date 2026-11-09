using System.Text.Json.Serialization;

namespace NadeLab.Core;

// Generate concrete accessors at build time. The hosted runtime need not emit
// reflection getters for collectible plugin enums/records during game callbacks.
[JsonSourceGenerationOptions(IncludeFields=true)]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(FlashSearchResult))]
[JsonSerializable(typeof(FlashObserver[]))]
[JsonSerializable(typeof(SavedRoute[]))]
[JsonSerializable(typeof(Trajectory))]
[JsonSerializable(typeof(Candidate))]
[JsonSerializable(typeof(Aim))]
[JsonSerializable(typeof(Aim[]))]
[JsonSerializable(typeof(ThrowStart))]
[JsonSerializable(typeof(Target))]
[JsonSerializable(typeof(PhysicsProfile))]
[JsonSerializable(typeof(ThrowObservation))]
[JsonSerializable(typeof(System.Numerics.Vector3))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
public partial class NadeLabJsonContext : JsonSerializerContext;
