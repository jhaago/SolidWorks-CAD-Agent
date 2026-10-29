// Ported from czuryk/SolidworksMCP eccb8af, automation/shape_edit_compatibility.py (MIT).
// Copyright (c) 2026 Samsaam Ali Baig. See third_party/SolidworksMCP/LICENSE.
// Changes: C#/net48, reject duplicate/missing facts instead of overwriting; bound comparisons.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SolidWorksCadAgent.Core.Geometry
{
    public sealed class FeatureInventoryDifference
    {
        internal FeatureInventoryDifference(string[] added,string[] removed,string[] changed) { Added=added;Removed=removed;TypeChanged=changed; }
        public string[] Added { get; }
        public string[] Removed { get; }
        public string[] TypeChanged { get; }
        public bool Matches => Added.Length==0&&Removed.Length==0&&TypeChanged.Length==0;
        public string Scope => "feature_names_and_types; independent persistent-reference and geometry checks still required";
    }
    public static class FeatureInventory
    {
        public static IReadOnlyDictionary<string,string> Capture(IEnumerable<KeyValuePair<string,string>> features)
        {
            if(features==null)throw new ArgumentNullException(nameof(features));
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var pair in features)
            {
                if(result.Count>=10000||string.IsNullOrWhiteSpace(pair.Key)||string.IsNullOrWhiteSpace(pair.Value)||result.ContainsKey(pair.Key))
                    throw new ArgumentException("Feature inventory is oversized, incomplete or has duplicate names.");
                result.Add(pair.Key,pair.Value);
            }
            return new ReadOnlyDictionary<string,string>(result);
        }
        public static FeatureInventoryDifference Compare(IReadOnlyDictionary<string,string> expected,IReadOnlyDictionary<string,string> actual)
        {
            if(expected==null||actual==null)throw new ArgumentNullException(nameof(expected));
            // Capture validates caller-supplied dictionary facts, including complete native type names.
            expected=Capture(expected);actual=Capture(actual);
            return new FeatureInventoryDifference(actual.Keys.Except(expected.Keys,StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
                expected.Keys.Except(actual.Keys,StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray(),
                expected.Keys.Intersect(actual.Keys,StringComparer.Ordinal).Where(k=>expected[k]!=actual[k]).OrderBy(x=>x,StringComparer.Ordinal).ToArray());
        }
    }
}
