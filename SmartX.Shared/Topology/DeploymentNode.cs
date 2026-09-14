namespace SmartX.Shared.Topology;

// The tier a node occupies. The numeric values encode the legal nesting
// order: a child must sit exactly one tier below its parent, which is what
// the recursive validator checks.
public enum TopologyTier
{
    Facility = 0,
    Zone = 1,
    SubZone = 2,
    Device = 3
}

// One node in the deployment tree, e.g.
// Facility A -&gt; Zone 1 -&gt; Sub-Zone B -&gt; ESP32-C3.
public sealed class DeploymentNode
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TopologyTier Tier { get; set; }

    // Devices only. Null for structural tiers. Used to cross-check that a
    // registered sensor is actually reachable in the tree.
    public string? MacAddress { get; set; }

    // Devices only. A powered actuator in an unpowered sub-zone is the
    // kind of unsafe configuration the validator is there to catch.
    public bool RequiresMainsPower { get; set; }

    // Structural tiers only. Whether mains power is available here.
    public bool ProvidesMainsPower { get; set; }

    public List<DeploymentNode> Children { get; set; } = new();

    public bool IsLeaf => Children.Count == 0;

    public override string ToString() => $"{Tier}:{Name}";
}
