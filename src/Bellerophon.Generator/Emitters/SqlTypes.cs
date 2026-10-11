// <copyright file="SqlTypes.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Generator.Emitters
{
    using System.Collections.Generic;
    using System.Globalization;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// How one column is stored in SQL Server: its column type, its table-valued-parameter metadata, and the calls that write
    /// it to a parameter record and read it back.  The schema (Entity Framework), the table types, the writer, and the loader
    /// all take their types from here, so they cannot disagree.
    /// </summary>
    /// <param name="Ddl">The column type as written in DDL, for example <c>nvarchar(64)</c>.</param>
    /// <param name="MetaData">C# arguments after the column name for <c>SqlMetaData</c>, for example <c>SqlDbType.NVarChar, 64</c>.</param>
    /// <param name="Setter">The <c>SqlDataRecord</c> call with <c>{0}</c> for the ordinal and <c>{1}</c> for the value.</param>
    /// <param name="Getter">The <c>SqlDataReader</c> expression with <c>{0}</c> for the ordinal.</param>
    internal sealed record SqlColumnType(string Ddl, string MetaData, string Setter, string Getter)
    {
        private const string DbType = "global::System.Data.SqlDbType";

        private static readonly HashSet<string> EnumUnderlyingTypes = new HashSet<string> { "System.Int32", "System.Int64", "System.Int16", "System.Byte" };

        private static readonly Dictionary<string, (string Ddl, string DbType, string Setter, string Getter)> Fixed = new Dictionary<string, (string, string, string, string)>
        {
            ["System.Guid"] = ("uniqueidentifier", "UniqueIdentifier", "SetGuid({0}, {1})", "GetGuid({0})"),
            ["System.Int32"] = ("int", "Int", "SetInt32({0}, {1})", "GetInt32({0})"),
            ["System.Int64"] = ("bigint", "BigInt", "SetInt64({0}, {1})", "GetInt64({0})"),
            ["System.Int16"] = ("smallint", "SmallInt", "SetInt16({0}, {1})", "GetInt16({0})"),
            ["System.Byte"] = ("tinyint", "TinyInt", "SetByte({0}, {1})", "GetByte({0})"),
            ["System.Boolean"] = ("bit", "Bit", "SetBoolean({0}, {1})", "GetBoolean({0})"),
            ["System.Double"] = ("float", "Float", "SetDouble({0}, {1})", "GetDouble({0})"),
            ["System.Single"] = ("real", "Real", "SetFloat({0}, {1})", "GetFloat({0})"),
            ["System.DateTimeOffset"] = ("datetimeoffset(7)", "DateTimeOffset", "SetDateTimeOffset({0}, {1})", "GetDateTimeOffset({0})"),
            ["System.DateTime"] = ("datetime2(7)", "DateTime2", "SetDateTime({0}, {1})", "GetDateTime({0})"),
            ["System.DateOnly"] = ("date", "Date", "SetDateTime({0}, {1}.ToDateTime(global::System.TimeOnly.MinValue))", "global::System.DateOnly.FromDateTime(reader.GetDateTime({0}))"),
            ["System.TimeOnly"] = ("time(7)", "Time", "SetTimeSpan({0}, {1}.ToTimeSpan())", "global::System.TimeOnly.FromTimeSpan(reader.GetTimeSpan({0}))"),
        };

        /// <summary>
        /// Maps a column to SQL Server.
        /// </summary>
        /// <param name="column">The column.</param>
        /// <param name="resolved">The resolved model, for enumerations' underlying types.</param>
        /// <returns>The mapping, or null if the type has none.</returns>
        public static SqlColumnType? For(ColumnModel column, ResolvedModel resolved) =>
            For(column.ClrType, resolved.KindOf(column.ClrType), resolved.UnderlyingTypeOf(column.ClrType), column.MaxLength, column.Precision, column.Scale);

        /// <summary>
        /// Maps a CLR type to SQL Server.
        /// </summary>
        /// <param name="clrType">The full CLR type name.</param>
        /// <param name="kind">What kind of type it is.</param>
        /// <param name="underlyingType">An enumeration's underlying type.</param>
        /// <param name="maxLength">The maximum length of a string or byte array; null for unlimited.</param>
        /// <param name="precision">A decimal's precision.</param>
        /// <param name="scale">A decimal's scale.</param>
        /// <returns>The mapping, or null if the type has none.</returns>
        public static SqlColumnType? For(string clrType, ClrTypeKind kind, string underlyingType, int? maxLength, int? precision, int? scale)
        {
            if (kind == ClrTypeKind.Enum)
            {
                // Enumerations are stored as their numbers.  Renumbering one is a data migration: export by name, change, reload.
                // Unsigned underlying types other than byte (and sbyte) have no exact SQL Server type.
                if (!EnumUnderlyingTypes.Contains(underlyingType) || !Fixed.TryGetValue(underlyingType, out var number))
                {
                    return null;
                }

                var cast = Names.CSharpType(underlyingType, false);
                var enumType = Names.CSharpType(clrType, false);
                return new SqlColumnType(
                    number.Ddl,
                    $"{DbType}.{number.DbType}",
                    number.Setter.Replace("{1}", $"({cast}){{1}}"),
                    $"({enumType})reader.{number.Getter}");
            }

            if (clrType == "System.String")
            {
                var length = maxLength?.ToString(CultureInfo.InvariantCulture);
                return new SqlColumnType(
                    $"nvarchar({length ?? "max"})",
                    $"{DbType}.NVarChar, {length ?? "-1"}",
                    "SetString({0}, {1})",
                    "reader.GetString({0})");
            }

            if (clrType == "System.Byte[]")
            {
                var length = maxLength?.ToString(CultureInfo.InvariantCulture);
                return new SqlColumnType(
                    $"varbinary({length ?? "max"})",
                    $"{DbType}.VarBinary, {length ?? "-1"}",
                    "SetBytes({0}, 0, {1}, 0, {1}.Length)",
                    "reader.GetFieldValue<byte[]>({0})");
            }

            if (clrType == "System.Decimal")
            {
                var p = (precision ?? 18).ToString(CultureInfo.InvariantCulture);
                var s = (scale ?? 0).ToString(CultureInfo.InvariantCulture);
                return new SqlColumnType($"decimal({p},{s})", $"{DbType}.Decimal, {p}, {s}", "SetDecimal({0}, {1})", "reader.GetDecimal({0})");
            }

            if (!Fixed.TryGetValue(clrType, out var known))
            {
                return null;
            }

            var getter = known.Getter.StartsWith("global::", System.StringComparison.Ordinal) ? known.Getter : "reader." + known.Getter;
            return new SqlColumnType(known.Ddl, $"{DbType}.{known.DbType}", known.Setter, getter);
        }
    }
}
