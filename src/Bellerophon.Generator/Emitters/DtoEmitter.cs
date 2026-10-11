// <copyright file="DtoEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits a table's data transfer object, <c>{Table}Dto</c>, in the model's root namespace: a flat record of the
    /// table's columns, used in both directions over the API and in JSON scripts.  The tenant comes from the route.
    /// </summary>
    internal static class DtoEmitter
    {
        /// <summary>
        /// Emits the data transfer object.
        /// </summary>
        /// <param name="shape">The model in its role.</param>
        /// <param name="table">The table.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape, TableModel table)
        {
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.Namespace}");
            writer.Open();
            writer.Summary($"A {table.Name} as it travels over the API and appears in JSON scripts: the same structure in both directions, keys included.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {table.Name}Dto");
            writer.Open();
            foreach (var column in shape.DtoColumns(table))
            {
                writer.Summary($"Gets or sets the {column.Name}.");
                writer.Line($"public {shape.Type(column)} {Names.Identifier(column.Name)} {{ get; set; }}{shape.Initializer(column)}");
                writer.Line();
            }

            writer.Summary("Gets or sets the row's version: its current version when read. Send it back unchanged with an update or delete; it is ignored on insert.");
            writer.Line("public long RowVersion { get; set; }");
            writer.Close();
            writer.Close();
            return writer.ToString();
        }
    }
}
