using System;
using System.Runtime.InteropServices;
using SolidWorksCadAgent.Contracts.Cad;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.References
{
    // Called only within the session STA. The callback must not retain native objects.
    internal static class SolidWorksSketchSelectionScope
    {
#if SOLIDWORKS_INTEROP
        internal static CadCommandResult Run(ModelDoc2 model, byte[] token, int mark, Func<ModelDoc2, CadCommandResult> action)
        {
            if (model == null || token == null || token.Length == 0 || mark < 0 || action == null)
                return SolidWorksReferenceResolver.Failure(Error("SKETCH_SELECTION_INVALID", "Validate", "A bound model, sketch token, nonnegative selection mark and action are required."));

            model.ClearSelection2(true);
            var actionStarted = false;
            try
            {
                var resolved = model.Extension.GetObjectByPersistReference3((byte[])token.Clone(), out var nativeStatus);
                var classification = SolidWorksReferenceResolver.ClassifySketchResolution(nativeStatus,
                    resolved is Sketch || resolved is Feature f && f.GetTypeName2() == "ProfileFeature" && f.GetSpecificFeature2() is Sketch);
                if (classification != null) return SolidWorksReferenceResolver.Failure(classification);

                var target = resolved as Feature ?? FindSketchFeature(model, (Sketch)resolved);
                if (target == null || target.GetTypeName2() != "ProfileFeature" || !(target.GetSpecificFeature2() is Sketch))
                    return SolidWorksReferenceResolver.Failure(Error("SKETCH_REFERENCE_KIND_MISMATCH", "Resolve", "The resolved sketch has no unique selectable feature."));
                if (!target.Select2(false, mark))
                    return SolidWorksReferenceResolver.Failure(Error("SKETCH_SELECTION_FAILED", "Select", "SOLIDWORKS did not select the resolved sketch feature."));

                var selection = (SelectionMgr)model.SelectionManager;
                var selected = selection.GetSelectedObject6(1, mark) as Feature;
                if (selection.GetSelectedObjectCount2(-1) != 1 || selection.GetSelectedObjectCount2(mark) != 1 ||
                    selected == null || !SameComIdentity(target, selected))
                    return SolidWorksReferenceResolver.Failure(Error("SKETCH_SELECTION_MISMATCH", "Select", "The selected object or mark differs from the resolved sketch feature."));
                actionStarted = true;
                return action(model);
            }
            catch (COMException ex)
            {
                // A consumer may already have mutated geometry. Its failure must propagate as an
                // uncertain operation, not be misreported as a harmless selection failure.
                if (actionStarted) throw;
                return SolidWorksReferenceResolver.Failure(Error("SKETCH_SELECTION_FAILED", "Select", "SOLIDWORKS failed during sketch selection.", ex.Message));
            }
            finally { model.ClearSelection2(true); }
        }

        private static Feature FindSketchFeature(ModelDoc2 model, Sketch sketch)
        {
            Feature match = null;
            for (var feature = model.FirstFeature() as Feature; feature != null; feature = feature.GetNextFeature() as Feature)
            {
                if (feature.GetTypeName2() != "ProfileFeature" || !SameComIdentity(feature.GetSpecificFeature2(), sketch)) continue;
                if (match != null) return null;
                match = feature;
            }
            return match;
        }

        private static bool SameComIdentity(object left, object right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || !Marshal.IsComObject(left) || !Marshal.IsComObject(right)) return false;
            var first = Marshal.GetIUnknownForObject(left);
            try
            {
                var second = Marshal.GetIUnknownForObject(right);
                try { return first == second; }
                finally { Marshal.Release(second); }
            }
            finally { Marshal.Release(first); }
        }
#endif

        private static CadError Error(string code, string stage, string message, string detail = null) =>
            new CadError { Code = code, Stage = stage, Message = message, Detail = detail };
    }
}
