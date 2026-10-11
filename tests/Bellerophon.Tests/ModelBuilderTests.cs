// <copyright file="ModelBuilderTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System.Linq;
    using Bellerophon.Modeling;
    using Bellerophon.Modeling.Diagnostics;
    using Bellerophon.Modeling.Model;
    using Xunit;

    /// <summary>
    /// Tests for building and validating the semantic model.
    /// </summary>
    public class ModelBuilderTests
    {
        private const string TwoTables = """
            {
              "access": { "read": ["Ledger.Read"], "write": ["Ledger.Write"] },
              "datatypes": {
                "guid": "System.Guid",
                "name": { "maxLength": 100, "type": "System.String" },
                "string": "System.String"
              },
              "name": "Test",
              "namespace": "Test.Model",
              "tables": {
                "Account": {
                  "columns": { "AccountId": "guid" },
                  "key": ["AccountId"],
                  "references": { "Node": { "columns": ["AccountId"] } }
                },
                "Node": {
                  "columns": { "Description": "string?", "Name": "name", "NodeId": "guid" },
                  "key": ["NodeId"]
                }
              },
              "version": "2.1"
            }
            """;

        /// <summary>
        /// The model's name and version are read, and the major version is available for routes.
        /// </summary>
        [Fact]
        public void NameAndVersionAreRead()
        {
            var model = ModelLoader.Load("bellerophon.json", TwoTables).Model!;

            Assert.Equal("Test", model.Name);
            Assert.Equal("2.1", model.Version);
            Assert.Equal(2, model.MajorVersion);
        }

        /// <summary>
        /// A version must be major.minor.
        /// </summary>
        [Fact]
        public void InvalidVersionIsAnError()
        {
            var result = ModelLoader.Load("bellerophon.json", TwoTables.Replace("\"version\": \"2.1\"", "\"version\": \"two\""));

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidValue && diagnostic.Message.Contains("major.minor"));
        }

        /// <summary>
        /// The model's name and version are required.
        /// </summary>
        [Fact]
        public void MissingNameAndVersionAreErrors()
        {
            var text = TwoTables.Replace("\"name\": \"Test\",", string.Empty).Replace(",\n  \"version\": \"2.1\"", string.Empty);

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Id == DiagnosticIds.MissingProperty));
        }

        /// <summary>
        /// The model's name becomes a class, so it cannot match a table, whose row type has the same name.
        /// </summary>
        [Fact]
        public void NameMatchingATableIsAnError()
        {
            var result = ModelLoader.Load("bellerophon.json", TwoTables.Replace("\"name\": \"Test\"", "\"name\": \"Node\""));

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.NameConflict);
        }

        /// <summary>
        /// Every table receives TenantId and RowVersion without declaring them.
        /// </summary>
        [Fact]
        public void EveryTableReceivesTenantIdAndRowVersion()
        {
            var result = ModelLoader.Load("bellerophon.json", TwoTables);

            Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
            foreach (var table in result.Model!.Tables)
            {
                var tenant = table.FindColumn("TenantId")!;
                Assert.True(tenant.IsImplicit);
                Assert.True(tenant.IsImmutable);
                Assert.Equal("System.Guid", tenant.ClrType);
                Assert.Equal("System.Int64", table.FindColumn("RowVersion")!.ClrType);
            }
        }

        /// <summary>
        /// A trailing question mark makes a column nullable; datatype constraints flow to the column.
        /// </summary>
        [Fact]
        public void NullabilityAndDomainsResolve()
        {
            var node = ModelLoader.Load("bellerophon.json", TwoTables).Model!.FindTable("Node")!;

            var description = node.FindColumn("Description")!;
            Assert.True(description.IsNullable);
            Assert.Equal("System.String", description.ClrType);
            var name = node.FindColumn("Name")!;
            Assert.False(name.IsNullable);
            Assert.Equal(100, name.MaxLength);
        }

        /// <summary>
        /// Key columns are immutable.  The client always supplies them; there is no key generation to declare.
        /// </summary>
        [Fact]
        public void KeysAreImmutableAndNeverGenerated()
        {
            var model = ModelLoader.Load("bellerophon.json", TwoTables).Model!;
            var withGeneration = TwoTables.Replace("\"NodeId\": \"guid\"", "\"NodeId\": { \"keyGeneration\": \"Server\", \"type\": \"guid\" }");

            Assert.True(model.FindTable("Node")!.FindColumn("NodeId")!.IsImmutable);
            Assert.Contains(ModelLoader.Load("bellerophon.json", withGeneration).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownProperty);
        }

        /// <summary>
        /// Tables and columns come out in alphabetical order whatever order the file uses.
        /// </summary>
        [Fact]
        public void TablesAndColumnsAreCanonicallyOrdered()
        {
            var node = ModelLoader.Load("bellerophon.json", TwoTables).Model!;

            Assert.Equal(new[] { "Account", "Node", "User" }, node.Tables.Select(table => table.Name).ToArray());
            Assert.Equal(new[] { "Description", "Name", "NodeId", "TenantId", "RowVersion" }, node.FindTable("Node")!.Columns.Select(column => column.Name).ToArray());
        }

        /// <summary>
        /// A table without its own access inherits the model default; a table's own access overrides it.
        /// </summary>
        [Fact]
        public void TableAccessOverridesTheDefault()
        {
            var text = TwoTables.Replace(
                "\"Account\": {",
                "\"Account\": {\n      \"access\": { \"read\": [\"Accounts.Read\"], \"write\": [\"Accounts.Write\"] },");

            var model = ModelLoader.Load("bellerophon.json", text).Model!;

            Assert.Equal(new[] { "Accounts.Read" }, model.FindTable("Account")!.Access.Read.ToArray());
            Assert.Equal(new[] { "Ledger.Read" }, model.FindTable("Node")!.Access.Read.ToArray());
        }

        /// <summary>
        /// Every table needs access for reads and writes, from the model default or its own declaration.
        /// </summary>
        [Fact]
        public void TableWithoutAccessIsAnError()
        {
            var text = TwoTables.Replace("\"access\": { \"read\": [\"Ledger.Read\"], \"write\": [\"Ledger.Write\"] },", string.Empty);

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.Id == DiagnosticIds.MissingAccess));
        }

        /// <summary>
        /// A reference to an undeclared table is reported where the table name is written.
        /// </summary>
        [Fact]
        public void UnknownReferenceTableIsReportedWhereItIsWritten()
        {
            var text = TwoTables.Replace("\"Node\": { \"columns\": [\"AccountId\"] }", "\"Node\": { \"columns\": [\"AccountId\"], \"table\": \"Nodes\" }");

            var result = ModelLoader.Load("bellerophon.json", text);

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(DiagnosticIds.UnknownTable, diagnostic.Id);
            Assert.Equal(13, diagnostic.Location.StartLine);
            Assert.Contains("'Nodes'", diagnostic.Message);
        }

        /// <summary>
        /// A column naming a datatype that is not in the dictionary is reported.
        /// </summary>
        [Fact]
        public void UnknownDataTypeIsAnError()
        {
            var text = TwoTables.Replace("\"Name\": \"name\"", "\"Name\": \"nam\"");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownDataType);
        }

        /// <summary>
        /// TenantId is implicit and cannot be declared.
        /// </summary>
        [Fact]
        public void DeclaringTenantIdIsAnError()
        {
            var text = TwoTables.Replace("\"columns\": { \"AccountId\": \"guid\" }", "\"columns\": { \"AccountId\": \"guid\", \"TenantId\": \"guid\" }");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ReservedName);
        }

        /// <summary>
        /// A synced table may not reference an unsynced one, or the client could not resolve the reference.
        /// </summary>
        [Fact]
        public void SyncedTableCannotReferenceUnsyncedTable()
        {
            var text = TwoTables.Replace("\"key\": [\"NodeId\"]", "\"key\": [\"NodeId\"],\n      \"sync\": false");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.SyncedReferencesUnsynced);
        }

        /// <summary>
        /// Decimals must declare precision and scale.
        /// </summary>
        [Fact]
        public void DecimalWithoutPrecisionIsAnError()
        {
            var text = TwoTables
                .Replace("\"guid\": \"System.Guid\",", "\"guid\": \"System.Guid\",\n    \"money\": \"System.Decimal\",")
                .Replace("\"Name\": \"name\",", "\"Balance\": \"money\", \"Name\": \"name\",");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.SizeRule);
        }

        /// <summary>
        /// Every model has the built-in User table, with its fixed shape and scopes, whether or not the file mentions it.
        /// </summary>
        [Fact]
        public void BuiltInUserTableIsAlwaysPresent()
        {
            var user = ModelLoader.Load("bellerophon.json", TwoTables).Model!.FindTable("User")!;

            Assert.True(user.IsBuiltIn);
            Assert.Equal(new[] { "IdentityIssuer", "IdentitySubject", "UserId", "TenantId", "RowVersion" }, user.Columns.Select(column => column.Name).ToArray());
            Assert.Equal(new[] { "Users.Read" }, user.Access.Read.ToArray());
            Assert.Equal(new[] { "Users.Write" }, user.Access.Write.ToArray());
            Assert.Equal(new[] { "IdentityIssuer", "IdentitySubject" }, Assert.Single(user.Uniques).Columns.ToArray());
            Assert.Equal(new PrincipalModel("IdentityIssuer", "IdentitySubject"), user.Principal);
        }

        /// <summary>
        /// User is reserved; a model cannot declare it.
        /// </summary>
        [Fact]
        public void DeclaringUserIsAnError()
        {
            var text = TwoTables.Replace("\"Node\": {\n      \"columns\"", "\"User\": { \"columns\": { \"Id\": \"guid\" }, \"key\": [\"Id\"] },\n    \"Node\": {\n      \"columns\"");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ReservedName && diagnostic.Message.Contains("built in"));
        }

        /// <summary>
        /// Model tables can reference the built-in User table, for example to add domain columns to users.
        /// </summary>
        [Fact]
        public void TablesCanReferenceTheBuiltInUser()
        {
            var text = TwoTables.Replace(
                "\"Node\": {\n      \"columns\"",
                "\"Member\": { \"columns\": { \"UserId\": \"guid\" }, \"key\": [\"UserId\"], \"references\": { \"User\": { \"columns\": [\"UserId\"] } } },\n    \"Node\": {\n      \"columns\"");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        }

        /// <summary>
        /// Strings in a key must declare a maximum length.
        /// </summary>
        [Fact]
        public void StringKeyWithoutMaxLengthIsAnError()
        {
            var text = TwoTables.Replace("\"key\": [\"NodeId\"]", "\"key\": [\"Description\"]")
                .Replace("\"Description\": \"string?\"", "\"Description\": \"string\"");

            var result = ModelLoader.Load("bellerophon.json", text);

            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.SizeRule);
        }
    }
}
