using System.Collections.Generic;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>
    /// Performs the small amount of sequence validation needed to protect document and sketch
    /// lifecycle, final-save ordering and whether a supported, explicitly closed profile primitive is
    /// available to consume. It does not validate arbitrary profile closure, feature references or
    /// resulting geometry.
    /// </summary>
    public static class CadPlanLifecycleValidator
    {
        private enum DocumentState
        {
            Unknown,
            Open,
            Closed
        }

        private enum ProfileKind
        {
            None,
            Rectangle,
            Circle,
            Unsupported
        }

        public static IReadOnlyList<string> Validate(
            IEnumerable<CadCommandEnvelope> commands,
            ExecutionMode executionMode = ExecutionMode.Real)
            => ValidateCore(commands, executionMode, false);

        // V2 preflight validates each named, closed, single-consumer sketch itself.
        // Reuse document/sketch/save lifecycle checks without V1's one-profile slot.
        internal static IReadOnlyList<string> ValidateReferenceAware(IEnumerable<CadCommandEnvelope> commands)
            => ValidateCore(commands, ExecutionMode.Real, true);

        private static IReadOnlyList<string> ValidateCore(
            IEnumerable<CadCommandEnvelope> commands,
            ExecutionMode executionMode,
            bool referenceAwareProfiles)
        {
            var errors = new List<string>();
            if (commands == null) return errors;

            // Existing clients may target an already-open document. We cannot inspect that initial
            // state in Core, so only explicit CloseDocument transitions make it known to be closed.
            var document = DocumentState.Unknown;
            var sketchOpen = false;
            var sketchHasClosedProfilePrimitive = false;
            var sketchProfileKind = ProfileKind.None;
            var completedProfileAvailable = false;
            var completedProfileKind = ProfileKind.None;
            var savePartCompleted = false;
            var openedExistingPartIsReadOnly = false;
            var index = 0;

            foreach (var envelope in commands)
            {
                index++;
                var command = envelope?.Command;
                if (openedExistingPartIsReadOnly && ModifiesOpenedExistingPart(command))
                    Add(errors, index, command, "An opened existing part is read-only in this workflow; create an approved managed working copy before modifying or saving it.");
                if (savePartCompleted && ChangesModelOrDocumentTarget(command))
                {
                    Add(errors, index, command, "SavePart is the final model-changing operation; no later CAD modification or document switch is allowed.");
                    break;
                }

                switch (command)
                {
                    case CadCommandNames.NewPart:
                        openedExistingPartIsReadOnly = false;
                        if (sketchOpen)
                        {
                            Add(errors, index, command, "ExitSketch before changing the active document.");
                        }
                        else
                        {
                            document = DocumentState.Open;
                            completedProfileAvailable = false;
                            completedProfileKind = ProfileKind.None;
                        }
                        break;

                    case CadCommandNames.OpenPart:
                        if (executionMode == ExecutionMode.Simulation && command == CadCommandNames.OpenPart)
                            Add(errors, index, command, "OpenPart is unavailable in simulation mode.");
                        if (sketchOpen)
                            Add(errors, index, command, "ExitSketch before changing the active document.");
                        else
                        {
                            document = DocumentState.Open;
                            openedExistingPartIsReadOnly = command == CadCommandNames.OpenPart;
                            completedProfileAvailable = false;
                            completedProfileKind = ProfileKind.None;
                        }
                        break;

                    case CadCommandNames.CloseDocument:
                        if (document == DocumentState.Closed)
                            Add(errors, index, command, "There is no open document to close.");
                        else if (sketchOpen)
                            Add(errors, index, command, "ExitSketch before closing the document.");
                        else
                        {
                            document = DocumentState.Closed;
                            openedExistingPartIsReadOnly = false;
                            completedProfileAvailable = false;
                            completedProfileKind = ProfileKind.None;
                        }
                        break;

                    case CadCommandNames.CreateSketch:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (sketchOpen)
                            Add(errors, index, command, "Exit the already open sketch before creating another sketch.");
                        else
                        {
                            sketchOpen = true;
                            sketchHasClosedProfilePrimitive = false;
                            sketchProfileKind = ProfileKind.None;
                            completedProfileAvailable = false;
                            completedProfileKind = ProfileKind.None;
                        }
                        break;

                    case CadCommandNames.AddLine:
                    case CadCommandNames.AddArc:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (!sketchOpen)
                            Add(errors, index, command, "Profile geometry requires an open sketch created earlier in the plan.");
                        else
                            sketchProfileKind = ProfileKind.Unsupported;
                        break;

                    case CadCommandNames.AddSlot:
                    case CadCommandNames.AddRegularPolygon:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (!sketchOpen)
                            Add(errors, index, command, "Profile geometry requires an open sketch created earlier in the plan.");
                        else
                        {
                            sketchHasClosedProfilePrimitive = true;
                            sketchProfileKind = AddProfile(sketchProfileKind, ProfileKind.Unsupported);
                        }
                        break;

                    case CadCommandNames.AddRectangle:
                    case CadCommandNames.AddCircle:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (!sketchOpen)
                            Add(errors, index, command, "Profile geometry requires an open sketch created earlier in the plan.");
                        else
                        {
                            sketchHasClosedProfilePrimitive = true;
                            sketchProfileKind = AddProfile(sketchProfileKind,
                                command == CadCommandNames.AddRectangle ? ProfileKind.Rectangle : ProfileKind.Circle);
                        }
                        break;

                    case CadCommandNames.ExitSketch:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (!sketchOpen)
                            Add(errors, index, command, "There is no open sketch to exit.");
                        else
                        {
                            sketchOpen = false;
                            completedProfileAvailable = sketchHasClosedProfilePrimitive;
                            completedProfileKind = completedProfileAvailable ? sketchProfileKind : ProfileKind.None;
                            sketchHasClosedProfilePrimitive = false;
                            sketchProfileKind = ProfileKind.None;
                        }
                        break;

                    case CadCommandNames.Extrude:
                    case CadCommandNames.CutExtrude:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (sketchOpen)
                            Add(errors, index, command, "ExitSketch before creating a feature.");
                        else if (!referenceAwareProfiles && !completedProfileAvailable)
                            Add(errors, index, command, "Feature creation requires a completed sketch containing a supported closed profile primitive.");
                        else
                        {
                            if (executionMode == ExecutionMode.Simulation && command == CadCommandNames.Extrude && completedProfileKind != ProfileKind.Rectangle)
                                Add(errors, index, command, "Simulation supports Extrude only with a single rectangle profile.");
                            if (executionMode == ExecutionMode.Simulation && command == CadCommandNames.CutExtrude)
                            {
                                if (completedProfileKind != ProfileKind.Circle)
                                    Add(errors, index, command, "Simulation supports CutExtrude only with a single circular profile.");
                                if ((string)envelope?.Parameters?["endCondition"] == "Blind")
                                    Add(errors, index, command, "Simulation does not support Blind CutExtrude.");
                            }
                            completedProfileAvailable = false;
                            completedProfileKind = ProfileKind.None;
                        }
                        break;

                    case CadCommandNames.SavePart:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (executionMode == ExecutionMode.Simulation)
                            Add(errors, index, command, "SavePart is unavailable in simulation mode.");
                        if (sketchOpen)
                            Add(errors, index, command, "ExitSketch before saving the document.");
                        else
                            savePartCompleted = true;
                        break;

                    case CadCommandNames.Rebuild:
                        RequireDocument(errors, index, command, document);
                        break;
                }
            }

            return errors;
        }

        private static bool RequireDocument(
            List<string> errors,
            int index,
            string command,
            DocumentState document)
        {
            if (document != DocumentState.Closed) return false;
            Add(errors, index, command, "The plan closed the document; open a part before continuing.");
            return true;
        }

        private static bool ChangesModelOrDocumentTarget(string command)
        {
            switch (command)
            {
                case CadCommandNames.NewPart:
                case CadCommandNames.OpenPart:
                case CadCommandNames.SavePart:
                case CadCommandNames.CreateSketch:
                case CadCommandNames.AddLine:
                case CadCommandNames.AddArc:
                case CadCommandNames.AddRectangle:
                case CadCommandNames.AddCircle:
                case CadCommandNames.AddSlot:
                case CadCommandNames.AddRegularPolygon:
                case CadCommandNames.ExitSketch:
                case CadCommandNames.Extrude:
                case CadCommandNames.CutExtrude:
                case CadCommandNames.Rebuild:
                    return true;
                default:
                    // Keep document closure and future read-only inspection operations legal
                    // after the artifact has been saved.
                    return false;
            }
        }

        private static bool ModifiesOpenedExistingPart(string command)
        {
            switch (command)
            {
                case CadCommandNames.SavePart:
                case CadCommandNames.CreateSketch:
                case CadCommandNames.AddLine:
                case CadCommandNames.AddArc:
                case CadCommandNames.AddRectangle:
                case CadCommandNames.AddCircle:
                case CadCommandNames.AddSlot:
                case CadCommandNames.AddRegularPolygon:
                case CadCommandNames.ExitSketch:
                case CadCommandNames.Extrude:
                case CadCommandNames.CutExtrude:
                case CadCommandNames.Rebuild:
                    return true;
                default:
                    return false;
            }
        }

        private static ProfileKind AddProfile(ProfileKind existing, ProfileKind added) =>
            existing == ProfileKind.None ? added : ProfileKind.Unsupported;

        private static void Add(List<string> errors, int index, string command, string message) =>
            errors.Add("Command " + index + " (" + command + "): " + message);
    }
}
