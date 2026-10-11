// <copyright file="ModelShape.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Answers the questions every emitter asks about a resolved model in a given role: which tables the role includes,
    /// what C# types the columns and keys have, and which relations connect the tables.
    /// </summary>
    internal sealed class ModelShape
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ModelShape"/> class.
        /// </summary>
        /// <param name="resolved">The resolved model.</param>
        /// <param name="role">The role.</param>
        public ModelShape(ResolvedModel resolved, RoleSetting role)
        {
            this.Resolved = resolved;
            this.Role = role;
        }

        public ResolvedModel Resolved { get; }

        public RoleSetting Role { get; }

        public DataModel Model => this.Resolved.Model;

        public bool IsPrimary => this.Role.IsPrimary;

        public string Namespace => this.Model.Namespace;

        public string RoleNamespace => $"{this.Model.Namespace}.{this.Role.Name}";

        public string Name => this.Model.Name;

        public string RootType => $"global::{this.Model.Namespace}.{Names.Identifier(this.Model.Name)}";

        public string PartitionName => $"{this.Model.Name}Partition";

        /// <summary>
        /// Gets the tables this role includes: every table for the server; synced durable tables for the client.
        /// </summary>
        public IEnumerable<TableModel> Tables => this.Model.Tables.Where(this.Includes);

        public bool Includes(TableModel table) =>
            this.IsPrimary
            || (this.Role.IsSchema ? table.Persistence == Persistence.Durable : table.IsSynced && table.Persistence == Persistence.Durable);

        public static bool IsDurable(TableModel table) => table.Persistence == Persistence.Durable;

        public TableModel Table(string name) => this.Model.FindTable(name)!;

        public static string Field(string name) => Names.Identifier(char.ToLowerInvariant(name[0]) + name.Substring(1));

        public string Type(ColumnModel column) => Names.CSharpType(column.ClrType, column.IsNullable);

        public string NonNullableType(ColumnModel column) => Names.CSharpType(column.ClrType, false);

        public string Initializer(ColumnModel column) => Names.Initializer(column.ClrType, this.Resolved.KindOf(column.ClrType), column.IsNullable);

        /// <summary>
        /// Gets the C# type of a list of columns as one value: the column's type, or a tuple for several.
        /// </summary>
        public string CompositeType(TableModel table, IEnumerable<string> columns)
        {
            var types = columns.Select(name => this.NonNullableType(table.FindColumn(name)!)).ToList();
            return types.Count == 1 ? types[0] : $"({string.Join(", ", types)})";
        }

        /// <summary>
        /// Gets an expression combining columns of a row into one value, for example <c>(row.Scheme, row.Code)</c>.  Nullable
        /// columns are read through <c>.Value</c> (value types) or the null-forgiving operator (reference types); callers
        /// check <see cref="AllSet"/> first.
        /// </summary>
        public string CompositeValue(TableModel table, IEnumerable<string> columns, string row)
        {
            var parts = columns.Select(name =>
            {
                var column = table.FindColumn(name)!;
                var access = $"{row}.{Names.Identifier(name)}";
                if (!column.IsNullable)
                {
                    return access;
                }

                return this.Resolved.KindOf(column.ClrType) == ClrTypeKind.Reference ? access + "!" : access + ".Value";
            }).ToList();
            return parts.Count == 1 ? parts[0] : $"({string.Join(", ", parts)})";
        }

        /// <summary>
        /// Gets a condition that is true when none of the columns is null, or <c>true</c> when none can be.
        /// </summary>
        public string AllSet(TableModel table, IEnumerable<string> columns, string row)
        {
            var checks = columns
                .Select(name => table.FindColumn(name)!)
                .Where(column => column.IsNullable)
                .Select(column => this.Resolved.KindOf(column.ClrType) == ClrTypeKind.Reference
                    ? $"{row}.{Names.Identifier(column.Name)} != null"
                    : $"{row}.{Names.Identifier(column.Name)}.HasValue")
                .ToList();
            return checks.Count == 0 ? "true" : string.Join(" && ", checks);
        }

        public string KeyType(TableModel table) => this.CompositeType(table, table.Key);

        public string KeyValue(TableModel table, string row) => this.CompositeValue(table, table.Key, row);

        /// <summary>
        /// Gets the key as method parameters, for example <c>global::System.Guid accountId</c>.
        /// </summary>
        public string KeyParameters(TableModel table) =>
            string.Join(", ", table.Key.Select(name => $"{this.NonNullableType(table.FindColumn(name)!)} {Names.Parameter(name)}"));

        /// <summary>
        /// Gets the key built from those parameters, for example <c>accountId</c> or <c>(scheme, code)</c>.
        /// </summary>
        public string KeyArguments(TableModel table)
        {
            var names = table.Key.Select(Names.Parameter).ToList();
            return names.Count == 1 ? names[0] : $"({string.Join(", ", names)})";
        }

        /// <summary>
        /// Gets the key parameter names separated by commas, for passing them on, for example <c>scheme, code</c>.
        /// </summary>
        public string KeyParameterNames(TableModel table) => string.Join(", ", table.Key.Select(Names.Parameter));

        public bool IsKeyColumn(TableModel table, string column) => table.Key.Contains(column);

        /// <summary>
        /// Gets the declared (not implicit) columns.
        /// </summary>
        public IEnumerable<ColumnModel> Declared(TableModel table) => table.Columns.Where(column => !column.IsImplicit && column.Name != "TenantId" && column.Name != "RowVersion");

        /// <summary>
        /// Gets the columns an update may change: declared columns that are neither key columns nor immutable.
        /// </summary>
        public IEnumerable<ColumnModel> Mutable(TableModel table) =>
            this.Declared(table).Where(column => !this.IsKeyColumn(table, column.Name) && !column.IsImmutable);

        /// <summary>
        /// Gets the columns of the table's data transfer object: every declared column, keys included.  The same structure
        /// goes in both directions, so what is exported can be imported as it is.
        /// </summary>
        public IEnumerable<ColumnModel> DtoColumns(TableModel table) => this.Declared(table);

        /// <summary>
        /// Gets the fully qualified name of a table's data transfer object, for example
        /// <c>global::AtlasLedger.Model.AccountDto</c>.
        /// </summary>
        public string DtoType(TableModel table) => $"global::{this.Namespace}.{table.Name}Dto";

        /// <summary>
        /// Gets the relations in which a table is the parent, among the tables this role includes.
        /// </summary>
        public IEnumerable<(TableModel Child, ReferenceModel Reference)> ChildrenOf(TableModel parent) =>
            this.Tables.SelectMany(child => child.References.Where(reference => reference.Table == parent.Name).Select(reference => (child, reference)));

        public bool IsOptional(TableModel child, ReferenceModel reference) =>
            reference.Columns.Any(name => child.FindColumn(name)!.IsNullable);

        /// <summary>
        /// Gets whether a reference can change after insert: some of its columns are updatable.
        /// </summary>
        public bool IsMovable(TableModel child, ReferenceModel reference) =>
            reference.Columns.Any(name => !this.IsKeyColumn(child, name) && !child.FindColumn(name)!.IsImmutable);

        /// <summary>
        /// Gets the table's unique constraints, enforced in both roles.
        /// </summary>
        public IEnumerable<UniqueModel> Uniques(TableModel table) => table.Uniques;

        public string RowType(TableModel table) => Names.Identifier(table.Name);

        /// <summary>
        /// Gets the tables that become facts for a rules engine: on the server, durable and latest-value tables not marked
        /// <c>"facts": false</c>.
        /// </summary>
        public IEnumerable<TableModel> FactTables =>
            this.IsPrimary
                ? this.Tables.Where(table => table.HasFacts && (IsDurable(table) || table.Concurrency == ConcurrencyMode.LatestValue))
                : Enumerable.Empty<TableModel>();

        /// <summary>
        /// Orders the role's transactional tables so every parent comes before its children (references to a table itself
        /// are ignored).  Ties, and any tables in a cycle of references, fall back to alphabetical order.  A database
        /// writer applies changes in this order and deletions in reverse, so foreign keys always hold.
        /// </summary>
        public IReadOnlyList<TableModel> TablesInDependencyOrder()
        {
            var tables = this.Tables.Where(table => table.Concurrency != ConcurrencyMode.LatestValue).ToList();
            var names = new HashSet<string>(tables.Select(table => table.Name), System.StringComparer.Ordinal);
            var parents = tables.ToDictionary(
                table => table.Name,
                table => new HashSet<string>(table.References.Select(reference => reference.Table).Where(parent => parent != table.Name && names.Contains(parent)), System.StringComparer.Ordinal),
                System.StringComparer.Ordinal);
            var ordered = new List<TableModel>();
            var placed = new HashSet<string>(System.StringComparer.Ordinal);
            while (ordered.Count < tables.Count)
            {
                var next = tables.FirstOrDefault(table => !placed.Contains(table.Name) && parents[table.Name].All(placed.Contains))
                    ?? tables.First(table => !placed.Contains(table.Name));
                ordered.Add(next);
                placed.Add(next.Name);
            }

            return ordered;
        }

        /// <summary>
        /// Gets a row type's fully qualified name, for code where a member could hide it (inside a controller, <c>User</c>
        /// is the signed-in principal).
        /// </summary>
        public string QualifiedRowType(TableModel table) => $"global::{this.RoleNamespace}.{Names.Identifier(table.Name)}";

        public string TableField(TableModel table) => Field(table.Plural);

        public string UniqueField(TableModel table, UniqueModel unique) => Field(table.Plural + "By" + unique.Name);

        public string ChildField(TableModel child, ReferenceModel reference) => Field(Navigation.ChildMemberName(child, reference));

        public string HelperName(TableModel child, ReferenceModel reference) => child.Name + reference.Name;
    }
}
