// <copyright file="EmitInputs.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Diagnostics;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// The generator role selected by the <c>BellerophonRole</c> build property.
    /// </summary>
    /// <param name="Name">The role: <c>Primary</c> (server), <c>Replica</c> (client mirror), or <c>Schema</c> (the database schema for Entity Framework migrations).</param>
    /// <param name="Requested">The property value as written, for diagnostics.</param>
    /// <param name="IsValid">Whether the property value was recognized.</param>
    internal sealed record RoleSetting(string Name, string Requested, bool IsValid)
    {
        /// <summary>
        /// Gets a value indicating whether this is the server role.
        /// </summary>
        public bool IsPrimary => this.Name == "Primary";

        /// <summary>
        /// Gets a value indicating whether this is the schema role, which generates only the Entity Framework model.
        /// </summary>
        public bool IsSchema => this.Name == "Schema";
    }

    /// <summary>
    /// What the compilation says about a CLR type named in the datatype dictionary.
    /// </summary>
    internal enum ClrTypeKind
    {
        /// <summary>Not found (or ambiguous) in the compilation.</summary>
        Missing,

        /// <summary>An enumeration.</summary>
        Enum,

        /// <summary>A value type other than an enumeration.</summary>
        Value,

        /// <summary>A reference type.</summary>
        Reference,
    }

    /// <summary>
    /// The resolved facts about one CLR type: value-equatable, so the compilation-dependent step stays cached.
    /// </summary>
    /// <param name="ClrType">The full CLR type name.</param>
    /// <param name="Kind">What kind of type it is.</param>
    /// <param name="EnumMembers">The member names of an enumeration; empty otherwise.</param>
    /// <param name="UnderlyingType">The full name of an enumeration's underlying type (<c>System.Int32</c> by default); empty otherwise.</param>
    internal sealed record TypeFact(string ClrType, ClrTypeKind Kind, EquatableArray<string> EnumMembers, string UnderlyingType);

    /// <summary>
    /// A model whose datatypes have been resolved against the compilation.
    /// </summary>
    /// <param name="Path">The model file's path.</param>
    /// <param name="Model">The semantic model.</param>
    /// <param name="Types">The facts about every CLR type the model uses.</param>
    /// <param name="Diagnostics">Problems found while resolving types.</param>
    /// <param name="HasRulesRuntime">Whether the compilation references the rules library, so the rules adapter is generated.</param>
    /// <param name="HasSqlServerRuntime">Whether the compilation references the SQL Server library, so the database writer and loader are generated.</param>
    /// <param name="HasEntityFramework">Whether the compilation references Entity Framework Core, which the schema role needs.</param>
    /// <param name="SqlProblems">Durable columns whose types have no SQL Server mapping; reported only when SQL code would be generated.</param>
    internal sealed record ResolvedModel(string Path, DataModel Model, EquatableArray<TypeFact> Types, EquatableArray<ModelDiagnostic> Diagnostics, bool HasRulesRuntime, bool HasSqlServerRuntime, bool HasEntityFramework, EquatableArray<ModelDiagnostic> SqlProblems)
    {
        /// <summary>
        /// Gets a value indicating whether any type failed to resolve.
        /// </summary>
        public bool HasErrors => this.Diagnostics.Count > 0;

        /// <summary>
        /// Gets the members of every enumeration the model uses, for the fingerprint.
        /// </summary>
        /// <returns>The member names of each enumeration, by type.</returns>
        public System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<string>> EnumMembers()
        {
            var members = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>>(System.StringComparer.Ordinal);
            foreach (var fact in this.Types)
            {
                if (fact.Kind == ClrTypeKind.Enum)
                {
                    members[fact.ClrType] = fact.EnumMembers;
                }
            }

            return members;
        }

        /// <summary>
        /// Gets the kind of a CLR type.
        /// </summary>
        /// <param name="clrType">The full CLR type name.</param>
        /// <returns>The kind; implicit columns use well-known value types.</returns>
        public ClrTypeKind KindOf(string clrType)
        {
            foreach (var fact in this.Types)
            {
                if (fact.ClrType == clrType)
                {
                    return fact.Kind;
                }
            }

            return clrType == "System.String" ? ClrTypeKind.Reference : ClrTypeKind.Value;
        }

        /// <summary>
        /// Gets the underlying type of an enumeration.
        /// </summary>
        /// <param name="clrType">The enumeration's full CLR type name.</param>
        /// <returns>The underlying type's full name, or empty if the type is not an enumeration.</returns>
        public string UnderlyingTypeOf(string clrType)
        {
            foreach (var fact in this.Types)
            {
                if (fact.ClrType == clrType)
                {
                    return fact.UnderlyingType;
                }
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// The input for the generated root class, named after the model.
    /// </summary>
    internal sealed record ModelInput(string Namespace, string Name, string Version, string Fingerprint, string Role, EquatableArray<string> Tables, EquatableArray<string> TablesInDependencyOrder);

    /// <summary>
    /// One key column as it appears in routes and method signatures.
    /// </summary>
    /// <param name="Column">The column (property) name.</param>
    /// <param name="Parameter">The parameter name.</param>
    /// <param name="Type">The C# type.</param>
    /// <param name="RouteConstraint">The route constraint, for example <c>:guid</c>, or empty.</param>
    internal sealed record KeyPart(string Column, string Parameter, string Type, string RouteConstraint);

    /// <summary>
    /// The input for one table's store interface and controller.
    /// </summary>
    internal sealed record ApiInput(string Namespace, string Model, int MajorVersion, string Table, string Route, EquatableArray<KeyPart> Key, EquatableArray<string> Read, EquatableArray<string> Write, bool IsAppendOnly);

    /// <summary>
    /// The scopes behind one table's read and write policies.
    /// </summary>
    internal sealed record PolicyPlan(string Table, EquatableArray<string> Read, EquatableArray<string> Write);

    /// <summary>
    /// The input for the access policies.
    /// </summary>
    internal sealed record AccessInput(string Namespace, string Model, EquatableArray<PolicyPlan> Policies);

    /// <summary>
    /// A generated source file: its hint name and text.  Comparing text lets the compiler reuse unchanged files.
    /// </summary>
    /// <param name="HintName">The hint name, which becomes the file name.</param>
    /// <param name="Text">The source text.</param>
    internal sealed record GeneratedFile(string HintName, string Text);

    /// <summary>
    /// The input for the Primary role's server-wide endpoints and wiring: the tenant picker and the contract check.
    /// </summary>
    internal sealed record ServerInput(string Namespace, string Model, int MajorVersion);
}
