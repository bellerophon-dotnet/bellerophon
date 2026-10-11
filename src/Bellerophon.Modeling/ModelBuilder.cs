// <copyright file="ModelBuilder.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Modeling
{
    using System;
    using System.Collections.Generic;
    using Bellerophon.Modeling.Diagnostics;
    using Bellerophon.Modeling.Json;
    using Bellerophon.Modeling.Model;

    /// <summary>
    /// Builds the semantic model from parsed JSON: resolves datatypes, applies defaults, adds the implicit columns, puts
    /// everything in canonical order, and validates.
    /// </summary>
    internal sealed class ModelBuilder
    {
        /// <summary>
        /// The implicit tenant column on every table.
        /// </summary>
        public const string TenantColumn = "TenantId";

        /// <summary>
        /// The implicit row-version column on every durable table.
        /// </summary>
        public const string RowVersionColumn = "RowVersion";

        /// <summary>
        /// The built-in table of the tenant's users.
        /// </summary>
        public const string UserTable = "User";

        /// <summary>
        /// The scope that authorizes reading the built-in User table.
        /// </summary>
        public const string UsersReadScope = "Users.Read";

        /// <summary>
        /// The scope that authorizes writing the built-in User table, which grants access to the tenant.
        /// </summary>
        public const string UsersWriteScope = "Users.Write";

        /// <summary>
        /// The CLR type of strings, which need a maximum length in keys and unique constraints.
        /// </summary>
        public const string StringType = "System.String";

        /// <summary>
        /// The CLR type of decimals, which need precision and scale.
        /// </summary>
        public const string DecimalType = "System.Decimal";

        /// <summary>
        /// The CLR type of guids, which the server generates for single-column keys.
        /// </summary>
        public const string GuidType = "System.Guid";

        private static readonly string[] TopLevelProperties = { "$schema", "access", "datatypes", "name", "namespace", "tables", "version" };

        private static readonly string[] DataTypeProperties = { "maxLength", "precision", "scale", "type" };

        private static readonly string[] TableProperties =
        {
            "access", "columns", "concurrency", "description", "facts", "key", "persistence", "plural", "references", "sync", "unique", "writeMode",
        };

        private static readonly string[] ColumnProperties =
        {
            "default", "description", "immutable", "maxLength", "precision", "scale", "type",
        };

        private static readonly string[] ReferenceProperties = { "columns", "onDelete", "table" };

        private static readonly string[] UniqueProperties = { "columns", "idempotent" };

        private static readonly string[] AccessProperties = { "read", "write" };

        /// <summary>
        /// The model file's path.
        /// </summary>
        private readonly string path;

        /// <summary>
        /// The diagnostics found so far.
        /// </summary>
        private readonly List<ModelDiagnostic> diagnostics = new List<ModelDiagnostic>();

        /// <summary>
        /// Initializes a new instance of the <see cref="ModelBuilder"/> class.
        /// </summary>
        /// <param name="path">The model file's path.</param>
        public ModelBuilder(string path)
        {
            this.path = path;
        }

        /// <summary>
        /// Gets the canonical ordering of names: alphabetical ignoring case, with an ordinal tie-break so the order is total.
        /// </summary>
        internal static Comparison<string> CanonicalOrder { get; } = (left, right) =>
        {
            var result = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            return result != 0 ? result : string.CompareOrdinal(left, right);
        };

        /// <summary>
        /// Builds the semantic model.
        /// </summary>
        /// <param name="root">The parsed JSON.</param>
        /// <returns>The model and its diagnostics.</returns>
        public ModelLoadResult Build(JsonNode root)
        {
            var rootObject = this.ExpectObject(root, "The model");
            if (rootObject == null)
            {
                return this.Result(null);
            }

            this.CheckProperties(rootObject, TopLevelProperties, "the model");
            var modelName = this.RequiredString(rootObject, "name", rootObject.Location);
            if (modelName != null)
            {
                this.CheckIdentifier(modelName, rootObject.Find("name")!.Value.Location);
            }

            var version = this.RequiredString(rootObject, "version", rootObject.Location);
            if (version != null && !IsVersion(version))
            {
                this.Report(DiagnosticIds.InvalidValue, $"'{version}' is not a version; use major.minor, for example 1.0.", rootObject.Find("version")!.Value.Location);
                version = null;
            }

            var modelNamespace = this.RequiredString(rootObject, "namespace", rootObject.Location);
            if (modelNamespace != null && !IsQualifiedIdentifier(modelNamespace))
            {
                this.Report(DiagnosticIds.InvalidIdentifier, $"'{modelNamespace}' is not a valid namespace.", rootObject.Find("namespace")!.Value.Location);
            }

            var defaultAccess = this.ParseAccess(rootObject.Find("access"));
            var dataTypes = this.ParseDataTypes(rootObject.Find("datatypes"));
            var drafts = this.ParseTables(rootObject, dataTypes);
            var tables = this.Resolve(drafts, defaultAccess);
            if (modelName != null)
            {
                foreach (var table in tables)
                {
                    if (table.Name == modelName)
                    {
                        this.Report(DiagnosticIds.NameConflict, $"The model's name '{modelName}' is also a table name; the generated root class would collide with the row type.", rootObject.Find("name")!.Value.Location);
                    }
                }
            }

            this.CheckMemberNames(tables, drafts);

            var model = new DataModel(
                modelName ?? string.Empty,
                modelNamespace ?? string.Empty,
                version ?? "0.0",
                defaultAccess.Read != null && defaultAccess.Write != null ? new AccessModel(Array(defaultAccess.Read), Array(defaultAccess.Write)) : null,
                new EquatableArray<DataTypeModel>(Sorted(dataTypes.Values, dataType => dataType.Name)),
                new EquatableArray<TableModel>(tables));
            return this.Result(model);
        }

        /// <summary>
        /// Determines whether text is a valid identifier (letters, digits, and underscores, not starting with a digit).
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns><c>true</c> if it is an identifier.</returns>
        internal static bool IsIdentifier(string text)
        {
            if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
            {
                return false;
            }

            for (var index = 1; index < text.Length; index++)
            {
                if (!(char.IsLetterOrDigit(text[index]) || text[index] == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks that the generated names cannot collide: each row's columns, parent properties, and child properties are
        /// distinct, and each table's plural (a member of the partition) is distinct.
        /// </summary>
        /// <param name="tables">The built tables.</param>
        /// <param name="drafts">The drafts, for locations.</param>
        private void CheckMemberNames(List<TableModel> tables, List<TableDraft> drafts)
        {
            var anchors = new Dictionary<string, SourceLocation>(StringComparer.Ordinal);
            foreach (var draft in drafts)
            {
                anchors[draft.Name] = draft.Anchor;
            }

            var members = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                var names = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var column in table.Columns)
                {
                    names[column.Name] = $"column '{column.Name}'";
                }

                members[table.Name] = names;
            }

            void Add(TableModel owner, string name, string what, string reportedOn)
            {
                if (!members.TryGetValue(owner.Name, out var names))
                {
                    return;
                }

                if (names.TryGetValue(name, out var existing) || name == owner.Name)
                {
                    this.Report(
                        DiagnosticIds.MemberCollision,
                        $"Row '{owner.Name}' would have two members named '{name}': {existing ?? "the type's own name"} and {what}. Rename the reference or set the table's \"plural\".",
                        anchors.TryGetValue(reportedOn, out var location) ? location : SourceLocation.None);
                    return;
                }

                names[name] = what;
            }

            foreach (var child in tables)
            {
                foreach (var reference in child.References)
                {
                    Add(child, reference.Name, $"the reference '{reference.Name}'", child.Name);
                }
            }

            foreach (var child in tables)
            {
                foreach (var reference in child.References)
                {
                    foreach (var parent in tables)
                    {
                        if (parent.Name == reference.Table)
                        {
                            Add(parent, Navigation.ChildMemberName(child, reference), $"the children through '{child.Name}.{reference.Name}'", child.Name);
                        }
                    }
                }
            }

            var plurals = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                if (plurals.TryGetValue(table.Plural, out var other))
                {
                    this.Report(
                        DiagnosticIds.MemberCollision,
                        $"Tables '{other}' and '{table.Name}' have the same plural '{table.Plural}'; set a different \"plural\" on one of them.",
                        anchors.TryGetValue(table.Name, out var location) ? location : SourceLocation.None);
                }
                else
                {
                    plurals[table.Plural] = table.Name;
                }
            }
        }

        /// <summary>
        /// Determines whether text is a <c>major.minor</c> version.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns><c>true</c> if it is two non-negative integers separated by a dot.</returns>
        private static bool IsVersion(string text)
        {
            var parts = text.Split('.');
            if (parts.Length != 2)
            {
                return false;
            }

            foreach (var part in parts)
            {
                if (part.Length == 0 || part.Length > 9)
                {
                    return false;
                }

                foreach (var character in part)
                {
                    if (character < '0' || character > '9')
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Determines whether text is a dotted sequence of identifiers.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns><c>true</c> if every segment is an identifier.</returns>
        private static bool IsQualifiedIdentifier(string text)
        {
            foreach (var segment in text.Split('.'))
            {
                if (!IsIdentifier(segment))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Determines whether text is a full CLR type name: a qualified identifier, optionally followed by <c>[]</c>.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns><c>true</c> if it is a type name.</returns>
        private static bool IsClrTypeName(string text)
        {
            var name = text.EndsWith("[]", StringComparison.Ordinal) ? text.Substring(0, text.Length - 2) : text;
            return name.IndexOf('.') > 0 && IsQualifiedIdentifier(name);
        }

        /// <summary>
        /// Determines whether text is a valid OAuth scope: non-empty, with no whitespace (scopes are space-separated in tokens).
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns><c>true</c> if it is a scope.</returns>
        private static bool IsScope(string text)
        {
            if (text.Length == 0)
            {
                return false;
            }

            foreach (var character in text)
            {
                if (char.IsWhiteSpace(character) || character == '"' || character == '\\')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Describes a JSON value for a diagnostic message.
        /// </summary>
        /// <param name="node">The value.</param>
        /// <returns>The description.</returns>
        private static string Describe(JsonNode node) => node switch
        {
            JsonString text => $"'{text.Value}'",
            JsonNumber number => number.Text,
            JsonBoolean flag => flag.Value ? "true" : "false",
            _ => node.Kind,
        };

        /// <summary>
        /// Copies a list into an equatable array.
        /// </summary>
        private static EquatableArray<string> Array(List<string> items) => new EquatableArray<string>(items);

        /// <summary>
        /// Sorts items into canonical order by name.
        /// </summary>
        private static List<T> Sorted<T>(IEnumerable<T> items, Func<T, string> name)
        {
            var list = new List<T>(items);
            list.Sort((left, right) => CanonicalOrder(name(left), name(right)));
            return list;
        }

        /// <summary>
        /// Parses the datatype dictionary.
        /// </summary>
        /// <param name="property">The <c>datatypes</c> property.</param>
        /// <returns>The entries, by name.</returns>
        private Dictionary<string, DataTypeModel> ParseDataTypes(JsonProperty? property)
        {
            var dataTypes = new Dictionary<string, DataTypeModel>(StringComparer.Ordinal);
            if (property == null)
            {
                return dataTypes;
            }

            var dataTypesObject = this.ExpectObject(property.Value, "'datatypes'");
            if (dataTypesObject == null)
            {
                return dataTypes;
            }

            this.CheckDuplicates(dataTypesObject, "datatype");
            foreach (var entry in dataTypesObject.Properties)
            {
                if (dataTypes.ContainsKey(entry.Name))
                {
                    continue;
                }

                this.CheckIdentifier(entry.Name, entry.NameLocation);
                string? clrType = null;
                int? maxLength = null;
                int? precision = null;
                int? scale = null;
                var typeLocation = entry.Value.Location;
                if (entry.Value is JsonString shorthand)
                {
                    clrType = shorthand.Value;
                }
                else if (entry.Value is JsonObject entryObject)
                {
                    this.CheckProperties(entryObject, DataTypeProperties, $"datatype '{entry.Name}'");
                    clrType = this.RequiredString(entryObject, "type", entry.NameLocation);
                    typeLocation = entryObject.Find("type")?.Value.Location ?? typeLocation;
                    maxLength = this.OptionalPositiveInteger(entryObject, "maxLength");
                    precision = this.OptionalPositiveInteger(entryObject, "precision");
                    scale = this.OptionalNonNegativeInteger(entryObject, "scale");
                }
                else
                {
                    this.Report(DiagnosticIds.UnexpectedType, $"Expected datatype '{entry.Name}' to be a CLR type name or an object, but found {entry.Value.Kind}.", entry.Value.Location);
                    continue;
                }

                if (clrType == null)
                {
                    continue;
                }

                if (clrType.EndsWith("?", StringComparison.Ordinal))
                {
                    this.Report(DiagnosticIds.InvalidValue, $"Datatype '{entry.Name}' cannot be nullable; nullability belongs to the column (\"{entry.Name}?\").", typeLocation);
                    continue;
                }

                if (!IsClrTypeName(clrType))
                {
                    this.Report(DiagnosticIds.InvalidValue, $"'{clrType}' is not a full CLR type name, such as System.Guid.", typeLocation);
                    continue;
                }

                dataTypes[entry.Name] = new DataTypeModel(entry.Name, clrType, maxLength, precision, scale, typeLocation);
            }

            return dataTypes;
        }

        /// <summary>
        /// Parses every table, applying the checks that need only the table itself.
        /// </summary>
        /// <param name="rootObject">The model's root object.</param>
        /// <param name="dataTypes">The datatype dictionary.</param>
        /// <returns>The drafts, in canonical order.</returns>
        private List<TableDraft> ParseTables(JsonObject rootObject, Dictionary<string, DataTypeModel> dataTypes)
        {
            var drafts = new List<TableDraft>();
            var tablesProperty = rootObject.Find("tables");
            if (tablesProperty == null)
            {
                this.Report(DiagnosticIds.MissingProperty, "The model is missing the required property 'tables'.", rootObject.Location);
                return drafts;
            }

            var tablesObject = this.ExpectObject(tablesProperty.Value, "'tables'");
            if (tablesObject == null)
            {
                return drafts;
            }

            this.CheckDuplicates(tablesObject, "table");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tableProperty in tablesObject.Properties)
            {
                if (!seen.Add(tableProperty.Name))
                {
                    continue;
                }

                if (tableProperty.Name == UserTable)
                {
                    this.Report(DiagnosticIds.ReservedName, $"'{UserTable}' is built in: the tenant's users, bound to identities. Put domain columns in a table that references it.", tableProperty.NameLocation);
                    continue;
                }

                this.CheckIdentifier(tableProperty.Name, tableProperty.NameLocation);
                var tableObject = this.ExpectObject(tableProperty.Value, $"Table '{tableProperty.Name}'");
                if (tableObject != null)
                {
                    drafts.Add(this.ParseTable(tableProperty, tableObject, dataTypes));
                }
            }

            drafts.Add(BuiltInUser());
            return Sorted(drafts, draft => draft.Name);
        }

        /// <summary>
        /// Builds the built-in User table: the tenant's security principals, each bound to an identity by issuer and
        /// subject.  Its shape is fixed so access control never depends on how a model was written.
        /// </summary>
        /// <returns>The draft.</returns>
        private static TableDraft BuiltInUser()
        {
            var anchor = SourceLocation.None;
            var draft = new TableDraft(UserTable, anchor)
            {
                Access = new AccessDraft { Read = new List<string> { UsersReadScope }, Write = new List<string> { UsersWriteScope } },
                Description = "A user of the tenant: a security principal bound to an identity in the identity provider.",
                IsBuiltIn = true,
                HasFacts = false,
                Principal = new PrincipalModel("IdentityIssuer", "IdentitySubject"),
            };

            draft.Columns.Add(new ColumnDraft("IdentityIssuer", anchor) { ClrType = StringType, MaxLength = 256, Description = "The identity provider that issued the identity." });
            draft.Columns.Add(new ColumnDraft("IdentitySubject", anchor) { ClrType = StringType, MaxLength = 128, Description = "The identity's subject; with Microsoft Entra, the oid claim." });
            draft.Columns.Add(new ColumnDraft("UserId", anchor) { ClrType = GuidType });
            draft.Key.Add("UserId");
            var identity = new UniqueDraft("Identity", anchor);
            identity.Columns.Add("IdentityIssuer");
            identity.Columns.Add("IdentitySubject");
            draft.Uniques.Add(identity);
            return draft;
        }

        /// <summary>
        /// Parses one table.
        /// </summary>
        private TableDraft ParseTable(JsonProperty tableProperty, JsonObject tableObject, Dictionary<string, DataTypeModel> dataTypes)
        {
            var name = tableProperty.Name;
            var anchor = tableProperty.NameLocation;
            this.CheckProperties(tableObject, TableProperties, $"table '{name}'");

            var draft = new TableDraft(name, anchor)
            {
                Access = this.ParseAccess(tableObject.Find("access")),
                Concurrency = this.ParseOption(tableObject, "concurrency", ConcurrencyMode.Locked),
                ConcurrencyLocation = tableObject.Find("concurrency")?.Value.Location ?? anchor,
                Description = this.OptionalString(tableObject, "description"),
                Persistence = this.ParseOption(tableObject, "persistence", Persistence.Durable),
                Plural = this.OptionalString(tableObject, "plural"),
                IsSynced = this.OptionalBoolean(tableObject, "sync") ?? true,
                HasFacts = this.OptionalBoolean(tableObject, "facts") ?? true,
                WriteMode = this.ParseOption(tableObject, "writeMode", WriteMode.Mutable),
            };

            if (draft.Plural != null)
            {
                this.CheckIdentifier(draft.Plural, tableObject.Find("plural")!.Value.Location);
            }

            this.ParseColumns(draft, tableObject, dataTypes);
            this.ParseKey(draft, tableObject);
            this.ParseReferences(draft, tableObject);
            this.ParseUniques(draft, tableObject);
            return draft;
        }

        /// <summary>
        /// Parses a table's columns.
        /// </summary>
        private void ParseColumns(TableDraft draft, JsonObject tableObject, Dictionary<string, DataTypeModel> dataTypes)
        {
            var columnsProperty = tableObject.Find("columns");
            if (columnsProperty == null)
            {
                this.Report(DiagnosticIds.MissingProperty, $"Table '{draft.Name}' is missing the required property 'columns'.", draft.Anchor);
                return;
            }

            var columnsObject = this.ExpectObject(columnsProperty.Value, $"The columns of table '{draft.Name}'");
            if (columnsObject == null)
            {
                return;
            }

            this.CheckDuplicates(columnsObject, "column");
            foreach (var columnProperty in columnsObject.Properties)
            {
                var columnName = columnProperty.Name;
                if (draft.FindColumn(columnName) != null)
                {
                    continue;
                }

                this.CheckIdentifier(columnName, columnProperty.NameLocation);
                if (columnName == TenantColumn || columnName == RowVersionColumn)
                {
                    this.Report(DiagnosticIds.ReservedName, $"'{columnName}' is added to every table automatically and cannot be declared.", columnProperty.NameLocation);
                    continue;
                }

                var column = new ColumnDraft(columnName, columnProperty.NameLocation);
                string? typeText = null;
                var typeLocation = columnProperty.Value.Location;
                if (columnProperty.Value is JsonString shorthand)
                {
                    typeText = shorthand.Value;
                }
                else if (columnProperty.Value is JsonObject columnObject)
                {
                    this.CheckProperties(columnObject, ColumnProperties, $"column '{draft.Name}.{columnName}'");
                    typeText = this.RequiredString(columnObject, "type", columnProperty.NameLocation);
                    typeLocation = columnObject.Find("type")?.Value.Location ?? typeLocation;
                    column.DefaultValue = this.OptionalScalarText(columnObject, "default");
                    column.Description = this.OptionalString(columnObject, "description");
                    column.IsImmutable = this.OptionalBoolean(columnObject, "immutable") ?? false;
                    column.MaxLength = this.OptionalPositiveInteger(columnObject, "maxLength");
                    column.Precision = this.OptionalPositiveInteger(columnObject, "precision");
                    column.Scale = this.OptionalNonNegativeInteger(columnObject, "scale");
                }
                else
                {
                    this.Report(DiagnosticIds.UnexpectedType, $"Expected column '{draft.Name}.{columnName}' to be a datatype name or an object, but found {columnProperty.Value.Kind}.", columnProperty.Value.Location);
                    continue;
                }

                if (typeText == null)
                {
                    continue;
                }

                column.IsNullable = typeText.EndsWith("?", StringComparison.Ordinal);
                var dataTypeName = column.IsNullable ? typeText.Substring(0, typeText.Length - 1) : typeText;
                if (!dataTypes.TryGetValue(dataTypeName, out var dataType))
                {
                    this.Report(DiagnosticIds.UnknownDataType, $"Datatype '{dataTypeName}' is not defined in 'datatypes'.", typeLocation);
                    continue;
                }

                column.DataType = dataType.Name;
                column.ClrType = dataType.ClrType;
                column.MaxLength ??= dataType.MaxLength;
                column.Precision ??= dataType.Precision;
                column.Scale ??= dataType.Scale;
                draft.Columns.Add(column);
            }
        }

        /// <summary>
        /// Parses a table's key.
        /// </summary>
        private void ParseKey(TableDraft draft, JsonObject tableObject)
        {
            var keyProperty = tableObject.Find("key");
            if (keyProperty == null)
            {
                this.Report(DiagnosticIds.MissingProperty, $"Table '{draft.Name}' is missing the required property 'key'.", draft.Anchor);
                return;
            }

            draft.KeyLocation = keyProperty.Value.Location;
            foreach (var (columnName, location) in this.ColumnList(keyProperty, draft, $"The key of table '{draft.Name}'"))
            {
                if (draft.FindColumn(columnName)!.IsNullable)
                {
                    this.Report(DiagnosticIds.InvalidPolicy, $"Key column '{draft.Name}.{columnName}' cannot be nullable.", location);
                }

                draft.Key.Add(columnName);
            }
        }

        /// <summary>
        /// Parses a table's references.
        /// </summary>
        private void ParseReferences(TableDraft draft, JsonObject tableObject)
        {
            var referencesProperty = tableObject.Find("references");
            if (referencesProperty == null)
            {
                return;
            }

            var referencesObject = this.ExpectObject(referencesProperty.Value, $"The references of table '{draft.Name}'");
            if (referencesObject == null)
            {
                return;
            }

            this.CheckDuplicates(referencesObject, "reference");
            foreach (var referenceProperty in referencesObject.Properties)
            {
                this.CheckIdentifier(referenceProperty.Name, referenceProperty.NameLocation);
                var referenceObject = this.ExpectObject(referenceProperty.Value, $"Reference '{draft.Name}.{referenceProperty.Name}'");
                if (referenceObject == null)
                {
                    continue;
                }

                this.CheckProperties(referenceObject, ReferenceProperties, $"reference '{draft.Name}.{referenceProperty.Name}'");
                var reference = new ReferenceDraft(referenceProperty.Name, referenceProperty.NameLocation)
                {
                    OnDelete = this.ParseOption(referenceObject, "onDelete", DeleteRule.Block),
                    OnDeleteLocation = referenceObject.Find("onDelete")?.Value.Location ?? referenceProperty.NameLocation,
                    Table = this.OptionalString(referenceObject, "table") ?? referenceProperty.Name,
                    TableLocation = referenceObject.Find("table")?.Value.Location ?? referenceProperty.NameLocation,
                };

                var columnsProperty = referenceObject.Find("columns");
                if (columnsProperty == null)
                {
                    this.Report(DiagnosticIds.MissingProperty, $"Reference '{draft.Name}.{reference.Name}' is missing the required property 'columns'.", referenceProperty.NameLocation);
                }
                else
                {
                    reference.ColumnsLocation = columnsProperty.Value.Location;
                    foreach (var (columnName, _) in this.ColumnList(columnsProperty, draft, $"The columns of reference '{draft.Name}.{reference.Name}'"))
                    {
                        reference.Columns.Add(columnName);
                    }
                }

                draft.References.Add(reference);
            }
        }

        /// <summary>
        /// Parses a table's unique constraints.
        /// </summary>
        private void ParseUniques(TableDraft draft, JsonObject tableObject)
        {
            var uniqueProperty = tableObject.Find("unique");
            if (uniqueProperty == null)
            {
                return;
            }

            var uniqueObject = this.ExpectObject(uniqueProperty.Value, $"The unique constraints of table '{draft.Name}'");
            if (uniqueObject == null)
            {
                return;
            }

            this.CheckDuplicates(uniqueObject, "unique constraint");
            foreach (var constraintProperty in uniqueObject.Properties)
            {
                this.CheckIdentifier(constraintProperty.Name, constraintProperty.NameLocation);
                var constraintObject = this.ExpectObject(constraintProperty.Value, $"Unique constraint '{draft.Name}.{constraintProperty.Name}'");
                if (constraintObject == null)
                {
                    continue;
                }

                this.CheckProperties(constraintObject, UniqueProperties, $"unique constraint '{draft.Name}.{constraintProperty.Name}'");
                var unique = new UniqueDraft(constraintProperty.Name, constraintProperty.NameLocation)
                {
                    IsIdempotent = this.OptionalBoolean(constraintObject, "idempotent") ?? false,
                };

                var columnsProperty = constraintObject.Find("columns");
                if (columnsProperty == null)
                {
                    this.Report(DiagnosticIds.MissingProperty, $"Unique constraint '{draft.Name}.{constraintProperty.Name}' is missing the required property 'columns'.", constraintProperty.NameLocation);
                }
                else
                {
                    unique.ColumnsLocation = columnsProperty.Value.Location;
                    foreach (var (columnName, _) in this.ColumnList(columnsProperty, draft, $"The columns of unique constraint '{draft.Name}.{constraintProperty.Name}'"))
                    {
                        unique.Columns.Add(columnName);
                    }
                }

                draft.Uniques.Add(unique);
            }
        }

        /// <summary>
        /// Resolves cross-table rules and builds the final tables.
        /// </summary>
        private List<TableModel> Resolve(List<TableDraft> drafts, AccessDraft defaultAccess)
        {
            var draftsByName = new Dictionary<string, TableDraft>(StringComparer.Ordinal);
            foreach (var draft in drafts)
            {
                draftsByName[draft.Name] = draft;
            }

            var tables = new List<TableModel>();
            foreach (var draft in drafts)
            {
                foreach (var reference in draft.References)
                {
                    this.ResolveReference(draft, reference, draftsByName);
                }

                this.CheckSizes(draft);

                if (draft.Concurrency == ConcurrencyMode.LatestValue)
                {
                    if (draft.Persistence != Persistence.Ephemeral)
                    {
                        this.Report(DiagnosticIds.InvalidPolicy, $"Table '{draft.Name}' uses LatestValue concurrency, which requires Ephemeral persistence.", draft.ConcurrencyLocation);
                    }

                    if (draft.References.Count > 0 || draft.Uniques.Count > 0)
                    {
                        this.Report(DiagnosticIds.InvalidPolicy, $"LatestValue table '{draft.Name}' is a lock-free store looked up by key; it cannot have references or unique constraints.", draft.ConcurrencyLocation);
                    }
                }

                foreach (var reference in draft.References)
                {
                    if (draftsByName.TryGetValue(reference.Table, out var parent) && parent.Concurrency == ConcurrencyMode.LatestValue)
                    {
                        this.Report(DiagnosticIds.InvalidPolicy, $"Table '{draft.Name}' cannot reference LatestValue table '{parent.Name}'; look its rows up by key instead.", reference.NameLocation);
                    }
                }

                var read = draft.Access.Read ?? defaultAccess.Read;
                var write = draft.Access.Write ?? defaultAccess.Write;
                if (read == null || write == null)
                {
                    var missing = read == null && write == null ? "reads or writes" : read == null ? "reads" : "writes";
                    this.Report(DiagnosticIds.MissingAccess, $"Table '{draft.Name}' has no access for {missing}; declare 'access' at the top level or on the table.", draft.Anchor);
                }

                tables.Add(this.Finish(draft, new AccessModel(Array(read ?? new List<string>()), Array(write ?? new List<string>()))));
            }

            return tables;
        }

        /// <summary>
        /// Validates one reference against its parent table.
        /// </summary>
        private void ResolveReference(TableDraft draft, ReferenceDraft reference, Dictionary<string, TableDraft> draftsByName)
        {
            if (!draftsByName.TryGetValue(reference.Table, out var parent))
            {
                this.Report(DiagnosticIds.UnknownTable, $"Reference '{draft.Name}.{reference.Name}' names table '{reference.Table}', which is not declared.", reference.TableLocation);
                return;
            }

            if (reference.Columns.Count != parent.Key.Count)
            {
                this.Report(
                    DiagnosticIds.ReferenceArity,
                    $"Reference '{draft.Name}.{reference.Name}' has {reference.Columns.Count} column(s), but the key of table '{parent.Name}' has {parent.Key.Count}.",
                    reference.ColumnsLocation);
            }
            else
            {
                for (var index = 0; index < reference.Columns.Count; index++)
                {
                    var childColumn = draft.FindColumn(reference.Columns[index]);
                    var parentColumn = parent.FindColumn(parent.Key[index]);
                    if (childColumn != null && parentColumn != null && childColumn.ClrType != parentColumn.ClrType)
                    {
                        this.Report(
                            DiagnosticIds.ReferenceType,
                            $"Column '{draft.Name}.{childColumn.Name}' ({childColumn.ClrType}) does not match key column '{parent.Name}.{parentColumn.Name}' ({parentColumn.ClrType}).",
                            reference.ColumnsLocation);
                    }
                }
            }

            if (draft.Persistence == Persistence.Durable && parent.Persistence == Persistence.Ephemeral)
            {
                this.Report(DiagnosticIds.DurableReferencesEphemeral, $"Durable table '{draft.Name}' cannot reference ephemeral table '{parent.Name}'; the reference would dangle after a restart.", reference.NameLocation);
            }

            if (draft.IsSynced && !parent.IsSynced)
            {
                this.Report(DiagnosticIds.SyncedReferencesUnsynced, $"Table '{draft.Name}' is synced to clients but references '{parent.Name}', which is not; the client could not resolve the reference.", reference.NameLocation);
            }

            if (draft.WriteMode == WriteMode.AppendOnly && reference.OnDelete == DeleteRule.Cascade)
            {
                this.Report(DiagnosticIds.InvalidPolicy, $"Append-only table '{draft.Name}' must use onDelete Block.", reference.OnDeleteLocation);
            }
        }

        /// <summary>
        /// Enforces the size rules: decimals declare precision and scale; strings in keys and unique constraints declare a maximum length.
        /// </summary>
        private void CheckSizes(TableDraft draft)
        {
            foreach (var column in draft.Columns)
            {
                if (column.ClrType == DecimalType && (column.Precision == null || column.Scale == null))
                {
                    this.Report(DiagnosticIds.SizeRule, $"Decimal column '{draft.Name}.{column.Name}' must declare precision and scale, on its datatype or on the column.", column.NameLocation);
                }
            }

            void CheckStrings(IEnumerable<string> columns, SourceLocation location, string what)
            {
                foreach (var columnName in columns)
                {
                    var column = draft.FindColumn(columnName);
                    if (column != null && column.ClrType == StringType && column.MaxLength == null)
                    {
                        this.Report(DiagnosticIds.SizeRule, $"String column '{draft.Name}.{columnName}' is part of {what} and must declare maxLength.", location);
                    }
                }
            }

            CheckStrings(draft.Key, draft.KeyLocation, "the key");
            foreach (var unique in draft.Uniques)
            {
                CheckStrings(unique.Columns, unique.ColumnsLocation, $"unique constraint '{unique.Name}'");
            }
        }

        /// <summary>
        /// Builds the final table: canonical order, key rules, key-generation defaults, and the implicit columns.
        /// </summary>
        private TableModel Finish(TableDraft draft, AccessModel access)
        {
            var keyColumns = new HashSet<string>(draft.Key, StringComparer.Ordinal);
            var columns = new List<ColumnModel>();
            foreach (var column in Sorted(draft.Columns, column => column.Name))
            {
                var isKey = keyColumns.Contains(column.Name);
                columns.Add(new ColumnModel(
                    column.Name,
                    column.DataType,
                    column.ClrType,
                    column.IsNullable,
                    column.MaxLength,
                    column.Precision,
                    column.Scale,
                    column.DefaultValue,
                    column.IsImmutable || isKey,
                    IsImplicit: false,
                    column.Description));
            }

            columns.Add(new ColumnModel(TenantColumn, string.Empty, GuidType, false, null, null, null, null, true, true, "The tenant that owns the row."));
            if (draft.Persistence == Persistence.Durable)
            {
                columns.Add(new ColumnModel(RowVersionColumn, string.Empty, "System.Int64", false, null, null, null, null, false, true, "The sequence number of the row's last change."));
            }

            var references = new List<ReferenceModel>();
            foreach (var reference in Sorted(draft.References, reference => reference.Name))
            {
                references.Add(new ReferenceModel(reference.Name, reference.Table, Array(reference.Columns), reference.OnDelete));
            }

            var uniques = new List<UniqueModel>();
            foreach (var unique in Sorted(draft.Uniques, unique => unique.Name))
            {
                uniques.Add(new UniqueModel(unique.Name, Array(unique.Columns), unique.IsIdempotent));
            }

            return new TableModel(
                draft.Name,
                Array(draft.Key),
                new EquatableArray<ColumnModel>(columns),
                new EquatableArray<ReferenceModel>(references),
                new EquatableArray<UniqueModel>(uniques),
                access,
                draft.IsSynced,
                draft.Principal,
                draft.Persistence,
                draft.WriteMode,
                draft.Concurrency,
                draft.Description,
                draft.IsBuiltIn,
                draft.Plural ?? Navigation.Plural(draft.Name),
                draft.HasFacts);
        }

        /// <summary>
        /// Parses an access declaration: optional <c>read</c> and <c>write</c> arrays of scopes.
        /// </summary>
        private AccessDraft ParseAccess(JsonProperty? property)
        {
            var access = new AccessDraft();
            if (property == null)
            {
                return access;
            }

            var accessObject = this.ExpectObject(property.Value, "'access'");
            if (accessObject == null)
            {
                return access;
            }

            this.CheckProperties(accessObject, AccessProperties, "'access'");
            access.Read = this.ScopeList(accessObject.Find("read"));
            access.Write = this.ScopeList(accessObject.Find("write"));
            return access;
        }

        /// <summary>
        /// Reads an array of OAuth scopes.
        /// </summary>
        private List<string>? ScopeList(JsonProperty? property)
        {
            if (property == null)
            {
                return null;
            }

            if (!(property.Value is JsonArray array) || array.Items.Count == 0)
            {
                this.Report(DiagnosticIds.InvalidValue, $"'{property.Name}' must be a non-empty array of scope names.", property.Value.Location);
                return null;
            }

            var scopes = new List<string>();
            foreach (var item in array.Items)
            {
                if (item is JsonString scope && IsScope(scope.Value))
                {
                    scopes.Add(scope.Value);
                }
                else
                {
                    this.Report(DiagnosticIds.InvalidValue, $"Invalid scope {Describe(item)}; a scope is a non-empty name without spaces.", item.Location);
                }
            }

            return scopes;
        }

        /// <summary>
        /// Reads an array of column names and checks each against the table.
        /// </summary>
        private List<(string Name, SourceLocation Location)> ColumnList(JsonProperty property, TableDraft draft, string what)
        {
            var columns = new List<(string Name, SourceLocation Location)>();
            if (!(property.Value is JsonArray array))
            {
                this.Report(DiagnosticIds.UnexpectedType, $"{what} must be an array of column names, but found {property.Value.Kind}.", property.Value.Location);
                return columns;
            }

            if (array.Items.Count == 0)
            {
                this.Report(DiagnosticIds.InvalidValue, $"{what} must name at least one column.", array.Location);
            }

            foreach (var item in array.Items)
            {
                if (!(item is JsonString columnName))
                {
                    this.Report(DiagnosticIds.UnexpectedType, $"Expected a column name (a string), but found {item.Kind}.", item.Location);
                    continue;
                }

                if (columnName.Value == TenantColumn)
                {
                    this.Report(DiagnosticIds.ReservedName, $"'{TenantColumn}' is part of every key and reference automatically and cannot be listed.", columnName.Location);
                    continue;
                }

                if (draft.FindColumn(columnName.Value) == null)
                {
                    this.Report(DiagnosticIds.UnknownColumn, $"Column '{columnName.Value}' is not declared in table '{draft.Name}'.", columnName.Location);
                    continue;
                }

                columns.Add((columnName.Value, columnName.Location));
            }

            return columns;
        }

        /// <summary>
        /// Parses an enumeration-valued option such as <c>onDelete</c>.
        /// </summary>
        private T ParseOption<T>(JsonObject owner, string name, T defaultValue)
            where T : struct, Enum
        {
            var property = owner.Find(name);
            if (property == null)
            {
                return defaultValue;
            }

            if (property.Value is JsonString text && IsIdentifier(text.Value) && Enum.TryParse(text.Value, false, out T value))
            {
                return value;
            }

            this.Report(
                DiagnosticIds.InvalidValue,
                $"Invalid value {Describe(property.Value)} for '{name}'. Expected one of: {string.Join(", ", Enum.GetNames(typeof(T)))}.",
                property.Value.Location);
            return defaultValue;
        }

        private string? RequiredString(JsonObject owner, string name, SourceLocation anchor)
        {
            if (owner.Find(name) == null)
            {
                this.Report(DiagnosticIds.MissingProperty, $"Missing required property '{name}'.", anchor);
                return null;
            }

            return this.OptionalString(owner, name);
        }

        private string? OptionalString(JsonObject owner, string name)
        {
            var property = owner.Find(name);
            if (property == null)
            {
                return null;
            }

            if (property.Value is JsonString text)
            {
                return text.Value;
            }

            this.Report(DiagnosticIds.UnexpectedType, $"Expected '{name}' to be a string, but found {property.Value.Kind}.", property.Value.Location);
            return null;
        }

        private bool? OptionalBoolean(JsonObject owner, string name)
        {
            var property = owner.Find(name);
            if (property == null)
            {
                return null;
            }

            if (property.Value is JsonBoolean flag)
            {
                return flag.Value;
            }

            this.Report(DiagnosticIds.UnexpectedType, $"Expected '{name}' to be true or false, but found {property.Value.Kind}.", property.Value.Location);
            return null;
        }

        private int? OptionalPositiveInteger(JsonObject owner, string name)
        {
            var value = this.OptionalNonNegativeInteger(owner, name);
            if (value == 0)
            {
                this.Report(DiagnosticIds.InvalidValue, $"'{name}' must be greater than zero.", owner.Find(name)!.Value.Location);
                return null;
            }

            return value;
        }

        private int? OptionalNonNegativeInteger(JsonObject owner, string name)
        {
            var property = owner.Find(name);
            if (property == null)
            {
                return null;
            }

            if (property.Value is JsonNumber number && number.TryGetInt32(out var value) && value >= 0)
            {
                return value;
            }

            this.Report(DiagnosticIds.InvalidValue, $"Expected '{name}' to be a non-negative integer, but found {Describe(property.Value)}.", property.Value.Location);
            return null;
        }

        private string? OptionalScalarText(JsonObject owner, string name)
        {
            var property = owner.Find(name);
            switch (property?.Value)
            {
                case null:
                    return null;
                case JsonString text:
                    return text.Value;
                case JsonNumber number:
                    return number.Text;
                case JsonBoolean flag:
                    return flag.Value ? "true" : "false";
                default:
                    this.Report(DiagnosticIds.UnexpectedType, $"Expected '{name}' to be a string, number, or boolean, but found {property.Value.Kind}.", property.Value.Location);
                    return null;
            }
        }

        private JsonObject? ExpectObject(JsonNode node, string what)
        {
            if (node is JsonObject jsonObject)
            {
                return jsonObject;
            }

            this.Report(DiagnosticIds.UnexpectedType, $"{what} must be an object, but found {node.Kind}.", node.Location);
            return null;
        }

        private void CheckProperties(JsonObject owner, string[] allowed, string context)
        {
            var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var property in owner.Properties)
            {
                if (!allowedSet.Contains(property.Name))
                {
                    this.Report(DiagnosticIds.UnknownProperty, $"Unknown property '{property.Name}' in {context}.", property.NameLocation, ModelSeverity.Warning);
                }
            }

            this.CheckDuplicates(owner, "property");
        }

        private void CheckDuplicates(JsonObject owner, string what)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in owner.Properties)
            {
                if (!seen.Add(property.Name))
                {
                    this.Report(DiagnosticIds.DuplicateName, $"The {what} '{property.Name}' is declared more than once.", property.NameLocation);
                }
            }
        }

        private void CheckIdentifier(string name, SourceLocation location)
        {
            if (!IsIdentifier(name))
            {
                this.Report(DiagnosticIds.InvalidIdentifier, $"'{name}' is not a valid identifier; use letters, digits, and underscores, not starting with a digit.", location);
            }
        }

        private void Report(string diagnosticId, string message, SourceLocation location, ModelSeverity severity = ModelSeverity.Error) =>
            this.diagnostics.Add(new ModelDiagnostic(diagnosticId, severity, message, location));

        private ModelLoadResult Result(DataModel? model) =>
            new ModelLoadResult(this.path, model, new EquatableArray<ModelDiagnostic>(this.diagnostics));

        /// <summary>
        /// An access declaration being built; <c>null</c> lists inherit the model default.
        /// </summary>
        private sealed class AccessDraft
        {
            public List<string>? Read { get; set; }

            public List<string>? Write { get; set; }
        }

        /// <summary>
        /// A table being built.
        /// </summary>
        private sealed class TableDraft
        {
            public TableDraft(string name, SourceLocation anchor)
            {
                this.Name = name;
                this.Anchor = anchor;
                this.KeyLocation = anchor;
                this.ConcurrencyLocation = anchor;
            }

            public string Name { get; }

            public SourceLocation Anchor { get; }

            public AccessDraft Access { get; set; } = new AccessDraft();

            public ConcurrencyMode Concurrency { get; set; }

            public SourceLocation ConcurrencyLocation { get; set; }

            public string? Description { get; set; }

            public Persistence Persistence { get; set; }

            public bool IsSynced { get; set; } = true;

            public WriteMode WriteMode { get; set; }

            public List<ColumnDraft> Columns { get; } = new List<ColumnDraft>();

            public List<string> Key { get; } = new List<string>();

            public SourceLocation KeyLocation { get; set; }

            public List<ReferenceDraft> References { get; } = new List<ReferenceDraft>();

            public List<UniqueDraft> Uniques { get; } = new List<UniqueDraft>();

            public PrincipalModel? Principal { get; set; }

            public bool IsBuiltIn { get; set; }

            public bool HasFacts { get; set; }

            public string? Plural { get; set; }

            public ColumnDraft? FindColumn(string name)
            {
                foreach (var column in this.Columns)
                {
                    if (column.Name == name)
                    {
                        return column;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// A column being built.
        /// </summary>
        private sealed class ColumnDraft
        {
            public ColumnDraft(string name, SourceLocation nameLocation)
            {
                this.Name = name;
                this.NameLocation = nameLocation;
            }

            public string Name { get; }

            public SourceLocation NameLocation { get; }

            public string DataType { get; set; } = string.Empty;

            public string ClrType { get; set; } = string.Empty;

            public bool IsNullable { get; set; }

            public int? MaxLength { get; set; }

            public int? Precision { get; set; }

            public int? Scale { get; set; }

            public string? DefaultValue { get; set; }

            public bool IsImmutable { get; set; }


            public string? Description { get; set; }
        }

        /// <summary>
        /// A reference being built.
        /// </summary>
        private sealed class ReferenceDraft
        {
            public ReferenceDraft(string name, SourceLocation nameLocation)
            {
                this.Name = name;
                this.NameLocation = nameLocation;
                this.Table = name;
                this.TableLocation = nameLocation;
                this.ColumnsLocation = nameLocation;
                this.OnDeleteLocation = nameLocation;
            }

            public string Name { get; }

            public SourceLocation NameLocation { get; }

            public string Table { get; set; }

            public SourceLocation TableLocation { get; set; }

            public List<string> Columns { get; } = new List<string>();

            public SourceLocation ColumnsLocation { get; set; }

            public DeleteRule OnDelete { get; set; }

            public SourceLocation OnDeleteLocation { get; set; }
        }

        /// <summary>
        /// A unique constraint being built.
        /// </summary>
        private sealed class UniqueDraft
        {
            public UniqueDraft(string name, SourceLocation nameLocation)
            {
                this.Name = name;
                this.ColumnsLocation = nameLocation;
            }

            public string Name { get; }

            public List<string> Columns { get; } = new List<string>();

            public SourceLocation ColumnsLocation { get; set; }

            public bool IsIdempotent { get; set; }
        }
    }
}
