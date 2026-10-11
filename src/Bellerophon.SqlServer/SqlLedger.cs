// <copyright file="SqlLedger.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.SqlServer
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Microsoft.Data.SqlClient.Server;

    /// <summary>
    /// The fixed code behind every generated SQL Server database: the <c>bellerophon</c> schema's watermarks, the table types
    /// the writer passes rows in, and table-valued parameters.
    /// </summary>
    public static class SqlLedger
    {
        /// <summary>
        /// The statement that records a tenant's durable watermark, in the same transaction as the cut it covers.  It expects
        /// the parameters <c>@TenantId</c> and <c>@Sequence</c>.
        /// </summary>
        public const string WatermarkWrite =
            "UPDATE [bellerophon].[Watermark] SET [DurableSequence] = @Sequence WHERE [TenantId] = @TenantId;\n"
            + "IF @@ROWCOUNT = 0 INSERT INTO [bellerophon].[Watermark] ([TenantId], [DurableSequence]) VALUES (@TenantId, @Sequence);\n";

        /// <summary>
        /// Checks that the schema's migrations have been applied: the watermark table exists.
        /// </summary>
        /// <param name="connection">An open connection.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>A task that completes when the check passes.</returns>
        /// <exception cref="InvalidOperationException">The schema is missing.</exception>
        public static async Task CheckSchemaAsync(SqlConnection connection, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT CASE WHEN OBJECT_ID(N'[bellerophon].[Watermark]', N'U') IS NULL THEN 0 ELSE 1 END;";
            var present = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (present == 0)
            {
                throw new InvalidOperationException(
                    $"The database '{connection.Database}' has no ledger schema: [bellerophon].[Watermark] is missing. Apply the schema project's migrations first.");
            }
        }

        /// <summary>
        /// Creates every table type that does not exist yet.  Each is named after a hash of its definition, so an existing
        /// type with the same name is the same type.
        /// </summary>
        /// <param name="connection">An open connection.</param>
        /// <param name="types">The types.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>A task that completes when every type exists.</returns>
        public static async Task EnsureTableTypesAsync(SqlConnection connection, IEnumerable<SqlTableType> types, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(types);
            foreach (var type in types)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "IF TYPE_ID(@Name) IS NULL EXEC sp_executesql @Definition;";
                command.Parameters.Add("@Name", SqlDbType.NVarChar, 256).Value = type.Name;
                command.Parameters.Add("@Definition", SqlDbType.NVarChar, -1).Value = type.Definition;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads every tenant the database holds: every tenant with a watermark.
        /// </summary>
        /// <param name="connection">An open connection.</param>
        /// <param name="cancellationToken">Cancels the work.</param>
        /// <returns>The tenants.</returns>
        public static async Task<IReadOnlyList<Guid>> ReadTenantsAsync(SqlConnection connection, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            var tenants = new List<Guid>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT [TenantId] FROM [bellerophon].[Watermark] ORDER BY [TenantId];";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tenants.Add(reader.GetGuid(0));
            }

            return tenants;
        }

        /// <summary>
        /// Adds a table-valued parameter.  Call it only with rows: SQL Server refuses an empty one.
        /// </summary>
        /// <param name="command">The command.</param>
        /// <param name="name">The parameter's name, for example <c>@NodeRows</c>.</param>
        /// <param name="typeName">The table type's name.</param>
        /// <param name="records">The rows.</param>
        /// <returns>The parameter.</returns>
        public static SqlParameter AddTable(SqlCommand command, string name, string typeName, IEnumerable<SqlDataRecord> records)
        {
            ArgumentNullException.ThrowIfNull(command);
            var parameter = command.Parameters.Add(name, SqlDbType.Structured);
            parameter.TypeName = typeName;
            parameter.Value = records;
            return parameter;
        }
    }
}
