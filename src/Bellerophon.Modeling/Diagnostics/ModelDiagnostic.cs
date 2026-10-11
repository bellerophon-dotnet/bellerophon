// <copyright file="ModelDiagnostic.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling.Diagnostics
{
    /// <summary>
    /// The severity of a model diagnostic.
    /// </summary>
    public enum ModelSeverity
    {
        /// <summary>
        /// Informational.
        /// </summary>
        Info,

        /// <summary>
        /// A problem that does not prevent generation.
        /// </summary>
        Warning,

        /// <summary>
        /// A problem that prevents generation.
        /// </summary>
        Error,
    }

    /// <summary>
    /// A problem found in a model file.
    /// </summary>
    /// <param name="Id">The diagnostic identifier, for example <c>BEL2003</c>.</param>
    /// <param name="Severity">The severity.</param>
    /// <param name="Message">The formatted message.</param>
    /// <param name="Location">Where in the model file the problem is.</param>
    /// <remarks>
    /// A plain, value-equatable record rather than a Roslyn <c>Diagnostic</c>, so it can flow through the incremental pipeline's cache.
    /// The generator converts it to a Roslyn diagnostic only in its output step.
    /// </remarks>
    public sealed record ModelDiagnostic(string Id, ModelSeverity Severity, string Message, SourceLocation Location)
    {
        /// <summary>
        /// Formats the diagnostic the way MSBuild formats compiler messages.
        /// </summary>
        /// <returns>For example <c>bellerophon.json(12,5): error BEL2003: Table 'Nodes' is not declared.</c></returns>
        public override string ToString() => $"{this.Location}: {this.Severity.ToString().ToLowerInvariant()} {this.Id}: {this.Message}";
    }
}
