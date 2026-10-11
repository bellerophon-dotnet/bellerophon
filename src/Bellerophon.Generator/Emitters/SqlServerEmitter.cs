// <copyright file="SqlServerEmitter.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Emits the SQL Server database for the Primary role: the write-behind flusher's writer and the recovery loader.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tables are shared by every tenant: each primary key starts with <c>TenantId</c>, and there are no unique
    /// constraints, foreign keys, or secondary indexes, since the ledger enforces those in memory and the loader checks them
    /// again.  A cut holds each key at most once, so its writes may run in any order.
    /// </para>
    /// <para>
    /// A cut is written in one round trip and one database transaction: for each table, an UPDATE then an INSERT from a
    /// table-valued parameter of the changed rows, a DELETE from one of the deleted keys, and the tenant's watermark.  The
    /// table types are named after a hash of their definition, so a changed table gets a new type and the old one is left
    /// alone; <c>PrepareAsync</c> creates any that are missing.
    /// </para>
    /// </remarks>
    internal static class SqlServerEmitter
    {
        private const string Sql = "global::Microsoft.Data.SqlClient";

        private const string Runtime = "global::Bellerophon.SqlServer.SqlLedger";

        private const string Record = "global::Microsoft.Data.SqlClient.Server.SqlDataRecord";

        private const string MetaData = "global::Microsoft.Data.SqlClient.Server.SqlMetaData";

        private const string Task = "global::System.Threading.Tasks.Task";

        private const string Token = "global::System.Threading.CancellationToken";

        /// <summary>
        /// Emits the database class.
        /// </summary>
        /// <param name="shape">The model in the Primary role.</param>
        /// <returns>The source text.</returns>
        public static string Emit(ModelShape shape)
        {
            var tables = shape.TablesInDependencyOrder().Where(ModelShape.IsDurable).ToList();
            var database = $"{shape.Name}SqlDatabase";
            var writer = new CodeWriter();
            writer.Header();
            writer.Line($"namespace {shape.RoleNamespace}");
            writer.Open();
            writer.Summary($"The {shape.Name} model's SQL Server database: writes the flusher's cuts and loads tenants for recovery. Tables are shared by every tenant and have primary keys only; apply the schema's migrations first, then call PrepareAsync once at startup.");
            writer.Line($"[global::System.CodeDom.Compiler.GeneratedCode(\"Bellerophon\", \"{Names.Version}\")]");
            writer.Line($"public sealed partial class {database}");
            writer.Open();

            // The statements and table types, fixed at generation time.
            foreach (var table in tables)
            {
                var rows = RowsDefinition(shape, table);
                var keys = KeysDefinition(shape, table);
                writer.Line($"private const string {table.Name}RowsType = \"{TypeName(table, "Rows", rows)}\";");
                writer.Line();
                writer.Line($"private const string {table.Name}KeysType = \"{TypeName(table, "Keys", keys)}\";");
                writer.Line();
                writer.Line($"private const string {table.Name}Write = {Literal(WriteStatement(shape, table))};");
                writer.Line();
                writer.Line($"private const string {table.Name}Delete = {Literal(DeleteStatement(table))};");
                writer.Line();
            }

            var load = new StringBuilder();
            load.Append("SELECT [DurableSequence] FROM [bellerophon].[Watermark] WHERE [TenantId] = @TenantId;\n");
            foreach (var table in tables)
            {
                load.Append(SelectStatement(shape, table));
            }

            writer.Line($"private const string LoadText = {Literal(load.ToString())};");
            writer.Line();
            foreach (var table in tables)
            {
                writer.Line($"private static readonly {MetaData}[] {table.Name}RowsMetaData =");
                writer.Open();
                foreach (var column in StoredColumns(shape, table))
                {
                    writer.Line($"new {MetaData}(\"{column.Name}\", {Map(shape, column).MetaData}),");
                }

                writer.Line($"new {MetaData}(\"RowVersion\", global::System.Data.SqlDbType.BigInt),");
                writer.Close(";");
                writer.Line();
                writer.Line($"private static readonly {MetaData}[] {table.Name}KeysMetaData =");
                writer.Open();
                foreach (var name in table.Key)
                {
                    writer.Line($"new {MetaData}(\"{name}\", {Map(shape, table.FindColumn(name)!).MetaData}),");
                }

                writer.Close(";");
                writer.Line();
            }

            // Construction.
            writer.Line("private readonly string connectionString;");
            writer.Line();
            writer.Summary($"Initializes a new instance of the {database} class.");
            writer.Line("/// <param name=\"connectionString\">The database's connection string.</param>");
            writer.Line($"public {database}(string connectionString)");
            writer.Open();
            writer.Line("global::System.ArgumentException.ThrowIfNullOrEmpty(connectionString);");
            writer.Line("this.connectionString = connectionString;");
            writer.Close();
            writer.Line();
            writer.Summary("Gets the table types the writer passes rows in, each named after a hash of its definition. PrepareAsync creates any that are missing.");
            writer.Line($"public static global::System.Collections.Generic.IReadOnlyList<global::Bellerophon.SqlServer.SqlTableType> TableTypes {{ get; }} = new global::Bellerophon.SqlServer.SqlTableType[]");
            writer.Open();
            foreach (var table in tables)
            {
                writer.Line($"new global::Bellerophon.SqlServer.SqlTableType({table.Name}RowsType, {Literal($"CREATE TYPE {TypeName(table, "Rows", RowsDefinition(shape, table))} AS TABLE ({RowsDefinition(shape, table)});")}),");
                writer.Line($"new global::Bellerophon.SqlServer.SqlTableType({table.Name}KeysType, {Literal($"CREATE TYPE {TypeName(table, "Keys", KeysDefinition(shape, table))} AS TABLE ({KeysDefinition(shape, table)});")}),");
            }

            writer.Close(";");
            writer.Line();
            writer.Summary("Gets or sets the time, in seconds, a command may run before it fails. The default is 30.");
            writer.Line("public int CommandTimeout { get; init; } = 30;");

            // Preparation.
            writer.Line();
            writer.Summary("Prepares the database for this model, once at startup: checks that the schema's migrations have been applied, and creates any missing table types.");
            writer.Line("/// <param name=\"cancellationToken\">Cancels the work.</param>");
            writer.Line("/// <returns>A task that completes when the database is ready.</returns>");
            writer.Line($"public async {Task} PrepareAsync({Token} cancellationToken = default)");
            writer.Open();
            writer.Line("await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);");
            writer.Line($"await {Runtime}.CheckSchemaAsync(connection, cancellationToken).ConfigureAwait(false);");
            writer.Line($"await {Runtime}.EnsureTableTypesAsync(connection, TableTypes, cancellationToken).ConfigureAwait(false);");
            writer.Close();

            // Tenants.
            writer.Line();
            writer.Summary("Reads the tenants the database holds: every tenant with a watermark.");
            writer.Line("/// <param name=\"cancellationToken\">Cancels the work.</param>");
            writer.Line("/// <returns>The tenants.</returns>");
            writer.Line($"public async {Task}<global::System.Collections.Generic.IReadOnlyList<global::System.Guid>> ReadTenantsAsync({Token} cancellationToken = default)");
            writer.Open();
            writer.Line("await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);");
            writer.Line($"return await {Runtime}.ReadTenantsAsync(connection, cancellationToken).ConfigureAwait(false);");
            writer.Close();

            EmitLoad(shape, tables, writer);
            EmitWrite(shape, tables, writer);

            writer.Line();
            writer.Line($"private async {Task}<{Sql}.SqlConnection> OpenAsync({Token} cancellationToken)");
            writer.Open();
            writer.Line($"var connection = new {Sql}.SqlConnection(this.connectionString);");
            writer.Line("try");
            writer.Open();
            writer.Line("await connection.OpenAsync(cancellationToken).ConfigureAwait(false);");
            writer.Line("return connection;");
            writer.Close();
            writer.Line("catch");
            writer.Open();
            writer.Line("await connection.DisposeAsync().ConfigureAwait(false);");
            writer.Line("throw;");
            writer.Close();
            writer.Close();

            foreach (var table in tables)
            {
                EmitRecords(shape, table, writer);
                EmitReader(shape, table, writer);
            }

            writer.Close();
            writer.Close();
            return writer.ToString();
        }

        /// <summary>
        /// Gets the columns stored for a table besides the tenant and version: its DTO's columns, keys included, in order.
        /// </summary>
        private static List<ColumnModel> StoredColumns(ModelShape shape, TableModel table) => shape.DtoColumns(table).ToList();

        private static SqlColumnType Map(ModelShape shape, ColumnModel column) => SqlColumnType.For(column, shape.Resolved)!;

        private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

        private static string TableName(TableModel table) => $"[dbo].{Quote(table.Name)}";

        private static string RowsDefinition(ModelShape shape, TableModel table)
        {
            var columns = StoredColumns(shape, table).Select(column => $"{Quote(column.Name)} {Map(shape, column).Ddl} {(column.IsNullable ? "NULL" : "NOT NULL")}").ToList();
            columns.Add("[RowVersion] bigint NOT NULL");
            columns.Add($"PRIMARY KEY ({string.Join(", ", table.Key.Select(Quote))})");
            return string.Join(", ", columns);
        }

        private static string KeysDefinition(ModelShape shape, TableModel table)
        {
            var columns = table.Key.Select(name => $"{Quote(name)} {Map(shape, table.FindColumn(name)!).Ddl} NOT NULL").ToList();
            columns.Add($"PRIMARY KEY ({string.Join(", ", table.Key.Select(Quote))})");
            return string.Join(", ", columns);
        }

        /// <summary>
        /// Names a table type after a hash of its definition, so a changed definition gets a new type.
        /// </summary>
        private static string TypeName(TableModel table, string kind, string definition)
        {
            byte[] hash;
            using (var algorithm = SHA256.Create())
            {
                hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes($"{table.Name}|{kind}|{definition}"));
            }

            var suffix = string.Concat(hash.Take(4).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            return $"[bellerophon].{Quote($"{table.Name}{kind}_{suffix}")}";
        }

        private static string KeyMatch(TableModel table, string left, string right) =>
            string.Join(" AND ", new[] { $"{left}.[TenantId] = @TenantId" }.Concat(table.Key.Select(name => $"{left}.{Quote(name)} = {right}.{Quote(name)}")));

        private static string WriteStatement(ModelShape shape, TableModel table)
        {
            var parameter = $"@{table.Name}Rows";
            var mutable = StoredColumns(shape, table).Where(column => !table.Key.Contains(column.Name)).Select(column => column.Name).Concat(new[] { "RowVersion" }).ToList();
            var all = StoredColumns(shape, table).Select(column => column.Name).Concat(new[] { "RowVersion" }).ToList();
            var update = $"UPDATE t SET {string.Join(", ", mutable.Select(name => $"t.{Quote(name)} = r.{Quote(name)}"))} FROM {TableName(table)} AS t INNER JOIN {parameter} AS r ON {KeyMatch(table, "t", "r")};\n";
            var insert = $"INSERT INTO {TableName(table)} ([TenantId], {string.Join(", ", all.Select(Quote))}) SELECT @TenantId, {string.Join(", ", all.Select(name => $"r.{Quote(name)}"))} FROM {parameter} AS r WHERE NOT EXISTS (SELECT 1 FROM {TableName(table)} AS t WHERE {KeyMatch(table, "t", "r")});\n";
            return update + insert;
        }

        private static string DeleteStatement(TableModel table) =>
            $"DELETE t FROM {TableName(table)} AS t INNER JOIN @{table.Name}Keys AS k ON {KeyMatch(table, "t", "k")};\n";

        private static string SelectStatement(ModelShape shape, TableModel table)
        {
            var all = StoredColumns(shape, table).Select(column => column.Name).Concat(new[] { "RowVersion" });
            return $"SELECT {string.Join(", ", all.Select(Quote))} FROM {TableName(table)} WHERE [TenantId] = @TenantId;\n";
        }

        private static string Literal(string text) =>
            "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

        private static void EmitLoad(ModelShape shape, List<TableModel> tables, CodeWriter writer)
        {
            writer.Line();
            writer.Summary("Loads one tenant into an empty partition, parents first, keeping every row's stored version, and resumes the partition after the tenant's watermark. One round trip. If it fails, discard the partition.");
            writer.Line("/// <param name=\"partition\">The tenant's empty partition.</param>");
            writer.Line("/// <param name=\"cancellationToken\">Cancels the work.</param>");
            writer.Line("/// <returns>The tenant's durable watermark.</returns>");
            writer.Line("/// <exception cref=\"System.Data.ConstraintException\">The stored rows break a key, unique, or reference rule: the database was written inconsistently.</exception>");
            writer.Line($"public async {Task}<long> LoadAsync({shape.PartitionName} partition, {Token} cancellationToken = default)");
            writer.Open();
            writer.Line("global::System.ArgumentNullException.ThrowIfNull(partition);");
            writer.Line("await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);");
            writer.Line("await using var command = connection.CreateCommand();");
            writer.Line("command.CommandTimeout = this.CommandTimeout;");
            writer.Line("command.CommandText = LoadText;");
            writer.Line("command.Parameters.Add(\"@TenantId\", global::System.Data.SqlDbType.UniqueIdentifier).Value = partition.TenantId;");
            writer.Line("await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);");
            writer.Line("var watermark = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? reader.GetInt64(0) : 0L;");
            writer.Line("partition.BeginLoad();");
            foreach (var table in tables)
            {
                writer.Line("await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);");
                writer.Line("while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))");
                writer.Open();
                writer.Line($"partition.Load{table.Name}(Read{table.Name}(reader));");
                writer.Close();
                writer.Line();
            }

            writer.Line("partition.CompleteLoad(watermark);");
            writer.Line("return watermark;");
            writer.Close();
        }

        private static void EmitWrite(ModelShape shape, List<TableModel> tables, CodeWriter writer)
        {
            var cut = $"{shape.Name}Cut";
            writer.Line();
            writer.Summary("Writes a cut in one database transaction and one round trip: every changed row, every deleted key, and the tenant's watermark. After it returns, mark the partition durable through the cut's sequence.");
            writer.Line("/// <param name=\"cut\">The cut.</param>");
            writer.Line("/// <param name=\"cancellationToken\">Cancels the work; the transaction then rolls back.</param>");
            writer.Line("/// <returns>A task that completes when the cut is committed.</returns>");
            writer.Line($"public async {Task} WriteAsync({cut} cut, {Token} cancellationToken = default)");
            writer.Open();
            writer.Line("global::System.ArgumentNullException.ThrowIfNull(cut);");
            writer.Line("await using var connection = await this.OpenAsync(cancellationToken).ConfigureAwait(false);");
            writer.Line($"await using var transaction = ({Sql}.SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);");
            writer.Line("await this.WriteAsync(cut, connection, transaction, cancellationToken).ConfigureAwait(false);");
            writer.Line("await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);");
            writer.Close();

            writer.Line();
            writer.Summary("Writes a cut inside a transaction the caller owns, without committing it, so other writes that must be durable with the cut (a broker connection's resume cursor, say) commit with it.");
            writer.Line("/// <param name=\"cut\">The cut.</param>");
            writer.Line("/// <param name=\"connection\">An open connection.</param>");
            writer.Line("/// <param name=\"transaction\">The connection's transaction; the caller commits it.</param>");
            writer.Line("/// <param name=\"cancellationToken\">Cancels the work.</param>");
            writer.Line("/// <returns>A task that completes when the cut is written.</returns>");
            writer.Line($"public async {Task} WriteAsync({cut} cut, {Sql}.SqlConnection connection, {Sql}.SqlTransaction transaction, {Token} cancellationToken = default)");
            writer.Open();
            writer.Line("global::System.ArgumentNullException.ThrowIfNull(cut);");
            writer.Line("global::System.ArgumentNullException.ThrowIfNull(connection);");
            writer.Line("await using var command = connection.CreateCommand();");
            writer.Line("command.Transaction = transaction;");
            writer.Line("command.CommandTimeout = this.CommandTimeout;");
            writer.Line("var text = new global::System.Text.StringBuilder(\"SET NOCOUNT ON; SET XACT_ABORT ON;\\n\");");
            writer.Line("command.Parameters.Add(\"@TenantId\", global::System.Data.SqlDbType.UniqueIdentifier).Value = cut.TenantId;");
            writer.Line("command.Parameters.Add(\"@Sequence\", global::System.Data.SqlDbType.BigInt).Value = cut.Sequence;");
            foreach (var table in tables)
            {
                writer.Line($"if (cut.Changed{table.Plural}.Count > 0)");
                writer.Open();
                writer.Line($"text.Append({table.Name}Write);");
                writer.Line($"{Runtime}.AddTable(command, \"@{table.Name}Rows\", {table.Name}RowsType, {table.Name}Rows(cut.Changed{table.Plural}));");
                writer.Close();
                writer.Line();
                writer.Line($"if (cut.Deleted{table.Plural}.Count > 0)");
                writer.Open();
                writer.Line($"text.Append({table.Name}Delete);");
                writer.Line($"{Runtime}.AddTable(command, \"@{table.Name}Keys\", {table.Name}KeysType, {table.Name}Keys(cut.Deleted{table.Plural}));");
                writer.Close();
                writer.Line();
            }

            writer.Line($"text.Append({Runtime}.WatermarkWrite);");
            writer.Line("command.CommandText = text.ToString();");
            writer.Line("await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);");
            writer.Close();
        }

        private static void EmitRecords(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var dto = shape.DtoType(table);
            var columns = StoredColumns(shape, table);
            writer.Line();
            writer.Line($"private static global::System.Collections.Generic.IEnumerable<{Record}> {table.Name}Rows(global::System.Collections.Generic.IReadOnlyList<{dto}> rows)");
            writer.Open();
            writer.Line($"var record = new {Record}({table.Name}RowsMetaData);");
            writer.Line("foreach (var dto in rows)");
            writer.Open();
            for (var ordinal = 0; ordinal < columns.Count; ordinal++)
            {
                EmitSet(shape, columns[ordinal], ordinal, writer);
            }

            writer.Line($"record.SetInt64({columns.Count.ToString(CultureInfo.InvariantCulture)}, dto.RowVersion);");
            writer.Line("yield return record;");
            writer.Close();
            writer.Close();

            writer.Line();
            writer.Line($"private static global::System.Collections.Generic.IEnumerable<{Record}> {table.Name}Keys(global::System.Collections.Generic.IReadOnlyList<{dto}> rows)");
            writer.Open();
            writer.Line($"var record = new {Record}({table.Name}KeysMetaData);");
            writer.Line("foreach (var dto in rows)");
            writer.Open();
            for (var ordinal = 0; ordinal < table.Key.Count; ordinal++)
            {
                EmitSet(shape, table.FindColumn(table.Key[ordinal])!, ordinal, writer);
            }

            writer.Line("yield return record;");
            writer.Close();
            writer.Close();
        }

        private static void EmitSet(ModelShape shape, ColumnModel column, int ordinal, CodeWriter writer)
        {
            var type = Map(shape, column);
            var index = ordinal.ToString(CultureInfo.InvariantCulture);
            var property = $"dto.{Names.Identifier(column.Name)}";
            if (!column.IsNullable)
            {
                writer.Line("record." + string.Format(CultureInfo.InvariantCulture, type.Setter, index, property) + ";");
                return;
            }

            var local = "value" + index;
            writer.Line($"if ({property} is {{ }} {local})");
            writer.Open();
            writer.Line("record." + string.Format(CultureInfo.InvariantCulture, type.Setter, index, local) + ";");
            writer.Close();
            writer.Line("else");
            writer.Open();
            writer.Line($"record.SetDBNull({index});");
            writer.Close();
            writer.Line();
        }

        private static void EmitReader(ModelShape shape, TableModel table, CodeWriter writer)
        {
            var columns = StoredColumns(shape, table);
            writer.Line();
            writer.Line($"private static {shape.DtoType(table)} Read{table.Name}({Sql}.SqlDataReader reader) => new {shape.DtoType(table)}");
            writer.Open();
            for (var ordinal = 0; ordinal < columns.Count; ordinal++)
            {
                var column = columns[ordinal];
                var index = ordinal.ToString(CultureInfo.InvariantCulture);
                var read = string.Format(CultureInfo.InvariantCulture, Map(shape, column).Getter, index);
                var value = column.IsNullable ? $"reader.IsDBNull({index}) ? null : {read}" : read;
                writer.Line($"{Names.Identifier(column.Name)} = {value},");
            }

            writer.Line($"RowVersion = reader.GetInt64({columns.Count.ToString(CultureInfo.InvariantCulture)}),");
            writer.Close(";");
        }
    }
}
