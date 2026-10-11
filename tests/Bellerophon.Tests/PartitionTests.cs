// <copyright file="PartitionTests.cs" company="Tangent Atlas">
//    Copyright © Tangent Atlas. All rights reserved.
// </copyright>
// <author>Donald Airey</author>
namespace Bellerophon.Tests
{
    using System;
    using System.Data;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Microsoft.CodeAnalysis;
    using Xunit;

    /// <summary>
    /// Compiles the generated server code, loads it, and exercises a partition: keys, references, navigation, delete
    /// rules, optimistic versions, and unique constraints.
    /// </summary>
    public class PartitionTests
    {
        private static readonly Lazy<Assembly> Ledger = new Lazy<Assembly>(() => Compile(Fixture.Read("bellerophon.json")));

        /// <summary>
        /// Inserting rows links each child to its parent and each parent to its children.
        /// </summary>
        [Fact]
        public void InsertLinksParentsAndChildren()
        {
            var partition = NewPartition();
            var node = partition.InsertNode(NewNode("Smith Trust"));
            var account = partition.InsertAccount(Make("Account", ("AccountId", node.NodeId), ("AccountType", Enum("AccountType", "ManagedAccount"))));
            var symbol = partition.InsertSymbol(Make("Symbol", ("Scheme", "Custodian"), ("Code", "1"), ("NodeId", node.NodeId)));
            partition.InsertSymbol(Make("Symbol", ("Scheme", "Custodian"), ("Code", "2"), ("NodeId", node.NodeId)));

            Assert.Same(account, node.Account);
            Assert.Same(node, account.Node);
            Assert.Same(node, symbol.Node);
            Assert.Equal(2, (int)node.Symbols.Count);
        }

        /// <summary>
        /// A child whose parent does not exist is refused, and nothing changes.
        /// </summary>
        [Fact]
        public void MissingParentIsRefused()
        {
            var partition = NewPartition();

            Assert.Throws<ConstraintException>(() => partition.InsertAccount(Make("Account", ("AccountId", Guid.NewGuid()), ("AccountType", Enum("AccountType", "GroupAccount")))));
            Assert.Empty((System.Collections.IEnumerable)partition.Accounts);
        }

        /// <summary>
        /// A parent whose children block deletion stays in place, with its links intact.
        /// </summary>
        [Fact]
        public void BlockedDeleteChangesNothing()
        {
            var partition = NewPartition();
            var node = partition.InsertNode(NewNode("Smith Trust"));
            partition.InsertSymbol(Make("Symbol", ("Scheme", "Custodian"), ("Code", "1"), ("NodeId", node.NodeId)));

            Assert.Throws<ConstraintException>(() => partition.DeleteNode(node.NodeId, node.RowVersion));
            Assert.Same(node, partition.FindNode(node.NodeId));
            Assert.Single((System.Collections.IEnumerable)node.Symbols);
        }

        /// <summary>
        /// A cascading relation deletes the children with the parent.
        /// </summary>
        [Fact]
        public void CascadeDeleteRemovesChildren()
        {
            var model = Fixture.Read("bellerophon.json").Replace(
                "\"Symbol\": {\n      \"columns\": {\n        \"Code\": { \"maxLength\": 32, \"type\": \"string\" },\n        \"NodeId\": \"guid\",\n        \"Scheme\": { \"maxLength\": 32, \"type\": \"string\" }\n      },\n      \"key\": [\"Scheme\", \"Code\"],\n      \"references\": {\n        \"Node\": { \"columns\": [\"NodeId\"] }",
                "\"Symbol\": {\n      \"columns\": {\n        \"Code\": { \"maxLength\": 32, \"type\": \"string\" },\n        \"NodeId\": \"guid\",\n        \"Scheme\": { \"maxLength\": 32, \"type\": \"string\" }\n      },\n      \"key\": [\"Scheme\", \"Code\"],\n      \"references\": {\n        \"Node\": { \"columns\": [\"NodeId\"], \"onDelete\": \"Cascade\" }");
            Assert.Contains("Cascade", model);
            var assembly = Compile(model);
            var partition = NewPartition(assembly);
            var node = partition.InsertNode(NewNode("Smith Trust", assembly));
            partition.InsertSymbol(Make("Symbol", assembly, ("Scheme", "Custodian"), ("Code", "1"), ("NodeId", node.NodeId)));

            Assert.True(partition.DeleteNode(node.NodeId, node.RowVersion));
            Assert.Empty((System.Collections.IEnumerable)partition.Symbols);
        }

        /// <summary>
        /// Changing a reference moves the child between parents; a stale version is refused.
        /// </summary>
        [Fact]
        public void UpdateMovesChildrenAndChecksVersions()
        {
            var partition = NewPartition();
            var first = partition.InsertNode(NewNode("First"));
            var second = partition.InsertNode(NewNode("Second"));
            var symbol = partition.InsertSymbol(Make("Symbol", ("Scheme", "Custodian"), ("Code", "1"), ("NodeId", first.NodeId)));
            long version = symbol.RowVersion;

            Assert.Throws<DBConcurrencyException>(() => partition.UpdateSymbol(Make("Symbol", ("Scheme", "Custodian"), ("Code", "1"), ("NodeId", second.NodeId), ("RowVersion", version - 1))));
            partition.UpdateSymbol(Make("Symbol", ("Scheme", "Custodian"), ("Code", "1"), ("NodeId", second.NodeId), ("RowVersion", version)));

            Assert.Same(second, symbol.Node);
            Assert.Empty((System.Collections.IEnumerable)first.Symbols);
            Assert.Single((System.Collections.IEnumerable)second.Symbols);
            Assert.True((long)symbol.RowVersion > version);
        }

        /// <summary>
        /// An identity can be bound to only one user per tenant, and membership is read from the User table.
        /// </summary>
        [Fact]
        public void IdentityIsUniqueAndDefinesMembership()
        {
            dynamic ledger = Activator.CreateInstance(Ledger.Value.GetType("AtlasLedger.Model.Ledger")!)!;
            var tenant = Guid.NewGuid();
            var partition = ledger.GetPartition(tenant);
            partition.InsertUser(Make("User", ("UserId", Guid.NewGuid()), ("IdentityIssuer", "issuer"), ("IdentitySubject", "person")));

            Assert.Throws<ConstraintException>(() => partition.InsertUser(Make("User", ("UserId", Guid.NewGuid()), ("IdentityIssuer", "issuer"), ("IdentitySubject", "person"))));
            Assert.True((bool)ledger.IsMember(tenant, "issuer", "person"));
            Assert.False((bool)ledger.IsMember(Guid.NewGuid(), "issuer", "person"));
        }

        private static dynamic NewPartition(Assembly? assembly = null)
        {
            dynamic ledger = Activator.CreateInstance((assembly ?? Ledger.Value).GetType("AtlasLedger.Model.Ledger")!)!;
            return ledger.GetPartition(Guid.NewGuid());
        }

        private static dynamic NewNode(string name, Assembly? assembly = null) => Make(
            "Node",
            assembly ?? Ledger.Value,
            ("NodeId", Guid.NewGuid()),
            ("DisplayName", name),
            ("FullName", name),
            ("NodeStatus", Enum("NodeStatus", "Active", assembly)),
            ("NodeType", Enum("NodeType", "Account", assembly)));

        private static object Enum(string type, string value, Assembly? assembly = null) =>
            System.Enum.Parse((assembly ?? Ledger.Value).GetType($"AtlasLedger.Model.{type}")!, value);

        private static dynamic Make(string type, params (string Name, object Value)[] values) => Make(type, Ledger.Value, values);

        private static dynamic Make(string type, Assembly assembly, params (string Name, object Value)[] values)
        {
            var rowType = assembly.GetType($"AtlasLedger.Model.Primary.{type}")!;
            var row = Activator.CreateInstance(rowType)!;
            foreach (var (name, value) in values)
            {
                rowType.GetProperty(name)!.SetValue(row, value);
            }

            return row;
        }

        private static Assembly Compile(string model)
        {
            var run = GeneratorTests.Run(model, "Primary", includeEnums: true);
            Assert.Empty(run.Result.Diagnostics);
            using var stream = new MemoryStream();
            var result = run.Output.Emit(stream);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
            return Assembly.Load(stream.ToArray());
        }
    }
}
