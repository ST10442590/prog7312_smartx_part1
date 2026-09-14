namespace SmartX.Shared.Topology;

public enum IssueSeverity { Warning = 0, Error = 1 }

public sealed record ValidationIssue(
    IssueSeverity Severity,
    string Path,
    string Message);

public sealed class ValidationReport
{
    public List<ValidationIssue> Issues { get; } = new();
    public int NodesVisited { get; set; }
    public int MaxDepthReached { get; set; }

    public bool IsValid => !Issues.Any(i => i.Severity == IssueSeverity.Error);
    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
}

// Recursively walks a deployment tree and verifies that every device is
// safely configured within its ancestry
// (Sub-Zone B -&gt; Zone 1 -&gt; Facility A).
// <remarks>
// <para><b>Termination.</b> Three separate guards keep the recursion safe
// on the deep, operator-authored hierarchies this system has to accept:</para>
// <list type="number">
//   <item>A natural base case — a leaf node has no children to descend into.</item>
//   <item>A hard depth ceiling (<see cref="MaxDepth"/>), so a malformed or
//         hostile configuration file cannot exhaust the stack.</item>
//   <item>A visited set, so a tree containing a cycle (a node reachable
//         from its own descendant) is reported rather than followed
//         forever.</item>
// </list>
// </remarks>
public static class RecursiveTopologyValidator
{
    // Deepest nesting accepted. Four tiers are meaningful; the ceiling is
    // set well above that purely as a stack-safety backstop.
    public const int MaxDepth = 64;

    // Validates a whole tree from its root.
    public static ValidationReport Validate(DeploymentNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var report = new ValidationReport();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ValidateNode(
            node: root,
            parent: null,
            path: root.Name,
            depth: 0,
            report: report,
            visited: visited,
            poweredAncestor: root.ProvidesMainsPower);

        return report;
    }

    // ------------------------------------------------------- recursion
    private static void ValidateNode(
        DeploymentNode node,
        DeploymentNode? parent,
        string path,
        int depth,
        ValidationReport report,
        HashSet<string> visited,
        bool poweredAncestor)
    {
        report.NodesVisited++;
        if (depth > report.MaxDepthReached) report.MaxDepthReached = depth;

        // GUARD 1 — depth ceiling. Stop before the stack does.
        if (depth >= MaxDepth)
        {
            report.Issues.Add(new ValidationIssue(
                IssueSeverity.Error, path,
                $"Nesting exceeds the maximum supported depth of {MaxDepth}. " +
                "Traversal stopped here to protect the stack."));
            return;
        }

        // GUARD 2 — cycle detection. A node already on this walk means the
        // "tree" is really a graph, and descending would never terminate.
        if (!string.IsNullOrWhiteSpace(node.Id) && !visited.Add(node.Id))
        {
            report.Issues.Add(new ValidationIssue(
                IssueSeverity.Error, path,
                $"Circular reference: node '{node.Id}' already appears higher " +
                "in this branch. Traversal stopped here."));
            return;
        }

        ValidateOwnRules(node, parent, path, report, poweredAncestor);

        var powered = node.ProvidesMainsPower || poweredAncestor;

        // BASE CASE — a leaf has no children, so the recursion unwinds here.
        foreach (var child in node.Children)
        {
            ValidateNode(
                node: child,
                parent: node,
                path: $"{path} -> {child.Name}",
                depth: depth + 1,
                report: report,
                visited: visited,
                poweredAncestor: powered);
        }

        // Release the id on the way back up so a node legitimately reused in
        // a *sibling* branch is not misreported as a cycle.
        if (!string.IsNullOrWhiteSpace(node.Id)) visited.Remove(node.Id);
    }

    private static void ValidateOwnRules(
        DeploymentNode node,
        DeploymentNode? parent,
        string path,
        ValidationReport report,
        bool poweredAncestor)
    {
        if (string.IsNullOrWhiteSpace(node.Name))
        {
            report.Issues.Add(new ValidationIssue(
                IssueSeverity.Error, path, "Node has no name."));
        }

        // Tier ordering: each child sits exactly one tier below its parent.
        if (parent is not null && (int)node.Tier != (int)parent.Tier + 1)
        {
            report.Issues.Add(new ValidationIssue(
                IssueSeverity.Error, path,
                $"Illegal nesting: a {node.Tier} cannot sit directly inside " +
                $"a {parent.Tier}."));
        }

        if (node.Tier == TopologyTier.Device)
        {
            // A device is the end of the line.
            if (!node.IsLeaf)
            {
                report.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error, path,
                    "A Device node cannot contain child nodes."));
            }

            if (string.IsNullOrWhiteSpace(node.MacAddress))
            {
                report.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error, path,
                    "Device is missing a MAC address."));
            }

            // The safety rule the brief describes: a device that needs mains
            // power must be inside an ancestry that supplies it.
            if (node.RequiresMainsPower && !poweredAncestor)
            {
                report.Issues.Add(new ValidationIssue(
                    IssueSeverity.Error, path,
                    "Device requires mains power, but no ancestor zone " +
                    "provides it."));
            }
        }
        else if (node.IsLeaf)
        {
            report.Issues.Add(new ValidationIssue(
                IssueSeverity.Warning, path,
                $"{node.Tier} contains no child nodes."));
        }
    }

    // -------------------------------------------------- recursive search
    // Recursively finds the ancestry path to a device, returning something
    // like <c>["Facility A", "Zone 1", "Sub-Zone B", "ESP32-C3"]</c>, or
    // null when the device is not in the tree. Used by the dashboard to
    // show where an alarming node actually sits.
    public static IReadOnlyList<string>? FindPath(DeploymentNode root, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(deviceId)) return null;

        var trail = new List<string>();
        return FindPathCore(root, deviceId, trail, 0) ? trail : null;
    }

    private static bool FindPathCore(
        DeploymentNode node, string deviceId, List<string> trail, int depth)
    {
        if (depth >= MaxDepth) return false;

        trail.Add(node.Name);

        // BASE CASE — match found, unwind with the trail intact.
        if (string.Equals(node.Id, deviceId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(node.MacAddress, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var child in node.Children)
        {
            if (FindPathCore(child, deviceId, trail, depth + 1)) return true;
        }

        // Backtrack: this branch was a dead end.
        trail.RemoveAt(trail.Count - 1);
        return false;
    }

    // Recursively counts device-tier leaves beneath a node.
    public static int CountDevices(DeploymentNode node, int depth = 0)
    {
        if (depth >= MaxDepth) return 0;

        var count = node.Tier == TopologyTier.Device ? 1 : 0;
        foreach (var child in node.Children)
        {
            count += CountDevices(child, depth + 1);
        }
        return count;
    }
}
