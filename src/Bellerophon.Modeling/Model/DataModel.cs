// <copyright file="DataModel.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling.Model
{
    /// <summary>
    /// The semantic model: every datatype, table, column, key, reference, and policy, with defaults applied, implicit
    /// columns added, names resolved, and everything in canonical (alphabetical) order.
    /// </summary>
    /// <param name="Name">The model's name; also the name of the generated root class, for example <c>Ledger</c>.</param>
    /// <param name="Namespace">The root namespace of the generated code.</param>
    /// <param name="Version">The contract version, <c>major.minor</c>: major for breaking changes, minor for additive ones.</param>
    /// <param name="Access">The default access, or <c>null</c> if the model declares none.</param>
    /// <param name="DataTypes">The datatype dictionary.</param>
    /// <param name="Tables">The tables.</param>
    /// <remarks>
    /// Every type in the semantic model is an immutable, value-equatable record, so the incremental generator can cache it.
    /// Emitters read only the semantic model, never the file format.
    /// </remarks>
    public sealed record DataModel(string Name, string Namespace, string Version, AccessModel? Access, EquatableArray<DataTypeModel> DataTypes, EquatableArray<TableModel> Tables)
    {
        /// <summary>
        /// Gets the major version, which appears in the API's routes.
        /// </summary>
        public int MajorVersion => int.TryParse(this.Version.Split('.')[0], out var major) ? major : 0;

        /// <summary>
        /// Finds a table by name.
        /// </summary>
        /// <param name="name">The table name.</param>
        /// <returns>The table, or <c>null</c>.</returns>
        public TableModel? FindTable(string name)
        {
            foreach (var table in this.Tables)
            {
                if (table.Name == name)
                {
                    return table;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The OAuth scopes that authorize reads and writes.  A caller is authorized if its token carries any listed name, as a
    /// delegated scope (the <c>scp</c> claim) or as an application permission (the <c>roles</c> claim).
    /// </summary>
    /// <param name="Read">The scopes that authorize reads.</param>
    /// <param name="Write">The scopes that authorize writes.</param>
    public sealed record AccessModel(EquatableArray<string> Read, EquatableArray<string> Write);

    /// <summary>
    /// An entry in the datatype dictionary: a CLR type, optionally constrained (a domain).
    /// </summary>
    /// <param name="Name">The entry name that columns refer to.</param>
    /// <param name="ClrType">The full CLR type name, for example <c>System.Guid</c>.</param>
    /// <param name="MaxLength">The maximum length of a string or binary value.</param>
    /// <param name="Precision">The total number of digits of a decimal.</param>
    /// <param name="Scale">The number of digits after the decimal point.</param>
    /// <param name="Location">Where the entry is declared, so the generator can report an unresolved CLR type there.</param>
    public sealed record DataTypeModel(string Name, string ClrType, int? MaxLength, int? Precision, int? Scale, SourceLocation Location);

    /// <summary>
    /// A table.  Every table belongs to a tenant: it receives an implicit, immutable <c>TenantId</c>, its keys are unique
    /// within the tenant, and the tenant is part of every reference.
    /// </summary>
    /// <param name="Name">The table name; also the generated row type's name.</param>
    /// <param name="Key">The key columns, in index order.</param>
    /// <param name="Columns">All columns: declared columns alphabetically, then the implicit columns.</param>
    /// <param name="References">References to parent tables.</param>
    /// <param name="Uniques">Unique constraints other than the key.</param>
    /// <param name="Access">The effective access (the table's own, or the model default).</param>
    /// <param name="IsSynced">Whether the table is included in the client data set.</param>
    /// <param name="Principal">The identity columns; set only on the built-in User table.</param>
    /// <param name="Persistence">Whether the table survives a restart.</param>
    /// <param name="WriteMode">Which changes the table accepts.</param>
    /// <param name="Concurrency">How concurrent access is managed.</param>
    /// <param name="Description">A description used for the generated documentation.</param>
    /// <param name="IsBuiltIn">Whether the table is infrastructure supplied by Bellerophon rather than declared in the model.</param>
    /// <param name="Plural">The plural of the table's name: regular English, or the model's <c>"plural"</c> override.</param>
    /// <param name="HasFacts">Whether the table's rows enter the rules engine's working memory as facts (the model's <c>"facts"</c>, default true).</param>
    public sealed record TableModel(
        string Name,
        EquatableArray<string> Key,
        EquatableArray<ColumnModel> Columns,
        EquatableArray<ReferenceModel> References,
        EquatableArray<UniqueModel> Uniques,
        AccessModel Access,
        bool IsSynced,
        PrincipalModel? Principal,
        Persistence Persistence,
        WriteMode WriteMode,
        ConcurrencyMode Concurrency,
        string? Description,
        bool IsBuiltIn,
        string Plural,
        bool HasFacts)
    {
        /// <summary>
        /// Finds a column by name.
        /// </summary>
        /// <param name="name">The column name.</param>
        /// <returns>The column, or <c>null</c>.</returns>
        public ColumnModel? FindColumn(string name)
        {
            foreach (var column in this.Columns)
            {
                if (column.Name == name)
                {
                    return column;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// A column.
    /// </summary>
    /// <param name="Name">The column name.</param>
    /// <param name="DataType">The datatype entry the column names; empty for implicit columns.</param>
    /// <param name="ClrType">The full CLR type name.</param>
    /// <param name="IsNullable">Whether the column accepts null (a trailing <c>?</c> on its type).</param>
    /// <param name="MaxLength">The effective maximum length: the column's own, else the datatype's.</param>
    /// <param name="Precision">The effective precision.</param>
    /// <param name="Scale">The effective scale.</param>
    /// <param name="DefaultValue">The default value as written, used to backfill new columns.</param>
    /// <param name="IsImmutable">Whether the value is fixed after insert.  Key columns and <c>TenantId</c> always are.</param>
    /// <param name="IsImplicit">Whether the generator added the column (<c>TenantId</c>, <c>RowVersion</c>).</param>
    /// <param name="Description">A description used for the generated documentation.</param>
    public sealed record ColumnModel(
        string Name,
        string DataType,
        string ClrType,
        bool IsNullable,
        int? MaxLength,
        int? Precision,
        int? Scale,
        string? DefaultValue,
        bool IsImmutable,
        bool IsImplicit,
        string? Description);

    /// <summary>
    /// A reference from a child table to a parent table.  Nullable columns make the reference optional.
    /// </summary>
    /// <param name="Name">The reference name; also the generated parent property's name.</param>
    /// <param name="Table">The parent table.</param>
    /// <param name="Columns">The child columns that match the parent's key.</param>
    /// <param name="OnDelete">What happens to children when the parent is deleted.</param>
    public sealed record ReferenceModel(string Name, string Table, EquatableArray<string> Columns, DeleteRule OnDelete);

    /// <summary>
    /// A unique constraint other than the key; uniqueness is within the tenant.
    /// </summary>
    /// <param name="Name">The constraint name.</param>
    /// <param name="Columns">The constrained columns.</param>
    /// <param name="IsIdempotent">Whether a duplicate insert is a no-op that returns the existing row.</param>
    public sealed record UniqueModel(string Name, EquatableArray<string> Columns, bool IsIdempotent);

    /// <summary>
    /// Names the columns of the built-in User table that bind each user to an identity.  They are ordinary columns: required,
    /// unique together within the tenant, returned and synced like any other.  Authorization matches the caller's issuer
    /// and subject against them; reading them requires the User table's read scope.
    /// </summary>
    /// <param name="Issuer">The column holding the identity provider's issuer.</param>
    /// <param name="Subject">The column holding the identity's subject (with Entra, the <c>oid</c> claim).</param>
    public sealed record PrincipalModel(string Issuer, string Subject);
}
