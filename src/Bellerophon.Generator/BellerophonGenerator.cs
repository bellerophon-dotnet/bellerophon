// <copyright file="BellerophonGenerator.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Threading;
    using Bellerophon.Generator.Emitters;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Diagnostics;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.Diagnostics;

    /// <summary>
    /// Generates code from a project's <c>bellerophon.json</c> model file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pipeline: the project's one <c>bellerophon.json</c> is parsed into the semantic model, and format and semantic problems
    /// are reported in the model file; a second model file is an error.  Valid models have their datatypes resolved against the compilation, the only step that depends on it, and
    /// unresolved types are reported at their datatype entries.  Each resolved model then produces every file for its role:
    /// the root class, the partition, rows and wire types, and, for the server, stores, controllers, policies, and wiring.
    /// </para>
    /// <para>
    /// Every value passed between steps is value-equatable, so typing in C# files never re-parses the model, and the
    /// compiler reuses every generated file whose text is unchanged.
    /// </para>
    /// </remarks>
    [Generator(LanguageNames.CSharp)]
    public sealed class BellerophonGenerator : IIncrementalGenerator
    {
        /// <summary>
        /// The build property that selects the role.
        /// </summary>
        private const string RoleProperty = "build_property.BellerophonRole";

        /// <summary>
        /// The oldest C# version the generated code supports: 13, the first in which <c>lock</c> on a
        /// <see cref="System.Threading.Lock"/> takes that lock; an older compiler takes the object's monitor instead.  Compared
        /// as the numeric value of <c>LanguageVersion</c> (13.0 is 1300), so the generator, built against an older Roslyn, needs
        /// no newer enum member.
        /// </summary>
        private const int MinimumLanguageVersion = 1300;

        /// <inheritdoc/>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var role = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => ReadRole(provider));
            context.RegisterSourceOutput(role, static (output, setting) =>
            {
                if (!setting.IsValid)
                {
                    output.ReportDiagnostic(Diagnostic.Create(DiagnosticFactory.InvalidRole, Location.None, setting.Requested));
                }
            });

            var results = context.AdditionalTextsProvider
                .Where(static file => string.Equals(Path.GetFileName(file.Path), ModelLoader.FileName, StringComparison.OrdinalIgnoreCase))
                .Collect()
                .SelectMany(static (files, cancellationToken) => LoadModel(files, cancellationToken));

            context.RegisterSourceOutput(results, static (output, result) =>
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    output.ReportDiagnostic(DiagnosticFactory.Create(diagnostic));
                }
            });

            // The project's effective C# version.  The generated code depends on C# 13 semantics, so an older version is an
            // error, reported once for the model file, and nothing is generated.
            var languageVersion = context.ParseOptionsProvider.Select(static (options, _) => options is CSharpParseOptions csharp ? (int)csharp.LanguageVersion : 0);
            context.RegisterSourceOutput(results.Combine(languageVersion).Where(static pair => pair.Right < MinimumLanguageVersion), static (output, pair) =>
            {
                output.ReportDiagnostic(Diagnostic.Create(DiagnosticFactory.LanguageVersionTooLow, Location.None, FormatLanguageVersion(pair.Right)));
            });

            var resolved = results
                .Where(static result => result.Model is not null && !result.HasErrors)
                .Combine(context.CompilationProvider)
                .Select(static (pair, cancellationToken) => TypeResolver.Resolve(pair.Left.Path, pair.Left.Model!, pair.Right, cancellationToken));

            context.RegisterSourceOutput(resolved, static (output, model) =>
            {
                foreach (var diagnostic in model.Diagnostics)
                {
                    output.ReportDiagnostic(DiagnosticFactory.Create(diagnostic));
                }
            });

            // Database code is generated for the server when it references the SQL Server library, and for the schema role;
            // only then do column types without a SQL Server type matter, and the schema role needs Entity Framework.
            context.RegisterSourceOutput(resolved.Combine(role), static (output, pair) =>
            {
                var (model, setting) = pair;
                if (model.HasErrors || !GeneratedFiles.GeneratesDatabaseCode(model, setting))
                {
                    return;
                }

                foreach (var diagnostic in model.SqlProblems)
                {
                    output.ReportDiagnostic(DiagnosticFactory.Create(diagnostic));
                }

                if (setting.IsSchema && !model.HasEntityFramework)
                {
                    output.ReportDiagnostic(Diagnostic.Create(DiagnosticFactory.SchemaNeedsEntityFramework, Location.None));
                }
            });

            var files = resolved
                .Where(static model => !model.HasErrors)
                .Combine(role)
                .Combine(languageVersion)
                .Where(static pair => pair.Right >= MinimumLanguageVersion)
                .SelectMany(static (pair, _) => GeneratedFiles.For(pair.Left.Left, pair.Left.Right));
            context.RegisterSourceOutput(files, static (output, file) => output.AddSource(file.HintName, file.Text));
        }

        /// <summary>
        /// Loads the project's model file.  A project has exactly one; the same file included twice counts once.
        /// </summary>
        /// <param name="files">Every additional file named <c>bellerophon.json</c>.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>No result if the project has no model; otherwise one result, which is an error if there are several.</returns>
        private static IEnumerable<ModelLoadResult> LoadModel(ImmutableArray<AdditionalText> files, CancellationToken cancellationToken)
        {
            var distinct = new List<AdditionalText>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (paths.Add(file.Path))
                {
                    distinct.Add(file);
                }
            }

            if (distinct.Count == 0)
            {
                return Array.Empty<ModelLoadResult>();
            }

            if (distinct.Count > 1)
            {
                var diagnostics = new List<ModelDiagnostic>();
                for (var index = 1; index < distinct.Count; index++)
                {
                    var path = distinct[index].Path;
                    diagnostics.Add(new ModelDiagnostic(
                        DiagnosticIds.MultipleModelFiles,
                        ModelSeverity.Error,
                        $"A project has exactly one {ModelLoader.FileName}, but this one also includes '{distinct[0].Path}'.",
                        new SourceLocation(path, 0, 0, 0, 0, 0, 0)));
                }

                return new[] { new ModelLoadResult(distinct[0].Path, null, new EquatableArray<ModelDiagnostic>(diagnostics)) };
            }

            var model = distinct[0];
            return new[] { ModelLoader.Load(model.Path, model.GetText(cancellationToken)?.ToString() ?? string.Empty) };
        }

        /// <summary>
        /// Formats a numeric C# version the way a project file writes it.
        /// </summary>
        /// <param name="version">The numeric value of the language version, for example 1200.</param>
        /// <returns>The version, for example <c>12.0</c>.</returns>
        private static string FormatLanguageVersion(int version) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}.{1}", version / 100, version % 100);

        /// <summary>
        /// Reads the <c>BellerophonRole</c> build property.
        /// </summary>
        /// <param name="provider">The build options.</param>
        /// <returns>The role, defaulting to Primary.</returns>
        private static RoleSetting ReadRole(AnalyzerConfigOptionsProvider provider)
        {
            if (!provider.GlobalOptions.TryGetValue(RoleProperty, out var value) || string.IsNullOrWhiteSpace(value))
            {
                return new RoleSetting("Primary", string.Empty, true);
            }

            var trimmed = value.Trim();
            if (string.Equals(trimmed, "Primary", StringComparison.OrdinalIgnoreCase))
            {
                return new RoleSetting("Primary", trimmed, true);
            }

            if (string.Equals(trimmed, "Replica", StringComparison.OrdinalIgnoreCase))
            {
                return new RoleSetting("Replica", trimmed, true);
            }

            if (string.Equals(trimmed, "Schema", StringComparison.OrdinalIgnoreCase))
            {
                return new RoleSetting("Schema", trimmed, true);
            }

            return new RoleSetting("Primary", trimmed, false);
        }
    }
}
