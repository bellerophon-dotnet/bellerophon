// <copyright file="TypeResolver.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using Bellerophon.Generator.Emitters;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Diagnostics;
    using Bellerophon.Modeling.Model;
    using Microsoft.CodeAnalysis;

    /// <summary>
    /// Resolves the datatype dictionary's CLR types against the compilation.
    /// </summary>
    /// <remarks>
    /// This is the only step that depends on the compilation, which changes on every keystroke.  It extracts only small,
    /// value-equatable facts, so while the types stay the same nothing downstream runs again.
    /// </remarks>
    internal static class TypeResolver
    {
        /// <summary>
        /// The rules library's tenant interface: when the compilation can see it, the rules adapter is generated.
        /// </summary>
        private const string RulesTenantInterface = "Bellerophon.Rules.IRulesTenant";

        /// <summary>
        /// The SQL Server library's entry point: when the compilation can see it, the database writer and loader are generated.
        /// </summary>
        private const string SqlServerRuntime = "Bellerophon.SqlServer.SqlLedger";

        /// <summary>
        /// Entity Framework Core's context type, which the schema role's generated context derives from.
        /// </summary>
        private const string EntityFrameworkContext = "Microsoft.EntityFrameworkCore.DbContext";

        /// <summary>
        /// Resolves a model's types.
        /// </summary>
        /// <param name="path">The model file's path.</param>
        /// <param name="model">The model.</param>
        /// <param name="compilation">The compilation.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>The resolved model.</returns>
        public static ResolvedModel Resolve(string path, DataModel model, Compilation compilation, CancellationToken cancellationToken)
        {
            var facts = new List<TypeFact>();
            var diagnostics = new List<ModelDiagnostic>();
            foreach (var dataType in model.DataTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (kind, members, underlying) = KindOf(compilation, dataType.ClrType);
                if (kind == ClrTypeKind.Missing)
                {
                    diagnostics.Add(new ModelDiagnostic(
                        DiagnosticIds.UnresolvedClrType,
                        ModelSeverity.Error,
                        $"Type '{dataType.ClrType}' for datatype '{dataType.Name}' was not found in the compilation. Reference the assembly or package that declares it.",
                        dataType.Location));
                }

                facts.Add(new TypeFact(dataType.ClrType, kind, members, underlying));
            }

            // A project that references the rules library also gets the generated adapter between its model and the engine.
            var hasRulesRuntime = compilation.GetTypeByMetadataName(RulesTenantInterface) != null;
            var hasSqlServerRuntime = compilation.GetTypeByMetadataName(SqlServerRuntime) != null;
            var hasEntityFramework = compilation.GetTypeByMetadataName(EntityFrameworkContext) != null;
            var sqlProblems = SqlProblems(model, facts);
            return new ResolvedModel(path, model, new EquatableArray<TypeFact>(facts), new EquatableArray<ModelDiagnostic>(diagnostics), hasRulesRuntime, hasSqlServerRuntime, hasEntityFramework, new EquatableArray<ModelDiagnostic>(sqlProblems));
        }

        /// <summary>
        /// Finds the datatypes of durable columns that have no SQL Server type.  They are reported only when the project
        /// generates database code (the SQL Server library, or the schema role), at the datatype entry.
        /// </summary>
        private static List<ModelDiagnostic> SqlProblems(DataModel model, List<TypeFact> facts)
        {
            var problems = new List<ModelDiagnostic>();
            var reported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var table in model.Tables)
            {
                if (table.Persistence != Persistence.Durable)
                {
                    continue;
                }

                foreach (var column in table.Columns)
                {
                    var fact = facts.Find(candidate => candidate.ClrType == column.ClrType);
                    var kind = fact?.Kind ?? (column.ClrType == "System.String" ? ClrTypeKind.Reference : ClrTypeKind.Value);
                    if (kind == ClrTypeKind.Missing || SqlColumnType.For(column.ClrType, kind, fact?.UnderlyingType ?? string.Empty, column.MaxLength, column.Precision, column.Scale) != null)
                    {
                        continue;
                    }

                    var dataType = FindDataType(model, column.DataType);
                    if (dataType != null && reported.Add(dataType.Name))
                    {
                        problems.Add(new ModelDiagnostic(
                            DiagnosticIds.NoSqlServerType,
                            ModelSeverity.Error,
                            $"Datatype '{dataType.Name}' ({dataType.ClrType}) has no SQL Server type, so the durable column {table.Name}.{column.Name} cannot be stored. Supported: Guid, String, Decimal, Int16, Int32, Int64, Byte, Boolean, Double, Single, DateTime, DateTimeOffset, DateOnly, TimeOnly, Byte[], and enumerations over Byte, Int16, Int32, or Int64.",
                            dataType.Location));
                    }
                }
            }

            return problems;
        }

        private static DataTypeModel? FindDataType(DataModel model, string name)
        {
            foreach (var dataType in model.DataTypes)
            {
                if (dataType.Name == name)
                {
                    return dataType;
                }
            }

            return null;
        }

        /// <summary>
        /// Classifies one CLR type.
        /// </summary>
        private static (ClrTypeKind Kind, EquatableArray<string> Members, string UnderlyingType) KindOf(Compilation compilation, string clrType)
        {
            var isArray = clrType.EndsWith("[]", StringComparison.Ordinal);
            var name = isArray ? clrType.Substring(0, clrType.Length - 2) : clrType;
            var symbol = compilation.GetTypeByMetadataName(name);
            if (symbol == null)
            {
                return (ClrTypeKind.Missing, EquatableArray<string>.Empty, string.Empty);
            }

            if (isArray)
            {
                return (ClrTypeKind.Reference, EquatableArray<string>.Empty, string.Empty);
            }

            if (symbol.TypeKind == TypeKind.Enum)
            {
                var members = new List<string>();
                foreach (var member in symbol.GetMembers())
                {
                    if (member is IFieldSymbol field && field.HasConstantValue)
                    {
                        members.Add(field.Name);
                    }
                }

                var underlying = symbol.EnumUnderlyingType is { } type ? $"{type.ContainingNamespace.ToDisplayString()}.{type.MetadataName}" : "System.Int32";
                return (ClrTypeKind.Enum, new EquatableArray<string>(members), underlying);
            }

            return (symbol.IsValueType ? ClrTypeKind.Value : ClrTypeKind.Reference, EquatableArray<string>.Empty, string.Empty);
        }
    }
}
