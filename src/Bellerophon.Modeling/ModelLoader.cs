// <copyright file="ModelLoader.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling
{
    using Bellerophon.Modeling.Diagnostics;
    using Bellerophon.Modeling.Json;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// The result of loading a model file: the semantic model (when it could be built) and every diagnostic found.
    /// </summary>
    /// <param name="Path">The model file's path.</param>
    /// <param name="Model">The semantic model, or <c>null</c> if the file could not be parsed.</param>
    /// <param name="Diagnostics">The diagnostics, in the order found.</param>
    public sealed record ModelLoadResult(string Path, DataModel? Model, EquatableArray<ModelDiagnostic> Diagnostics)
    {
        /// <summary>
        /// Gets a value indicating whether any diagnostic is an error.  Code is generated only from models without errors.
        /// </summary>
        public bool HasErrors
        {
            get
            {
                foreach (var diagnostic in this.Diagnostics)
                {
                    if (diagnostic.Severity == ModelSeverity.Error)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Loads model files: parses the JSON, then builds and validates the semantic model.
    /// </summary>
    public static class ModelLoader
    {
        /// <summary>
        /// The name of the model file.  A project has exactly one, recognized by this name, the way StyleCop recognizes
        /// <c>stylecop.json</c>.
        /// </summary>
        public const string FileName = "bellerophon.json";

        /// <summary>
        /// Loads a model from its text.
        /// </summary>
        /// <param name="path">The file path, used in diagnostics.</param>
        /// <param name="text">The file's text.</param>
        /// <returns>The semantic model and diagnostics.</returns>
        public static ModelLoadResult Load(string path, string text)
        {
            JsonNode root;
            try
            {
                root = new JsonParser(path, text).Parse();
            }
            catch (JsonSyntaxException exception)
            {
                var diagnostic = new ModelDiagnostic(DiagnosticIds.SyntaxError, ModelSeverity.Error, exception.Message, exception.Location);
                return new ModelLoadResult(path, null, new EquatableArray<ModelDiagnostic>(new[] { diagnostic }));
            }

            return new ModelBuilder(path).Build(root);
        }
    }
}
