using System.Collections.Generic;
using SolidWorksCadAgent.Contracts.Cad;

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

        public static IReadOnlyList<string> Validate(IEnumerable<CadCommandEnvelope> commands)
        {
            var errors = new List<string>();
            if (commands == null) return errors;

            // Existing clients may target an already-open document. We cannot inspect that initial
            // state in Core, so only explicit CloseDocument transitions make it known to be closed.
            var document = DocumentState.Unknown;
            var sketchOpen = false;
            var sketchHasClosedProfilePrimitive = false;
            var completedProfileAvailable = false;
            var savePartCompleted = false;
            var index = 0;

            foreach (var envelope in commands)
            {
                index++;
                var command = envelope?.Command;
                if (savePartCompleted && ChangesModelOrDocumentTarget(command))
                {
                    Add(errors, index, command, "SavePart is the final model-changing operation; no later CAD modification or document switch is allowed.");
                    break;
                }

                switch (command)
                {
                    case CadCommandNames.NewPart:
                    case CadCommandNames.OpenPart:
                        if (sketchOpen)
                            Add(errors, index, command, "ExitSketch before changing the active document.");
                        else
                        {
                            document = DocumentState.Open;
                            completedProfileAvailable = false;
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
                            completedProfileAvailable = false;
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
                            completedProfileAvailable = false;
                        }
                        break;

                    case CadCommandNames.AddLine:
                    case CadCommandNames.AddArc:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (!sketchOpen)
                            Add(errors, index, command, "Profile geometry requires an open sketch created earlier in the plan.");
                        break;

                    case CadCommandNames.AddRectangle:
                    case CadCommandNames.AddCircle:
                    case CadCommandNames.AddSlot:
                    case CadCommandNames.AddRegularPolygon:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (!sketchOpen)
                            Add(errors, index, command, "Profile geometry requires an open sketch created earlier in the plan.");
                        else
                            sketchHasClosedProfilePrimitive = true;
                        break;

                    case CadCommandNames.ExitSketch:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (!sketchOpen)
                            Add(errors, index, command, "There is no open sketch to exit.");
                        else
                        {
                            sketchOpen = false;
                            completedProfileAvailable = sketchHasClosedProfilePrimitive;
                            sketchHasClosedProfilePrimitive = false;
                        }
                        break;

                    case CadCommandNames.Extrude:
                    case CadCommandNames.CutExtrude:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (sketchOpen)
                            Add(errors, index, command, "ExitSketch before creating a feature.");
                        else if (!completedProfileAvailable)
                            Add(errors, index, command, "Feature creation requires a completed sketch containing a supported closed profile primitive.");
                        else
                            completedProfileAvailable = false;
                        break;

                    case CadCommandNames.SavePart:
                    case CadCommandNames.Rebuild:
                        if (RequireDocument(errors, index, command, document)) break;
                        if (command == CadCommandNames.SavePart && sketchOpen)
                            Add(errors, index, command, "ExitSketch before saving the document.");
                        else if (command == CadCommandNames.SavePart)
                            savePartCompleted = true;
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

        private static void Add(List<string> errors, int index, string command, string message) =>
            errors.Add("Command " + index + " (" + command + "): " + message);
    }
}
