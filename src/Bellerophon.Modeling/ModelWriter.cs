// <copyright file="ModelWriter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Writes a semantic model back to the file format in canonical form: properties in alphabetical order, defaults and
    /// implicit columns omitted, and short forms wherever possible.
    /// </summary>
    public static class ModelWriter
    {
        /// <summary>
        /// Writes a model.
        /// </summary>
        /// <param name="model">The model.</param>
        /// <returns>The JSON text, with a trailing newline.</returns>
        public static string Write(DataModel model)
        {
            var writer = new Writer();
            writer.Open("{");
            var sections = new List<System.Action>();
            if (model.Access != null)
            {
                sections.Add(() => writer.Item($"\"access\": {Access(model.Access)}"));
            }

            if (model.DataTypes.Count > 0)
            {
                sections.Add(() =>
                {
                    writer.Open("\"datatypes\": {");
                    for (var index = 0; index < model.DataTypes.Count; index++)
                    {
                        var dataType = model.DataTypes[index];
                        writer.Line($"{Quote(dataType.Name)}: {DataType(dataType)}{Comma(index, model.DataTypes.Count)}");
                    }

                    writer.Close("}");
                });
            }

            sections.Add(() => writer.Item($"\"name\": {Quote(model.Name)}"));
            sections.Add(() => writer.Item($"\"namespace\": {Quote(model.Namespace)}"));
            sections.Add(() =>
            {
                var declared = new List<TableModel>();
                foreach (var table in model.Tables)
                {
                    if (!table.IsBuiltIn)
                    {
                        declared.Add(table);
                    }
                }

                writer.Open("\"tables\": {");
                for (var index = 0; index < declared.Count; index++)
                {
                    WriteTable(writer, model, declared[index]);
                    writer.AppendComma(index < declared.Count - 1);
                }

                writer.Close("}");
            });
            sections.Add(() => writer.Item($"\"version\": {Quote(model.Version)}"));

            for (var index = 0; index < sections.Count; index++)
            {
                sections[index]();
                writer.AppendComma(index < sections.Count - 1);
            }

            writer.Close("}");
            return writer.ToString();
        }

        private static void WriteTable(Writer writer, DataModel model, TableModel table)
        {
            writer.Open($"{Quote(table.Name)}: {{");
            var sections = new List<System.Action>();
            if (model.Access == null || !table.Access.Equals(model.Access))
            {
                sections.Add(() => writer.Item($"\"access\": {Access(table.Access)}"));
            }

            sections.Add(() =>
            {
                var declared = new List<ColumnModel>();
                foreach (var column in table.Columns)
                {
                    if (!column.IsImplicit)
                    {
                        declared.Add(column);
                    }
                }

                writer.Open("\"columns\": {");
                for (var index = 0; index < declared.Count; index++)
                {
                    writer.Line($"{Quote(declared[index].Name)}: {Column(model, table, declared[index])}{Comma(index, declared.Count)}");
                }

                writer.Close("}");
            });

            if (table.Concurrency != ConcurrencyMode.Locked)
            {
                sections.Add(() => writer.Item($"\"concurrency\": {Quote(table.Concurrency.ToString())}"));
            }

            if (table.Description != null)
            {
                sections.Add(() => writer.Item($"\"description\": {Quote(table.Description)}"));
            }

            if (!table.HasFacts)
            {
                sections.Add(() => writer.Item("\"facts\": false"));
            }

            sections.Add(() => writer.Item($"\"key\": {Array(table.Key)}"));
            if (table.Persistence != Persistence.Durable)
            {
                sections.Add(() => writer.Item($"\"persistence\": {Quote(table.Persistence.ToString())}"));
            }

            if (table.Plural != Navigation.Plural(table.Name))
            {
                sections.Add(() => writer.Item($"\"plural\": {Quote(table.Plural)}"));
            }

            if (table.References.Count > 0)
            {
                sections.Add(() =>
                {
                    writer.Open("\"references\": {");
                    for (var index = 0; index < table.References.Count; index++)
                    {
                        var reference = table.References[index];
                        var parts = new List<string> { $"\"columns\": {Array(reference.Columns)}" };
                        if (reference.OnDelete != DeleteRule.Block)
                        {
                            parts.Add($"\"onDelete\": {Quote(reference.OnDelete.ToString())}");
                        }

                        if (reference.Table != reference.Name)
                        {
                            parts.Add($"\"table\": {Quote(reference.Table)}");
                        }

                        writer.Line($"{Quote(reference.Name)}: {{ {string.Join(", ", parts)} }}{Comma(index, table.References.Count)}");
                    }

                    writer.Close("}");
                });
            }

            if (!table.IsSynced)
            {
                sections.Add(() => writer.Item("\"sync\": false"));
            }

            if (table.Uniques.Count > 0)
            {
                sections.Add(() =>
                {
                    writer.Open("\"unique\": {");
                    for (var index = 0; index < table.Uniques.Count; index++)
                    {
                        var unique = table.Uniques[index];
                        var idempotent = unique.IsIdempotent ? ", \"idempotent\": true" : string.Empty;
                        writer.Line($"{Quote(unique.Name)}: {{ \"columns\": {Array(unique.Columns)}{idempotent} }}{Comma(index, table.Uniques.Count)}");
                    }

                    writer.Close("}");
                });
            }

            if (table.WriteMode != WriteMode.Mutable)
            {
                sections.Add(() => writer.Item($"\"writeMode\": {Quote(table.WriteMode.ToString())}"));
            }

            for (var index = 0; index < sections.Count; index++)
            {
                sections[index]();
                writer.AppendComma(index < sections.Count - 1);
            }

            writer.Close("}");
        }

        private static string Access(AccessModel access) =>
            $"{{ \"read\": {Array(access.Read)}, \"write\": {Array(access.Write)} }}";

        private static string DataType(DataTypeModel dataType)
        {
            var parts = new List<string>();
            if (dataType.MaxLength.HasValue)
            {
                parts.Add($"\"maxLength\": {Number(dataType.MaxLength.Value)}");
            }

            if (dataType.Precision.HasValue)
            {
                parts.Add($"\"precision\": {Number(dataType.Precision.Value)}");
            }

            if (dataType.Scale.HasValue)
            {
                parts.Add($"\"scale\": {Number(dataType.Scale.Value)}");
            }

            if (parts.Count == 0)
            {
                return Quote(dataType.ClrType);
            }

            parts.Add($"\"type\": {Quote(dataType.ClrType)}");
            return $"{{ {string.Join(", ", parts)} }}";
        }

        /// <summary>
        /// Formats a column in its short form when it has no settings of its own, or as an object otherwise.  Sizes are
        /// written only where they differ from the datatype's.
        /// </summary>
        private static string Column(DataModel model, TableModel table, ColumnModel column)
        {
            DataTypeModel? dataType = null;
            foreach (var candidate in model.DataTypes)
            {
                if (candidate.Name == column.DataType)
                {
                    dataType = candidate;
                }
            }

            var isKey = false;
            foreach (var keyColumn in table.Key)
            {
                isKey |= keyColumn == column.Name;
            }

            var parts = new List<string>();
            if (column.DefaultValue != null)
            {
                parts.Add($"\"default\": {Quote(column.DefaultValue)}");
            }

            if (column.Description != null)
            {
                parts.Add($"\"description\": {Quote(column.Description)}");
            }

            if (column.IsImmutable && !isKey)
            {
                parts.Add("\"immutable\": true");
            }

            if (column.MaxLength.HasValue && column.MaxLength != dataType?.MaxLength)
            {
                parts.Add($"\"maxLength\": {Number(column.MaxLength.Value)}");
            }

            if (column.Precision.HasValue && column.Precision != dataType?.Precision)
            {
                parts.Add($"\"precision\": {Number(column.Precision.Value)}");
            }

            if (column.Scale.HasValue && column.Scale != dataType?.Scale)
            {
                parts.Add($"\"scale\": {Number(column.Scale.Value)}");
            }

            var type = column.IsNullable ? column.DataType + "?" : column.DataType;
            if (parts.Count == 0)
            {
                return Quote(type);
            }

            parts.Add($"\"type\": {Quote(type)}");
            return $"{{ {string.Join(", ", parts)} }}";
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Array(EquatableArray<string> items)
        {
            var quoted = new List<string>();
            foreach (var item in items)
            {
                quoted.Add(Quote(item));
            }

            return $"[{string.Join(", ", quoted)}]";
        }

        private static string Comma(int index, int count) => index < count - 1 ? "," : string.Empty;

        private static string Quote(string text)
        {
            var builder = new StringBuilder(text.Length + 2);
            builder.Append('"');
            foreach (var character in text)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < ' ')
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            return builder.Append('"').ToString();
        }

        /// <summary>
        /// Accumulates indented lines, with commas appended after the fact so sections can be optional.
        /// </summary>
        private sealed class Writer
        {
            private readonly StringBuilder builder = new StringBuilder();

            private int indent;

            public void Line(string text) => this.builder.Append(' ', this.indent * 2).Append(text).Append('\n');

            public void Item(string text) => this.Line(text);

            public void Open(string text)
            {
                this.Line(text);
                this.indent++;
            }

            public void Close(string text)
            {
                this.indent--;
                this.Line(text);
            }

            /// <summary>
            /// Appends a comma to the last line written.
            /// </summary>
            /// <param name="needed">Whether another item follows.</param>
            public void AppendComma(bool needed)
            {
                if (needed)
                {
                    this.builder.Insert(this.builder.Length - 1, ',');
                }
            }

            public override string ToString() => this.builder.ToString();
        }
    }
}
