// <copyright file="RowEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Linq;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits a table's row type: its columns, the properties that navigate to its parents and children, and its mapping
    /// to and from the wire types.  Rows live in the in-memory graph and never go over the wire themselves.
    /// </summary>
    internal static class RowEmitter
    {
        /// <summary>
        /// Emits a row type.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <param name="table">The table.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape, TableModel table)
        {
            var row = shape.RowType(table);
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary(table.Description ?? $"A row of the {table.Name} table.");
            writer.Line($"/// <remarks>Key: {string.Join(", ", table.Key)}, unique within the tenant. {Remarks(shape, table)}</remarks>");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public partial class {row}");
            writer.Open();

            foreach (var column in table.Columns)
            {
                writer.Summary(ColumnSummary(shape, table, column));
                writer.Line($"public {shape.Type(column)} {Names.Identifier(column.Name)} {Accessors(shape, table, column)}{shape.Initializer(column)}");
                writer.Line();
            }

            foreach (var reference in table.References)
            {
                var parent = shape.Table(reference.Table);
                var optional = shape.IsOptional(table, reference);
                writer.Summary(optional
                    ? $"Gets the parent {parent.Name} through {reference.Name}, or null when the reference is not set."
                    : $"Gets the parent {parent.Name} through {reference.Name}.");
                writer.Line(optional
                    ? $"public {shape.RowType(parent)}? {Names.Identifier(reference.Name)} {{ get; internal set; }}"
                    : $"public {shape.RowType(parent)} {Names.Identifier(reference.Name)} {{ get; internal set; }} = default!;");
                writer.Line();
            }

            foreach (var (child, reference) in shape.ChildrenOf(table))
            {
                var member = Navigation.ChildMemberName(child, reference);
                if (Navigation.IsOneToOne(child, reference))
                {
                    writer.Summary($"Gets the {child.Name} that extends this row through {child.Name}.{reference.Name}, or null.");
                    writer.Line($"public {shape.RowType(child)}? {Names.Identifier(member)} {{ get; internal set; }}");
                }
                else
                {
                    var field = shape.ChildField(child, reference);
                    var childRow = shape.RowType(child);
                    writer.Line("// Allocated with the first child, so rows that never have children carry no set.");
                    writer.Line($"private global::System.Collections.Generic.HashSet<{childRow}>? {field};");
                    writer.Line();
                    writer.Summary($"Gets the {child.Plural} whose {reference.Name} is this row. Read it while holding the partition's lock.");
                    writer.Line($"public global::System.Collections.Generic.IReadOnlyCollection<{childRow}> {Names.Identifier(member)} => (global::System.Collections.Generic.IReadOnlyCollection<{childRow}>?)this.{field} ?? global::System.Array.Empty<{childRow}>();");
                    writer.Line();
                    writer.Summary($"Adds a child to {member}. Only the partition calls it, inside a transaction.");
                    writer.Line($"internal void AddTo{member}({childRow} child) => (this.{field} ??= new global::System.Collections.Generic.HashSet<{childRow}>(global::System.Collections.Generic.ReferenceEqualityComparer.Instance)).Add(child);");
                    writer.Line();
                    writer.Summary($"Removes a child from {member}. Only the partition calls it, inside a transaction.");
                    writer.Line($"internal void RemoveFrom{member}({childRow} child) => this.{field}?.Remove(child);");
                }

                writer.Line();
            }

            if (table.Concurrency != ConcurrencyMode.LatestValue)
            {
                EmitCloneValues(shape, table, writer);
                EmitHasSameValues(shape, table, writer);
            }

            if (!ModelShape.IsDurable(table))
            {
                // Ephemeral tables never go over the wire, so they have no wire types to map to.
            }
            else if (shape.IsPrimary)
            {
                EmitServerMapping(shape, table, writer);
                writer.Line();
                EmitClientMapping(shape, table, writer);
            }
            else
            {
                EmitClientMapping(shape, table, writer);
            }

            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the shallow copy of a row's values that an update keeps for undo.  Navigation is not copied: undo relinks
        /// the row from its restored columns.
        /// </summary>
        private static void EmitCloneValues(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            writer.Summary("Copies this row's values, without its navigation, for undo.");
            writer.Line($"internal {row} CloneValues()");
            writer.Open();
            writer.Line($"return new {row}");
            writer.Open();
            foreach (var column in table.Columns)
            {
                writer.Line($"{Names.Identifier(column.Name)} = this.{Names.Identifier(column.Name)},");
            }

            writer.Close();
            writer.Line(";");
            writer.Close();
            writer.Line();
        }

        /// <summary>
        /// Emits the comparison a retried insert uses: does a stored row hold exactly the values being inserted?
        /// </summary>
        private static void EmitHasSameValues(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            var comparisons = shape.DtoColumns(table)
                .Select(column => $"global::System.Collections.Generic.EqualityComparer<{shape.Type(column)}>.Default.Equals(this.{Names.Identifier(column.Name)}, other.{Names.Identifier(column.Name)})")
                .ToList();
            writer.Summary("Determines whether another row holds exactly this row's values (versions and links aside).");
            writer.Line($"internal bool HasSameValues({row} other)");
            writer.Open();
            writer.Line("return " + (comparisons.Count == 0 ? "true" : string.Join("\n                && ", comparisons)) + ";");
            writer.Close();
            writer.Line();
        }

        private static string Remarks(ModelShape shape, TableModel table)
        {
            if (!shape.IsPrimary)
            {
                return "Maintained by the partition from the server's responses.";
            }

            if (table.Concurrency == ConcurrencyMode.LatestValue)
            {
                return "An immutable record in a lock-free store: publish a new one to replace it.";
            }

            return table.WriteMode == WriteMode.AppendOnly
                ? "Append-only: inserted once, never changed or deleted."
                : "Change a stored row only through its partition's Update method.";
        }

        private static string ColumnSummary(ModelShape shape, TableModel table, ColumnModel column)
        {
            var detail = column.Description ?? string.Empty;
            if (column.Name == "TenantId" || column.Name == "RowVersion")
            {
                detail = column.Description ?? string.Empty;
            }
            else if (shape.IsKeyColumn(table, column.Name))
            {
                detail += " Part of the key; fixed after insert.";
            }
            else if (column.IsImmutable)
            {
                detail += " Fixed after insert.";
            }

            return $"Gets the {column.Name}. {detail.Trim()}".Trim();
        }

        private static string Accessors(ModelShape shape, TableModel table, ColumnModel column)
        {
            if (!shape.IsPrimary || column.IsImplicit || column.Name == "TenantId" || column.Name == "RowVersion")
            {
                return "{ get; internal set; }";
            }

            // Append-only rows never change after insert, and a latest-value row is an immutable record replaced whole.
            var fixedAfterInsert = column.IsImmutable || table.WriteMode == WriteMode.AppendOnly || table.Concurrency == ConcurrencyMode.LatestValue;
            return fixedAfterInsert ? "{ get; init; }" : "{ get; set; }";
        }

        private static void EmitServerMapping(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            writer.Summary("Copies this row to its data transfer object: every column, keys included, and the version.");
            writer.Line("/// <returns>The data transfer object.</returns>");
            writer.Line($"public {shape.DtoType(table)} ToDto()");
            writer.Open();
            writer.Line($"return new {shape.DtoType(table)}");
            writer.Open();
            foreach (var column in shape.DtoColumns(table))
            {
                writer.Line($"{Names.Identifier(column.Name)} = this.{Names.Identifier(column.Name)},");
            }

            writer.Line("RowVersion = this.RowVersion,");
            writer.Close();
            writer.Line(";");
            writer.Close();
            writer.Line();

            writer.Summary("Builds a row to insert from a data transfer object, keys included.");
            writer.Line($"internal static {row} FromInsert({shape.DtoType(table)} dto)");
            writer.Open();
            writer.Line($"return new {row}");
            writer.Open();
            foreach (var column in shape.DtoColumns(table))
            {
                writer.Line($"{Names.Identifier(column.Name)} = dto.{Names.Identifier(column.Name)},");
            }

            writer.Close();
            writer.Line(";");
            writer.Close();
            writer.Line();

            writer.Summary("Builds the changes for an update: the key from the route, the values and expected version from the data transfer object.");
            writer.Line($"internal static {row} FromUpdate({shape.DtoType(table)} dto, {shape.KeyParameters(table)})");
            writer.Open();
            writer.Line($"return new {row}");
            writer.Open();
            foreach (var name in table.Key)
            {
                writer.Line($"{Names.Identifier(name)} = {Names.Parameter(name)},");
            }

            foreach (var column in shape.DtoColumns(table).Where(column => !shape.IsKeyColumn(table, column.Name)))
            {
                writer.Line($"{Names.Identifier(column.Name)} = dto.{Names.Identifier(column.Name)},");
            }

            writer.Line("RowVersion = dto.RowVersion,");
            writer.Close();
            writer.Line(";");
            writer.Close();
        }

        private static void EmitClientMapping(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var row = shape.RowType(table);
            writer.Summary("Builds a row from a data transfer object, keeping its version: what the server sends a replica, what recovery loads, or the changes for an update made outside the generated code (read the row, change its data transfer object, pass the result to Update).");
            writer.Line("/// <param name=\"dto\">The data transfer object.</param>");
            writer.Line("/// <returns>A new row, not stored anywhere.</returns>");
            writer.Line($"public static {row} FromDto({shape.DtoType(table)} dto)");
            writer.Open();
            writer.Line($"return new {row}");
            writer.Open();
            foreach (var column in shape.DtoColumns(table))
            {
                writer.Line($"{Names.Identifier(column.Name)} = dto.{Names.Identifier(column.Name)},");
            }

            writer.Line("RowVersion = dto.RowVersion,");
            writer.Close();
            writer.Line(";");
            writer.Close();
        }
    }
}
