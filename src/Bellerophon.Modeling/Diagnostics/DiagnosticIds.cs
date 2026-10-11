// <copyright file="DiagnosticIds.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling.Diagnostics
{
    using System.Collections.Generic;

    /// <summary>
    /// The catalog of model diagnostics.
    /// </summary>
    /// <remarks>
    /// Ranges: BEL0xxx build configuration (reported by the generator); BEL1xxx model file format; BEL2xxx semantic validation;
    /// BEL3xxx schema evolution (reserved); BEL4xxx analyzers for hand-written code (reserved).
    /// </remarks>
    public static class DiagnosticIds
    {
        /// <summary>A project contains more than one model file.</summary>
        public const string MultipleModelFiles = "BEL0002";

        /// <summary>The file is not valid JSON.</summary>
        public const string SyntaxError = "BEL1001";

        /// <summary>A value has the wrong JSON type.</summary>
        public const string UnexpectedType = "BEL1002";

        /// <summary>A property is not part of the format.</summary>
        public const string UnknownProperty = "BEL1003";

        /// <summary>A property has a value outside its allowed set.</summary>
        public const string InvalidValue = "BEL1004";

        /// <summary>A name is declared twice.</summary>
        public const string DuplicateName = "BEL1005";

        /// <summary>A required property is missing.</summary>
        public const string MissingProperty = "BEL1006";

        /// <summary>A name is not a valid identifier.</summary>
        public const string InvalidIdentifier = "BEL1007";

        /// <summary>A column names a datatype that is not in the dictionary.</summary>
        public const string UnknownDataType = "BEL2001";

        /// <summary>A key, reference, or unique constraint names an undeclared column.</summary>
        public const string UnknownColumn = "BEL2002";

        /// <summary>A reference names an undeclared table.</summary>
        public const string UnknownTable = "BEL2003";

        /// <summary>A reference's columns do not match the parent's key in number.</summary>
        public const string ReferenceArity = "BEL2004";

        /// <summary>An implicit column is declared explicitly.</summary>
        public const string ReservedName = "BEL2005";

        /// <summary>A combination of table policies is not allowed.</summary>
        public const string InvalidPolicy = "BEL2006";

        /// <summary>A datatype's CLR type was not found in the compilation (reported by the generator).</summary>
        public const string UnresolvedClrType = "BEL2007";

        /// <summary>A durable table references an ephemeral table.</summary>
        public const string DurableReferencesEphemeral = "BEL2008";

        /// <summary>A synced table references a table that is not synced.</summary>
        public const string SyncedReferencesUnsynced = "BEL2009";

        /// <summary>A reference column's type differs from the parent key column's type.</summary>
        public const string ReferenceType = "BEL2010";

        /// <summary>A decimal lacks precision or scale, or a string in a key or unique constraint lacks a maximum length.</summary>
        public const string SizeRule = "BEL2011";

        /// <summary>A table has no access for reads or writes.</summary>
        public const string MissingAccess = "BEL2013";

        /// <summary>The model's name is the same as a table's name.</summary>
        public const string NameConflict = "BEL2014";

        /// <summary>Two generated members would have the same name.</summary>
        public const string MemberCollision = "BEL2015";

        /// <summary>A durable column's type has no SQL Server type.</summary>
        public const string NoSqlServerType = "BEL2016";

        /// <summary>
        /// Gets the short title of every diagnostic, keyed by identifier.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Titles { get; } = new Dictionary<string, string>
        {
            [MultipleModelFiles] = "More than one model file",
            [SyntaxError] = "Invalid JSON",
            [UnexpectedType] = "Unexpected JSON type",
            [UnknownProperty] = "Unknown property",
            [InvalidValue] = "Invalid value",
            [DuplicateName] = "Duplicate name",
            [MissingProperty] = "Missing property",
            [InvalidIdentifier] = "Invalid identifier",
            [UnknownDataType] = "Unknown datatype",
            [UnknownColumn] = "Unknown column",
            [UnknownTable] = "Unknown table",
            [ReferenceArity] = "Reference does not match key",
            [ReservedName] = "Reserved column name",
            [InvalidPolicy] = "Invalid table policy",
            [UnresolvedClrType] = "Unresolved CLR type",
            [DurableReferencesEphemeral] = "Durable table references ephemeral table",
            [SyncedReferencesUnsynced] = "Synced table references unsynced table",
            [ReferenceType] = "Reference type mismatch",
            [SizeRule] = "Missing size",
            [MissingAccess] = "Missing access",
            [NameConflict] = "Model name conflicts with a table",
            [MemberCollision] = "Generated names collide",
            [NoSqlServerType] = "No SQL Server type",
        };
    }
}
