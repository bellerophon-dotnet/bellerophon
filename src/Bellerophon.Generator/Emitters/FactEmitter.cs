// <copyright file="FactEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits a table's fact: an immutable snapshot of a row for a rules engine's working memory.
    /// </summary>
    /// <remarks>
    /// A Rete engine finds a fact again by equality when it is updated or retracted, so a fact is equal to any other fact
    /// with the same key, whatever its values, and it never changes once created.  It is <c>partial</c>, so rules code can
    /// add computed properties without touching the wire format; denormalized facts belong in the rules, built from these.
    /// </remarks>
    internal static class FactEmitter
    {
        /// <summary>
        /// Emits the fact.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <param name="table">The table.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape, TableModel table)
        {
            var fact = $"{table.Name}Fact";
            var isDurable = ModelShape.IsDurable(table);
            var columns = shape.DtoColumns(table).ToList();
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"A {table.Name} in a rules engine's working memory: an immutable snapshot of the row, equal to any other {fact} with the same key.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {fact} : global::System.IEquatable<{fact}>");
            writer.Open();
            if (isDurable)
            {
                writer.Summary($"Initializes a new instance of the {fact} class from the row's data transfer object.");
                writer.Line("/// <param name=\"dto\">The row's data transfer object.</param>");
                writer.Line($"public {fact}({shape.DtoType(table)} dto)");
                writer.Open();
                writer.Line("global::System.ArgumentNullException.ThrowIfNull(dto);");
                foreach (var column in columns)
                {
                    writer.Line($"this.{Names.Identifier(column.Name)} = dto.{Names.Identifier(column.Name)};");
                }

                writer.Line("this.RowVersion = dto.RowVersion;");
                writer.Close();
            }
            else
            {
                writer.Summary($"Initializes a new instance of the {fact} class from the latest record.");
                writer.Line("/// <param name=\"row\">The latest record.</param>");
                writer.Line($"public {fact}({shape.RowType(table)} row)");
                writer.Open();
                writer.Line("global::System.ArgumentNullException.ThrowIfNull(row);");
                foreach (var column in columns)
                {
                    writer.Line($"this.{Names.Identifier(column.Name)} = row.{Names.Identifier(column.Name)};");
                }

                writer.Close();
            }

            foreach (var column in columns)
            {
                writer.Line();
                writer.Summary($"Gets the {column.Name}.{(shape.IsKeyColumn(table, column.Name) ? " Part of the key, which decides equality." : string.Empty)}");
                writer.Line($"public {shape.Type(column)} {Names.Identifier(column.Name)} {{ get; }}");
            }

            if (isDurable)
            {
                writer.Line();
                writer.Summary("Gets the row's version; a rule that changes the row sends it back. Not part of equality.");
                writer.Line("public long RowVersion { get; }");
            }

            var comparisons = table.Key.Select(name =>
            {
                var column = table.FindColumn(name)!;
                return $"global::System.Collections.Generic.EqualityComparer<{shape.Type(column)}>.Default.Equals(this.{Names.Identifier(name)}, other.{Names.Identifier(name)})";
            });
            writer.Line();
            writer.Summary("Determines whether another fact is for the same row: whether the keys are equal.");
            writer.Line("/// <param name=\"other\">The other fact.</param>");
            writer.Line("/// <returns>True if both facts have the same key.</returns>");
            writer.Line($"public bool Equals({fact}? other)");
            writer.Open();
            writer.Line($"return other is not null && {string.Join("\n                && ", comparisons)};");
            writer.Close();
            writer.Line();
            writer.Line("/// <inheritdoc/>");
            writer.Line($"public override bool Equals(object? obj) => this.Equals(obj as {fact});");
            writer.Line();
            writer.Line("/// <inheritdoc/>");
            writer.Line("public override int GetHashCode()");
            writer.Open();
            writer.Line("var hash = default(global::System.HashCode);");
            foreach (var name in table.Key)
            {
                writer.Line($"hash.Add(this.{Names.Identifier(name)});");
            }

            writer.Line("return hash.ToHashCode();");
            writer.Close();
            writer.Line();
            writer.Line("/// <inheritdoc/>");
            var keyText = string.Join(", ", table.Key.Select(name => $"{{this.{Names.Identifier(name)}}}"));
            writer.Line($"public override string ToString() => $\"{fact} ({keyText})\";");
            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
