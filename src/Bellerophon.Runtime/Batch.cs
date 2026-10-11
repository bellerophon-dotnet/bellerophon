// <copyright file="Batch.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Runtime
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// What a batch operation does to its row.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BatchAction
    {
        /// <summary>Inserts a row.</summary>
        Insert,

        /// <summary>Updates the row with the given key, at the version in the row.</summary>
        Update,

        /// <summary>Deletes the row with the given key, at the given version.</summary>
        Delete,
    }

    /// <summary>
    /// A batch: an ordered list of operations applied in one transaction, so all of them commit or none does.
    /// </summary>
    public sealed class BatchRequest
    {
        /// <summary>
        /// Gets or sets the operations, applied in order.
        /// </summary>
        public List<BatchOperation> Operations { get; set; } = new List<BatchOperation>();
    }

    /// <summary>
    /// One operation in a batch.
    /// </summary>
    public sealed class BatchOperation
    {
        /// <summary>
        /// Gets or sets what the operation does.
        /// </summary>
        public BatchAction Action { get; set; }

        /// <summary>
        /// Gets or sets the table's name, for example <c>Account</c>.
        /// </summary>
        public string Table { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the key of the row to update or delete, as an object of key columns, for example
        /// <c>{ "scheme": "Custodian", "code": "1234" }</c>.
        /// </summary>
        public JsonElement? Key { get; set; }

        /// <summary>
        /// Gets or sets the row to insert, or the new values (with the version being replaced) for an update, in the table's
        /// request shape.
        /// </summary>
        public JsonElement? Row { get; set; }

        /// <summary>
        /// Gets or sets, for a delete, the version of the row being deleted.
        /// </summary>
        public long RowVersion { get; set; }
    }

    /// <summary>
    /// The batch itself is malformed: an unknown table, an action the table does not allow, or a missing or unreadable key
    /// or row.  Answered with 400, and nothing is changed.
    /// </summary>
    public sealed class InvalidBatchException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="InvalidBatchException"/> class.
        /// </summary>
        public InvalidBatchException()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InvalidBatchException"/> class.
        /// </summary>
        /// <param name="message">What is wrong with the batch.</param>
        public InvalidBatchException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InvalidBatchException"/> class.
        /// </summary>
        /// <param name="message">What is wrong with the batch.</param>
        /// <param name="innerException">The underlying error.</param>
        public InvalidBatchException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// Reads the rows and keys of batch operations.  Generated batch controllers call it.
    /// </summary>
    public static class BatchReader
    {
        /// <summary>
        /// Reads an operation's row.
        /// </summary>
        /// <typeparam name="T">The table's request type.</typeparam>
        /// <param name="operation">The operation.</param>
        /// <param name="options">The API's JSON options.</param>
        /// <returns>The row.</returns>
        public static T Row<T>(BatchOperation operation, JsonSerializerOptions options)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (operation.Row is not { ValueKind: JsonValueKind.Object } row)
            {
                throw new InvalidBatchException($"{operation.Action} on {operation.Table} needs a row.");
            }

            try
            {
                return row.Deserialize<T>(options) ?? throw new InvalidBatchException($"The row for {operation.Table} is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidBatchException($"The row for {operation.Table} cannot be read: {exception.Message}", exception);
            }
        }

        /// <summary>
        /// Reads one column of an operation's key.  Column names match without regard to case.
        /// </summary>
        /// <typeparam name="T">The column's type.</typeparam>
        /// <param name="operation">The operation.</param>
        /// <param name="column">The key column.</param>
        /// <param name="options">The API's JSON options.</param>
        /// <returns>The column's value.</returns>
        public static T Key<T>(BatchOperation operation, string column, JsonSerializerOptions options)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (operation.Key is not { ValueKind: JsonValueKind.Object } key)
            {
                throw new InvalidBatchException($"{operation.Action} on {operation.Table} needs a key.");
            }

            foreach (var property in key.EnumerateObject())
            {
                if (string.Equals(property.Name, column, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        return property.Value.Deserialize<T>(options) ?? throw new InvalidBatchException($"The key for {operation.Table} has no {column}.");
                    }
                    catch (JsonException exception)
                    {
                        throw new InvalidBatchException($"The key for {operation.Table} has an unreadable {column}: {exception.Message}", exception);
                    }
                }
            }

            throw new InvalidBatchException($"The key for {operation.Table} is missing {column}.");
        }
    }
}
