using System;
using System.Collections.Generic;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    public sealed class FeatureTreeEntry<T> where T : class
    {
        internal FeatureTreeEntry(T node, T parent, int depth) { Node = node; Parent = parent; Depth = depth; }
        public T Node { get; }
        public T Parent { get; }
        public int Depth { get; }
    }

    /// <summary>Depth-first traversal; repeated references are deduplicated, cycles/limits fail closed.</summary>
    public static class BoundedFeatureTraversal
    {
        public static IReadOnlyList<FeatureTreeEntry<T>> Walk<T>(IEnumerable<T> roots, Func<T, string> identity,
            Func<T, IEnumerable<T>> children, int maxVisits = 10000, int maxDepth = 64) where T : class
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (children == null) throw new ArgumentNullException(nameof(children));
            if (maxVisits < 1 || maxDepth < 0) throw new ArgumentOutOfRangeException(nameof(maxVisits));
            var output = new List<FeatureTreeEntry<T>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ancestors = new HashSet<string>(StringComparer.Ordinal);
            int visits = 0;
            void Visit(IEnumerable<T> nodes, T parent, int depth)
            {
                if (nodes == null) throw new InvalidOperationException("Native feature enumeration is unavailable.");
                var siblings = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in nodes)
                {
                    if (++visits > maxVisits) throw new InvalidOperationException("Feature inspection exceeded its visit limit.");
                    if (depth > maxDepth) throw new InvalidOperationException("Feature inspection exceeded its depth limit.");
                    if (node == null) throw new InvalidOperationException("Native feature enumeration contains a null node.");
                    var key = identity(node);
                    if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Native feature identity is unavailable.");
                    if (!siblings.Add(key) || ancestors.Contains(key)) throw new InvalidOperationException("Native feature enumeration contains a cycle or duplicate sibling identity.");
                    if (!seen.Add(key)) continue;
                    output.Add(new FeatureTreeEntry<T>(node, parent, depth));
                    ancestors.Add(key);
                    Visit(children(node), node, depth + 1);
                    ancestors.Remove(key);
                }
            }
            Visit(roots, null, 0);
            return output;
        }
    }
}
