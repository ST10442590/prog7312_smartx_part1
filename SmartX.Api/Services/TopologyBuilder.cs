using SmartX.Shared.Sensors;
using SmartX.Shared.Topology;

namespace SmartX.Api.Services;

/// <summary>
/// Builds a deployment tree from the flat registry and runs the recursive
/// validator over it.
/// </summary>
/// <remarks>
/// Registration captures each device's ancestry as four strings (facility,
/// zone, sub-zone, node). That is convenient to type into a form but useless
/// for answering structural questions: whether an actuator has mains power
/// somewhere above it, or where a given node actually sits. Rebuilding the
/// flat list into a real tree is what makes the recursive checks possible.
/// </remarks>
public sealed class TopologyBuilder
{
    private readonly DeviceRegistry _registry;

    public TopologyBuilder(DeviceRegistry registry) => _registry = registry;

    /// <summary>
    /// Assembles the tree. Zones are treated as mains-powered and sub-zones
    /// inherit from them, so an actuator registered outside a powered
    /// ancestry is caught by the validator rather than by a site visit.
    /// </summary>
    public DeploymentNode Build()
    {
        var root = new DeploymentNode
        {
            Id = "facility-root",
            Name = "Facility A",
            Tier = TopologyTier.Facility,
            ProvidesMainsPower = true
        };

        foreach (var profile in _registry.AllProfiles())
        {
            var facility = string.IsNullOrWhiteSpace(profile.Facility)
                ? "Unassigned Facility" : profile.Facility;

            // Everything hangs off one facility root; a profile naming a
            // different facility is recorded but not given its own root,
            // since the validator walks a single tree.
            if (!string.Equals(facility, root.Name, StringComparison.OrdinalIgnoreCase))
            {
                root.Name = facility;
            }

            var zone = FindOrAdd(root, profile.Zone, TopologyTier.Zone,
                providesPower: true);

            var subZone = FindOrAdd(zone, profile.SubZone, TopologyTier.SubZone,
                providesPower: false);

            subZone.Children.Add(new DeploymentNode
            {
                Id = profile.Id,
                Name = profile.DisplayName,
                Tier = TopologyTier.Device,
                MacAddress = profile.MacAddress,
                RequiresMainsPower = profile.RequiresMainsPower
            });
        }

        return root;
    }

    private static DeploymentNode FindOrAdd(
        DeploymentNode parent, string name, TopologyTier tier, bool providesPower)
    {
        var label = string.IsNullOrWhiteSpace(name) ? $"Unassigned {tier}" : name;

        var existing = parent.Children.FirstOrDefault(c =>
            string.Equals(c.Name, label, StringComparison.OrdinalIgnoreCase));

        if (existing is not null) return existing;

        var node = new DeploymentNode
        {
            Id = $"{tier}-{label}".ToLowerInvariant().Replace(' ', '-'),
            Name = label,
            Tier = tier,
            ProvidesMainsPower = providesPower
        };

        parent.Children.Add(node);
        return node;
    }

    /// <summary>Runs the recursive validation over the current mesh.</summary>
    public ValidationReport Validate() =>
        RecursiveTopologyValidator.Validate(Build());

    /// <summary>Recursively resolves a device's ancestry path.</summary>
    public IReadOnlyList<string>? PathTo(string deviceId) =>
        RecursiveTopologyValidator.FindPath(Build(), deviceId);

    /// <summary>Recursively counts device leaves in the tree.</summary>
    public int DeviceCount() =>
        RecursiveTopologyValidator.CountDevices(Build());
}
