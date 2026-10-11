// <copyright file="GeneratorTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
extern alias generator;

namespace Bellerophon.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Text;
    using Xunit;
    using BellerophonGenerator = generator::Bellerophon.Generator.BellerophonGenerator;

    /// <summary>
    /// Runs the source generator in memory with Roslyn's generator driver, the way the compiler runs it during a build.
    /// </summary>
    public class GeneratorTests
    {
        private static readonly string[] Tables = { "Account", "Alert", "Dossier", "Edge", "Member", "Node", "Person", "Symbol", "User" };

        /// <summary>
        /// The tables whose rows become facts: every durable table except Member (marked "facts": false) and the built-in
        /// User, plus the latest-value Price table.
        /// </summary>
        private static readonly string[] FactTables = { "Account", "Alert", "Dossier", "Edge", "Node", "Person", "Price", "Symbol" };

        /// <summary>
        /// The Primary role generates a row, a store interface, and a controller per table, plus the root class, the access
        /// policies, the tenant picker, and the contract check, and the result compiles without warnings.
        /// </summary>
        [Fact]
        public void PrimaryRoleGeneratesCompilableCode()
        {
            var run = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true);

            var expected = new List<string>
            {
                "Ledger.g.cs",
                "Ledger.Primary.LedgerAccessPolicies.g.cs",
                "Ledger.Primary.LedgerApplicationBuilderExtensions.g.cs",
                "Ledger.Primary.LedgerMembership.g.cs",
                "Ledger.Primary.LedgerPartition.g.cs",
                "Ledger.Primary.LedgerServiceCollectionExtensions.g.cs",
                "Ledger.Primary.LedgerStore.g.cs",
                "Ledger.Primary.LedgerTenantsController.g.cs",
                "Ledger.Primary.LedgerTransaction.g.cs",
                "Ledger.Primary.LedgerBatchController.g.cs",
                "Ledger.Primary.LedgerCut.g.cs",
                "Ledger.Primary.LedgerChanges.g.cs",
                "Ledger.Primary.Price.g.cs",
            };
            foreach (var table in Tables)
            {
                expected.Add($"Ledger.Primary.{table}.g.cs");
                expected.Add($"Ledger.Primary.{table}Dto.g.cs");
                expected.Add($"Ledger.Primary.I{table}Store.g.cs");
                expected.Add($"Ledger.Primary.{table}Controller.g.cs");
            }

            foreach (var table in FactTables)
            {
                expected.Add($"Ledger.Primary.{table}Fact.g.cs");
            }

            Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(), HintNames(run));
            Assert.Empty(run.Result.Diagnostics);
            Assert.Empty(run.Output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        }

        /// <summary>
        /// The root class is named after the model and carries its version and fingerprint.
        /// </summary>
        [Fact]
        public void RootClassIsNamedAfterTheModel()
        {
            var run = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true);

            var root = Source(run, "Ledger.g.cs");
            Assert.Contains("public partial class Ledger", root);
            Assert.Contains("public const string Version = \"1.0\";", root);
            Assert.Contains("public const string Fingerprint = \"", root);
        }

        /// <summary>
        /// Controllers carry the major version in their route, require an authenticated caller, and require the table's
        /// policy on every action; every policy includes the tenant membership requirement.
        /// </summary>
        [Fact]
        public void ControllersAndPoliciesEnforceAccess()
        {
            var run = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true);

            var controller = Source(run, "Ledger.Primary.DossierController.g.cs");
            Assert.Contains("Route(\"v1/tenants/{tenantId:guid}/dossier\")", controller);
            Assert.Contains("[global::Microsoft.AspNetCore.Authorization.Authorize]", controller);
            Assert.Equal(2, Count(controller, "LedgerAccessPolicies.DossierRead"));
            Assert.Equal(3, Count(controller, "LedgerAccessPolicies.DossierWrite"));
            var policies = Source(run, "Ledger.Primary.LedgerAccessPolicies.g.cs");
            Assert.Contains("AddLedgerAccessPolicies(", policies);
            Assert.Contains("Add(options, DossierRead, \"Dossier.Read\");", policies);
            Assert.Contains("Add(options, UserWrite, \"Users.Write\");", policies);
            Assert.Contains("TenantMembershipRequirement.Instance", policies);
            Assert.Contains("Route(\"v1/me/tenants\")", Source(run, "Ledger.Primary.LedgerTenantsController.g.cs"));
        }

        /// <summary>
        /// The Replica role (the client data set) omits unsynced tables, identity columns, and every server piece, adds the
        /// contract headers, and carries the same fingerprint as the Primary role.
        /// </summary>
        [Fact]
        public void ReplicaRoleIsTheClientDataSet()
        {
            var master = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true);
            var run = Run(Fixture.Read("bellerophon.json"), "Replica", includeEnums: true);

            var expected = new List<string> { "Ledger.g.cs", "Ledger.Replica.LedgerPartition.g.cs", "Ledger.Replica.LedgerTransaction.g.cs" };
            foreach (var table in Tables.Where(table => table != "Dossier"))
            {
                expected.Add($"Ledger.Replica.{table}.g.cs");
                expected.Add($"Ledger.Replica.{table}Dto.g.cs");
            }

            Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(), HintNames(run));
            var user = Source(run, "Ledger.Replica.User.g.cs");
            Assert.Contains("namespace AtlasLedger.Model.Replica", user);
            Assert.Contains("IdentitySubject", user);
            var root = Source(run, "Ledger.g.cs");
            Assert.Contains("AddContractHeaders", root);
            Assert.Equal(FingerprintOf(Source(master, "Ledger.g.cs")), FingerprintOf(root));
            Assert.Empty(run.Output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        }

        /// <summary>
        /// Model errors surface as compiler errors located in the model file, and no code is generated.
        /// </summary>
        [Fact]
        public void ModelErrorsAreReportedInTheModelFile()
        {
            var text = Fixture.Read("bellerophon.json").Replace("\"Email\": \"email\"", "\"Email\": \"emial\"");

            var run = Run(text, "Primary", includeEnums: true);

            var diagnostic = Assert.Single(run.Result.Diagnostics);
            Assert.Equal("BEL2001", diagnostic.Id);
            Assert.EndsWith("bellerophon.json", diagnostic.Location.GetLineSpan().Path);
            Assert.Empty(run.Result.GeneratedTrees);
        }

        /// <summary>
        /// A project has exactly one model file; a second is an error and nothing is generated.
        /// </summary>
        [Fact]
        public void SecondModelFileIsAnError()
        {
            var text = Fixture.Read("bellerophon.json");

            var run = Run(text, "Primary", includeEnums: true, secondModelPath: "/other/bellerophon.json");

            var diagnostic = Assert.Single(run.Result.Diagnostics);
            Assert.Equal("BEL0002", diagnostic.Id);
            Assert.Empty(run.Result.GeneratedTrees);
        }

        /// <summary>
        /// A datatype whose CLR type is missing from the compilation is reported at its dictionary entry.
        /// </summary>
        [Fact]
        public void UnresolvedClrTypeIsReportedAtItsDatatype()
        {
            var run = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: false);

            var diagnostics = run.Result.Diagnostics.Where(diagnostic => diagnostic.Id == "BEL2007").ToList();
            Assert.Equal(4, diagnostics.Count);
            Assert.Contains(diagnostics, diagnostic => diagnostic.GetMessage().Contains("AtlasLedger.Model.AccountType") && diagnostic.Location.GetLineSpan().StartLinePosition.Line == 3);
            Assert.Empty(run.Result.GeneratedTrees);
        }

        /// <summary>
        /// Rows carry navigation in both directions, named by the convention: the reference's name toward the parent; the
        /// child table's plural (or its name, for a one-to-one extension) toward the children, decorated when the
        /// reference's name differs from the parent's.
        /// </summary>
        [Fact]
        public void RowsCarryNavigationProperties()
        {
            var run = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true);

            var node = Source(run, "Ledger.Primary.Node.g.cs");
            Assert.Contains("public global::AtlasLedger.Model.Primary.Account? Account { get; internal set; }".Replace("global::AtlasLedger.Model.Primary.", string.Empty), node);
            Assert.Contains("IReadOnlyCollection<Symbol> Symbols =>", node);
            Assert.Contains("IReadOnlyCollection<Edge> EdgesByParent =>", node);
            Assert.Contains("IReadOnlyCollection<Edge> EdgesByChild =>", node);
            Assert.Contains("public Node Node { get; internal set; } = default!;", Source(run, "Ledger.Primary.Symbol.g.cs"));
            Assert.Contains("public Person? Person { get; internal set; }", Source(run, "Ledger.Primary.Member.g.cs"));
        }

        /// <summary>
        /// Each table has one data transfer object, {Table}Dto in the model's root namespace: every column, keys and identity
        /// included, plus the version, never the tenant.  Server and client generate exactly the same type, so what one
        /// exports the other can import.
        /// </summary>
        [Fact]
        public void EachTableHasOneDataTransferObject()
        {
            var server = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true);
            var client = Run(Fixture.Read("bellerophon.json"), "Replica", includeEnums: true);

            var user = Source(server, "Ledger.Primary.UserDto.g.cs");
            Assert.Contains("namespace AtlasLedger.Model\n", user.Replace("\r\n", "\n"));
            Assert.Contains("public sealed partial class UserDto", user);
            Assert.Contains("UserId", user);
            Assert.Contains("IdentitySubject", user);
            Assert.Contains("RowVersion", user);
            Assert.DoesNotContain("TenantId", user);
            Assert.DoesNotContain(HintNames(server), name => name.Contains("Request") || name.Contains("Response"));
            foreach (var table in Tables.Where(table => table != "Dossier"))
            {
                Assert.Equal(
                    Source(server, $"Ledger.Primary.{table}Dto.g.cs"),
                    Source(client, $"Ledger.Replica.{table}Dto.g.cs"));
            }
        }

        /// <summary>
        /// The fingerprint covers the members of the enumerations the model uses, which live outside the model file.
        /// </summary>
        [Fact]
        public void EnumMembersChangeTheFingerprint()
        {
            var model = Fixture.Read("bellerophon.json");
            var enums = Fixture.Read("Enums.cs.txt");
            var reordered = enums.Replace("GroupAccount,", "WeightedAccount2,").Replace("WeightedAccount,", "GroupAccount,").Replace("WeightedAccount2,", "WeightedAccount,");
            var extended = enums.Replace("Active,", "Active,\n        Closed,");
            Assert.NotEqual(enums, extended);

            var original = FingerprintOf(Source(Run(model, "Primary", includeEnums: true), "Ledger.g.cs"));

            Assert.Equal(original, FingerprintOf(Source(Run(model, "Primary", includeEnums: true, enumsText: reordered), "Ledger.g.cs")));
            Assert.NotEqual(original, FingerprintOf(Source(Run(model, "Primary", includeEnums: true, enumsText: extended), "Ledger.g.cs")));
        }

        /// <summary>
        /// The partition's lock is the dedicated lock type; only .NET 10 is supported, so there is no fallback.
        /// </summary>
        [Fact]
        public void PartitionUsesTheDedicatedLockType()
        {
            var partition = Source(Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true), "Ledger.Primary.LedgerPartition.g.cs");

            Assert.Contains("private readonly global::System.Threading.Lock syncRoot = new global::System.Threading.Lock();", partition);
            Assert.DoesNotContain("#if", partition);
            Assert.Contains("HashSet", Source(Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true), "Ledger.Primary.Node.g.cs"));
        }

        /// <summary>
        /// The rules adapter is generated only for a primary-role project that references the rules library.
        /// </summary>
        [Fact]
        public void RulesAdapterRequiresTheRulesLibrary()
        {
            var without = HintNames(Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true));
            var with = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true, withRules: true);
            var replica = HintNames(Run(Fixture.Read("bellerophon.json"), "Replica", includeEnums: true, withRules: true));

            Assert.DoesNotContain(without, name => name.Contains("Rule"));
            Assert.Contains("Ledger.Primary.LedgerRulesTenant.g.cs", HintNames(with));
            Assert.Contains("Ledger.Primary.LedgerRuleActions.g.cs", HintNames(with));
            Assert.DoesNotContain(replica, name => name.Contains("Rule") || name.Contains("Fact"));
            Assert.Empty(with.Output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        }

        /// <summary>
        /// Facts are immutable and equal by key alone; tables marked "facts": false, and the built-in User, have none.
        /// </summary>
        [Fact]
        public void FactsAreImmutableAndEqualByKey()
        {
            var run = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true);

            var symbol = Source(run, "Ledger.Primary.SymbolFact.g.cs");
            Assert.Contains("public sealed partial class SymbolFact : global::System.IEquatable<SymbolFact>", symbol);
            Assert.DoesNotContain("set;", symbol);
            Assert.DoesNotContain("init;", symbol);
            Assert.Contains("this.Scheme, other.Scheme", symbol);
            Assert.Contains("this.Code, other.Code", symbol);
            Assert.DoesNotContain("this.NodeId, other.NodeId", symbol);
            Assert.DoesNotContain(HintNames(run), name => name == "Ledger.Primary.MemberFact.g.cs" || name == "Ledger.Primary.UserFact.g.cs");
        }

        /// <summary>
        /// The generated code needs C# 13: <c>lock</c> on a <see cref="System.Threading.Lock"/> takes that lock only from C# 13
        /// on.  A compiler older than C# 13 silently takes the object's monitor instead, so readers stop waiting for
        /// transactions (a current compiler set to C# 12 refuses with CS9202).  Either way, an older language version is one
        /// clear build error, and nothing is generated.
        /// </summary>
        [Fact]
        public void TheGeneratedCodeRequiresCSharp13()
        {
            var old = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true, languageVersion: LanguageVersion.CSharp12);
            var error = Assert.Single(old.Result.Diagnostics, diagnostic => diagnostic.Id == "BEL0003");
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("12.0", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Empty(old.Result.GeneratedTrees);

            var current = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true);
            Assert.DoesNotContain(current.Result.Diagnostics, diagnostic => diagnostic.Id == "BEL0003");
            Assert.NotEmpty(current.Result.GeneratedTrees);

            var noModel = CSharpGeneratorDriver.Create(new[] { new BellerophonGenerator().AsSourceGenerator() }, parseOptions: new CSharpParseOptions(LanguageVersion.CSharp12))
                .RunGenerators(CSharpCompilation.Create("NoModel"), TestContext.Current.CancellationToken).GetRunResult();
            Assert.Empty(noModel.Diagnostics);
        }

        /// <summary>
        /// The SQL Server writer and loader are generated only for a Primary project that references the SQL Server library,
        /// and they compile against it.
        /// </summary>
        [Fact]
        public void SqlDatabaseRequiresTheSqlServerLibrary()
        {
            var without = HintNames(Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true));
            var with = Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true, withSqlServer: true);
            var replica = HintNames(Run(Fixture.Read("bellerophon.json"), "Replica", includeEnums: true, withSqlServer: true));

            Assert.DoesNotContain(without, name => name.Contains("Sql", StringComparison.Ordinal));
            Assert.Contains("Ledger.Primary.LedgerSqlDatabase.g.cs", HintNames(with));
            Assert.DoesNotContain(replica, name => name.Contains("Sql", StringComparison.Ordinal));
            Assert.Empty(with.Output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning && diagnostic.Id != "CS1701"));

            var database = Source(with, "Ledger.Primary.LedgerSqlDatabase.g.cs");
            Assert.DoesNotContain("MERGE", database);
            Assert.DoesNotContain("FOREIGN KEY", database);
            Assert.DoesNotContain("UNIQUE", database);
            Assert.Contains("[dbo].[User]", database);
            Assert.Contains("[Rule]", database);
        }

        /// <summary>
        /// The table types the writer passes rows in, and the Entity Framework schema the migrations create, give every column
        /// the same SQL Server type, nullability included.
        /// </summary>
        [Fact]
        public void TheWriterAndTheSchemaAgreeOnEveryColumn()
        {
            var database = Source(Run(Fixture.Read("bellerophon.json"), "Primary", includeEnums: true, withSqlServer: true), "Ledger.Primary.LedgerSqlDatabase.g.cs");
            var schemaRun = Run(Fixture.Read("bellerophon.json"), "Schema", includeEnums: true, withEntityFramework: true);
            Assert.Empty(schemaRun.Output.GetDiagnostics(TestContext.Current.CancellationToken).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning && diagnostic.Id != "CS1701"));
            var schema = Source(schemaRun, "Ledger.Schema.LedgerDbContext.g.cs");

            var written = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match type in System.Text.RegularExpressions.Regex.Matches(database, @"CREATE TYPE \[bellerophon\]\.\[(\w+)Rows_\w+\] AS TABLE \((.*?), PRIMARY KEY"))
            {
                foreach (System.Text.RegularExpressions.Match column in System.Text.RegularExpressions.Regex.Matches(type.Groups[2].Value, @"\[(\w+)\] ([\w(),]+) (NOT NULL|NULL)"))
                {
                    written[$"{type.Groups[1].Value}.{column.Groups[1].Value}"] = $"{column.Groups[2].Value} {column.Groups[3].Value}";
                }
            }

            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match entity in System.Text.RegularExpressions.Regex.Matches(schema, @"modelBuilder\.Entity<(\w+)>\(entity =>(.*?)\}\);", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                foreach (System.Text.RegularExpressions.Match property in System.Text.RegularExpressions.Regex.Matches(entity.Groups[2].Value, @"row\.(\w+)\)\.HasColumnType\(""([^""]+)""\)([^;]*);"))
                {
                    if (property.Groups[1].Value == "TenantId")
                    {
                        continue;
                    }

                    var nullable = property.Groups[3].Value.Contains("IsRequired(false)", StringComparison.Ordinal) ? "NULL" : "NOT NULL";
                    stored[$"{entity.Groups[1].Value}.{property.Groups[1].Value}"] = $"{property.Groups[2].Value} {nullable}";
                }
            }

            Assert.NotEmpty(written);
            Assert.Equal(written.OrderBy(pair => pair.Key, StringComparer.Ordinal), stored.Where(pair => written.ContainsKey(pair.Key)).OrderBy(pair => pair.Key, StringComparer.Ordinal));
            Assert.Equal(written.Keys.OrderBy(key => key, StringComparer.Ordinal), stored.Keys.Where(key => !key.StartsWith("LedgerWatermark", StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal));
            Assert.Equal("nvarchar(16) NOT NULL", written["Node.DisplayName"]);
            Assert.Equal("nvarchar(max) NULL", written["Node.Description"]);
            Assert.Equal("int NOT NULL", written["Node.NodeStatus"]);
            Assert.Equal("bigint NOT NULL", written["Node.RowVersion"]);
        }

        /// <summary>
        /// A durable column whose type has no SQL Server type is an error at its datatype entry, but only for a project that
        /// generates database code; an ephemeral column may use any type.
        /// </summary>
        [Fact]
        public void ColumnsWithoutASqlServerTypeAreReported()
        {
            var model = Fixture.Read("bellerophon.json")
                .Replace("\"guid\": \"System.Guid\",", "\"duration\": \"System.TimeSpan\", \"guid\": \"System.Guid\",", StringComparison.Ordinal);
            var durable = model.Replace("\"Description\": \"string?\",", "\"Description\": \"string?\", \"Hold\": \"duration?\",", StringComparison.Ordinal);
            var ephemeral = model.Replace("\"AsOf\": \"time\",", "\"AsOf\": \"time\", \"Hold\": \"duration?\",", StringComparison.Ordinal);
            Assert.NotEqual(model, durable);
            Assert.NotEqual(model, ephemeral);

            var refused = Run(durable, "Primary", includeEnums: true, withSqlServer: true);
            var error = Assert.Single(refused.Result.Diagnostics, diagnostic => diagnostic.Id == "BEL2016");
            Assert.Equal(DiagnosticSeverity.Error, error.Severity);
            Assert.Contains("Node.Hold", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            Assert.DoesNotContain(HintNames(refused), name => name.Contains("Sql", StringComparison.Ordinal));

            Assert.DoesNotContain(Run(durable, "Primary", includeEnums: true).Result.Diagnostics, diagnostic => diagnostic.Id == "BEL2016");
            Assert.DoesNotContain(Run(ephemeral, "Primary", includeEnums: true, withSqlServer: true).Result.Diagnostics, diagnostic => diagnostic.Id == "BEL2016");
            Assert.Single(Run(durable, "Schema", includeEnums: true, withEntityFramework: true).Result.Diagnostics, diagnostic => diagnostic.Id == "BEL2016");
        }

        /// <summary>
        /// The Schema role generates only the Entity Framework model (a context and one entity per durable table, unsynced
        /// ones included), and needs Entity Framework Core.
        /// </summary>
        [Fact]
        public void TheSchemaRoleGeneratesOnlyTheEntityFrameworkModel()
        {
            var schema = Run(Fixture.Read("bellerophon.json"), "Schema", includeEnums: true, withEntityFramework: true);
            Assert.Empty(schema.Result.Diagnostics);
            Assert.Equal(
                new[] { "Ledger.Schema.Account.g.cs", "Ledger.Schema.Alert.g.cs", "Ledger.Schema.Dossier.g.cs", "Ledger.Schema.Edge.g.cs", "Ledger.Schema.LedgerDbContext.g.cs", "Ledger.Schema.Member.g.cs", "Ledger.Schema.Node.g.cs", "Ledger.Schema.Person.g.cs", "Ledger.Schema.Symbol.g.cs", "Ledger.Schema.User.g.cs" },
                HintNames(schema));

            var missing = Run(Fixture.Read("bellerophon.json"), "Schema", includeEnums: true);
            Assert.Single(missing.Result.Diagnostics, diagnostic => diagnostic.Id == "BEL0004");
            Assert.Empty(missing.Result.GeneratedTrees);
        }

        private static string FingerprintOf(string root)
        {
            const string Marker = "Fingerprint = \"";
            var start = root.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
            return root.Substring(start, root.IndexOf('"', start) - start);
        }

        private static string[] HintNames((GeneratorDriverRunResult Result, Compilation Output) run) =>
            run.Result.GeneratedTrees.Select(tree => Path.GetFileName(tree.FilePath)).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        private static string Source((GeneratorDriverRunResult Result, Compilation Output) run, string hintName) =>
            run.Result.GeneratedTrees.Single(tree => Path.GetFileName(tree.FilePath) == hintName).ToString();

        private static int Count(string text, string value)
        {
            var count = 0;
            for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        internal static (GeneratorDriverRunResult Result, Compilation Output) Run(string modelText, string role, bool includeEnums, string? secondModelPath = null, string? enumsText = null, string? extraSource = null, bool withRules = false, LanguageVersion languageVersion = LanguageVersion.Latest, bool withSqlServer = false, bool withEntityFramework = false)
        {
            // The test process loads the rules library, the SQL Server library, and Entity Framework, so each is left out unless
            // the compilation is meant to reference it: referencing one is what makes Bellerophon generate the code for it.
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(path => withRules || !Path.GetFileName(path).StartsWith("Bellerophon.Rules", StringComparison.Ordinal))
                .Where(path => withSqlServer || !Path.GetFileName(path).StartsWith("Bellerophon.SqlServer", StringComparison.Ordinal))
                .Where(path => withEntityFramework || !Path.GetFileName(path).StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            // The same symbols a .NET 10 project defines, so generated code takes the same branches it takes in a real build.
            var parseOptions = new CSharpParseOptions(languageVersion, preprocessorSymbols: new[] { "NET", "NET9_0_OR_GREATER", "NET10_0_OR_GREATER" });
            var trees = new List<SyntaxTree>();
            if (includeEnums)
            {
                trees.Add(CSharpSyntaxTree.ParseText(enumsText ?? Fixture.Read("Enums.cs.txt"), parseOptions));
            }

            if (extraSource != null)
            {
                // Code written against the generated types, the way a user writes it.
                trees.Add(CSharpSyntaxTree.ParseText(extraSource, parseOptions));
            }
            var compilation = CSharpCompilation.Create(
                "GeneratorTest",
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

            var driver = CSharpGeneratorDriver.Create(
                generators: new[] { new BellerophonGenerator().AsSourceGenerator() },
                additionalTexts: secondModelPath == null
                    ? new AdditionalText[] { new InMemoryAdditionalText("/server/bellerophon.json", modelText) }
                    : new AdditionalText[] { new InMemoryAdditionalText("/server/bellerophon.json", modelText), new InMemoryAdditionalText(secondModelPath, modelText) },
                parseOptions: parseOptions,
                optionsProvider: new TestOptionsProvider(new Dictionary<string, string> { ["build_property.BellerophonRole"] = role }));

            var updated = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            return (updated.GetRunResult(), output);
        }

        private sealed class InMemoryAdditionalText : AdditionalText
        {
            private readonly SourceText text;

            public InMemoryAdditionalText(string path, string text)
            {
                this.Path = path;
                this.text = SourceText.From(text);
            }

            public override string Path { get; }

            public override SourceText GetText(CancellationToken cancellationToken = default) => this.text;
        }

        private sealed class TestOptionsProvider : AnalyzerConfigOptionsProvider
        {
            public TestOptionsProvider(Dictionary<string, string> globalOptions)
            {
                this.GlobalOptions = new TestOptions(globalOptions);
            }

            public override AnalyzerConfigOptions GlobalOptions { get; }

            public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => TestOptions.Empty;

            public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => TestOptions.Empty;
        }

        private sealed class TestOptions : AnalyzerConfigOptions
        {
            private readonly Dictionary<string, string> values;

            public TestOptions(Dictionary<string, string> values)
            {
                this.values = values;
            }

            public static TestOptions Empty { get; } = new TestOptions(new Dictionary<string, string>());

            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
            {
                if (this.values.TryGetValue(key, out var found))
                {
                    value = found;
                    return true;
                }

                value = null;
                return false;
            }
        }
    }
}
