// <copyright file="DiagnosticFactory.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator
{
    using Bellerophon.Modeling.Diagnostics;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Text;

    /// <summary>
    /// Converts model diagnostics into Roslyn diagnostics that point into the model file.
    /// </summary>
    internal static class DiagnosticFactory
    {
        /// <summary>
        /// The category shown in the IDE's Error List.
        /// </summary>
        private const string Category = "Bellerophon";

        /// <summary>
        /// Reported when the <c>BellerophonRole</c> build property is not recognized.
        /// </summary>
        public static readonly DiagnosticDescriptor InvalidRole = new DiagnosticDescriptor(
            "BEL0001",
            "Unknown generator role",
            "The BellerophonRole build property is '{0}'; expected Primary, Replica, or Schema. Generating the Primary role.",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// Reported when the project's C# version is older than the generated code requires.
        /// </summary>
        public static readonly DiagnosticDescriptor LanguageVersionTooLow = new DiagnosticDescriptor(
            "BEL0003",
            "C# 13 or later required",
            "The generated code requires C# 13 or later, but this project compiles as C# {0}. The ledger's isolation depends on C# 13's 'lock' on System.Threading.Lock: a compiler older than C# 13 takes the object's monitor instead, so readers would stop waiting for transactions. Build with the .NET 10 SDK and remove any LangVersion setting below 13.0. Nothing was generated.",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// Reported when a project in the schema role does not reference Entity Framework Core.
        /// </summary>
        public static readonly DiagnosticDescriptor SchemaNeedsEntityFramework = new DiagnosticDescriptor(
            "BEL0004",
            "The schema role needs Entity Framework Core",
            "The Schema role generates an Entity Framework Core model for the database schema and its migrations; reference Microsoft.EntityFrameworkCore.SqlServer. Nothing was generated.",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>
        /// Creates a Roslyn diagnostic from a model diagnostic.
        /// </summary>
        /// <param name="diagnostic">The model diagnostic.</param>
        /// <returns>The Roslyn diagnostic.</returns>
        public static Diagnostic Create(ModelDiagnostic diagnostic)
        {
            var severity = diagnostic.Severity switch
            {
                ModelSeverity.Error => DiagnosticSeverity.Error,
                ModelSeverity.Warning => DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Info,
            };

            var title = DiagnosticIds.Titles.TryGetValue(diagnostic.Id, out var text) ? text : Category;
            var descriptor = new DiagnosticDescriptor(diagnostic.Id, title, "{0}", Category, severity, isEnabledByDefault: true);
            var source = diagnostic.Location;
            var location = source.Path.Length == 0
                ? Location.None
                : Location.Create(
                    source.Path,
                    new TextSpan(source.Start, source.Length),
                    new LinePositionSpan(new LinePosition(source.StartLine, source.StartColumn), new LinePosition(source.EndLine, source.EndColumn)));
            return Diagnostic.Create(descriptor, location, diagnostic.Message);
        }
    }
}
