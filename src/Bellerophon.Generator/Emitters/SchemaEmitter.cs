// <copyright file="SchemaEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Linq;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits the Schema role: an Entity Framework Core model of the database, used only for the schema and its migrations.
    /// The ledger never reads or writes through it; the Primary role's generated SQL does that, with the same column types.
    /// </summary>
    internal static class SchemaEmitter
    {
        private const string Ef = "global::Microsoft.EntityFrameworkCore";

        /// <summary>
        /// Emits one table's entity: its columns, keys included, plus the tenant and the version.
        /// </summary>
        /// <param name="shape">The model in the Schema role.</param>
        /// <param name="table">The table.</param>
        /// <returns>The source text.</returns>
        public static string EmitEntity(ModelShape shape, TableModel table)
        {
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"A stored {table.Name} row, for the schema and its migrations only.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {Names.Identifier(table.Name)}");
            writer.Open();
            writer.Summary("Gets or sets the tenant: the first column of every primary key.");
            writer.Line("public global::System.Guid TenantId { get; set; }");
            foreach (var column in shape.DtoColumns(table))
            {
                writer.Line();
                writer.Summary($"Gets or sets the {column.Name}.");
                writer.Line($"public {shape.Type(column)} {Names.Identifier(column.Name)} {{ get; set; }}{shape.Initializer(column)}");
            }

            writer.Line();
            writer.Summary("Gets or sets the row's version: the sequence of its last change.");
            writer.Line("public long RowVersion { get; set; }");
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Emits the context: every durable table with its primary key and exact column types, and the two tables the
        /// generated database code keeps in the <c>bellerophon</c> schema: the tenants' watermarks.
        /// </summary>
        /// <param name="shape">The model in the Schema role.</param>
        /// <returns>The source text.</returns>
        public static string EmitContext(ModelShape shape)
        {
            var context = $"{shape.Name}DbContext";
            var watermark = $"{shape.Name}Watermark";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Line("// Entity Framework's relational and SQL Server configuration methods are extension methods in this namespace.");
            writer.Line("using global::Microsoft.EntityFrameworkCore;");
            writer.Line();

            writer.Summary("A tenant's durable watermark: every change up to this sequence is in the database.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed class {watermark}");
            writer.Open();
            writer.Summary("Gets or sets the tenant.");
            writer.Line("public global::System.Guid TenantId { get; set; }");
            writer.Line();
            writer.Summary("Gets or sets the tenant's durable watermark.");
            writer.Line("public long DurableSequence { get; set; }");
            writer.Close();
            writer.Line();

            writer.Summary($"The {shape.Name} model's database schema, for migrations only: tables shared by every tenant, primary keys only (no unique constraints, foreign keys, or secondary indexes), and the same column types the generated SQL writes.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public partial class {context} : {Ef}.DbContext");
            writer.Open();
            writer.Summary($"Initializes a new instance of the {context} class.");
            writer.Line("/// <param name=\"options\">The options, naming the database.</param>");
            writer.Line($"public {context}({Ef}.DbContextOptions<{context}> options)");
            writer.Line("    : base(options)");
            writer.Open();
            writer.Close();
            writer.Line();
            writer.Line("/// <inheritdoc/>");
            writer.Line($"protected override void OnModelCreating({Ef}.ModelBuilder modelBuilder)");
            writer.Open();
            writer.Line("global::System.ArgumentNullException.ThrowIfNull(modelBuilder);");
            foreach (var table in shape.TablesInDependencyOrder())
            {
                EmitTable(shape, table, writer);
            }

            writer.Line($"modelBuilder.Entity<{watermark}>(entity =>");
            writer.Open();
            writer.Line("entity.ToTable(\"Watermark\", \"bellerophon\");");
            writer.Line("entity.HasKey(row => row.TenantId).HasName(\"PK_Watermark\");");
            writer.Line("entity.Property(row => row.TenantId).HasColumnType(\"uniqueidentifier\").ValueGeneratedNever();");
            writer.Line("entity.Property(row => row.DurableSequence).HasColumnType(\"bigint\");");
            writer.Close(");");
            writer.Line();
            writer.Line("this.OnModelCreatingPartial(modelBuilder);");
            writer.Close();
            writer.Line();
            writer.Summary("Adds to the model, for example to set the default schema; the generated tables must keep their primary keys only.");
            writer.Line("/// <param name=\"modelBuilder\">The model builder.</param>");
            writer.Line($"partial void OnModelCreatingPartial({Ef}.ModelBuilder modelBuilder);");
            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        private static void EmitTable(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var entity = Names.Identifier(table.Name);
            var key = string.Join(", ", new[] { "row.TenantId" }.Concat(table.Key.Select(name => "row." + Names.Identifier(name))));
            writer.Line($"modelBuilder.Entity<{entity}>(entity =>");
            writer.Open();
            writer.Line($"entity.ToTable(\"{table.Name}\", \"dbo\");");
            writer.Line($"entity.HasKey(row => new {{ {key} }}).HasName(\"PK_{table.Name}\");");
            writer.Line("entity.Property(row => row.TenantId).HasColumnType(\"uniqueidentifier\").ValueGeneratedNever();");
            foreach (var column in shape.DtoColumns(table))
            {
                var type = SqlColumnType.For(column, shape.Resolved)!;
                var line = $"entity.Property(row => row.{Names.Identifier(column.Name)}).HasColumnType(\"{type.Ddl}\")";
                if (column.MaxLength is { } length && (column.ClrType == "System.String" || column.ClrType == "System.Byte[]"))
                {
                    line += $".HasMaxLength({length})";
                }

                if (column.ClrType == "System.Decimal" && column.Precision is { } precision)
                {
                    line += $".HasPrecision({precision}, {column.Scale ?? 0})";
                }

                line += column.IsNullable ? ".IsRequired(false)" : ".IsRequired()";
                if (table.Key.Contains(column.Name))
                {
                    line += ".ValueGeneratedNever()";
                }

                writer.Line(line + ";");
            }

            writer.Line("entity.Property(row => row.RowVersion).HasColumnType(\"bigint\");");
            writer.Close(");");
            writer.Line();
        }

        /// <summary>
        /// Gets the files of the Schema role.
        /// </summary>
        /// <param name="shape">The model in the Schema role.</param>
        /// <returns>The files.</returns>
        public static IEnumerable<GeneratedFile> Files(ModelShape shape)
        {
            var prefix = $"{shape.Name}.{shape.Role.Name}";
            yield return new GeneratedFile($"{prefix}.{shape.Name}DbContext.g.cs", EmitContext(shape));
            foreach (var table in shape.Tables)
            {
                yield return new GeneratedFile($"{prefix}.{table.Name}.g.cs", EmitEntity(shape, table));
            }
        }
    }
}
